#!/usr/bin/env python3
"""
convert_shaders.py - convert all D3D11-only Unity 2021.1.11f1 shaders (42 stock in
unity_builtin_extra + 12 game shaders in resources.assets/sharedassets0/sharedassets2)
into native Metal MSL shaders + patch globalgamemanagers for Metal, without a Unity editor.

Never modifies --src-data; writes converted files to --dst-dir.
"""
import argparse
from concurrent.futures import ProcessPoolExecutor
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile

import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)

from dxbc2msl_2021 import HLSLCC_FLAGS, build_names_from_trailing, dxbc_to_msl
from unity2021_blob import (
    D3D_TO_METAL,
    FS_TYPES,
    build_code_entry,
    build_metal_payload,
    compress_blob,
    decompress_blob,
    parse_blob,
    parse_code_entry,
    parse_trailing,
    rebuild_vs_trailing,
    serialize_blob,
)

SHADER_FILES = [
    "Resources/unity_builtin_extra",
    "resources.assets",
    "sharedassets0.assets",
    "sharedassets2.assets",
]

MTX_PREFIX = "hlslcc_mtx4x4"

TYPE_INFO = {
    "half": (2, 2), "half2": (4, 4), "half3": (8, 8), "half4": (8, 8),
    "packed_half2": (4, 2), "packed_half3": (6, 2), "packed_half4": (8, 2),
    "float": (4, 4), "int": (4, 4), "uint": (4, 4), "bool": (4, 4),
    "float2": (8, 8), "int2": (8, 8), "uint2": (8, 8),
    "float3": (16, 16), "int3": (16, 16), "uint3": (16, 16),
    "float4": (16, 16), "int4": (16, 16), "uint4": (16, 16),
    "packed_float2": (8, 4), "packed_int2": (8, 4), "packed_uint2": (8, 4),
    "packed_float3": (12, 4), "packed_int3": (12, 4), "packed_uint3": (12, 4),
    "packed_float4": (16, 4), "packed_int4": (16, 4), "packed_uint4": (16, 4),
}

PACKED = {
    "half2": "packed_half2", "half3": "packed_half3", "half4": "packed_half4",
    "float2": "packed_float2", "int2": "packed_int2", "uint2": "packed_uint2",
    "float3": "packed_float3", "int3": "packed_int3", "uint3": "packed_uint3",
    "float4": "packed_float4", "int4": "packed_int4", "uint4": "packed_uint4",
}

STRUCT_RE = re.compile(r"(struct\s+([\w$]+)\s*\{)(.*?)(\};)", re.S)
MEMBER_RE = re.compile(r"([\w$]+)\s+([\w$]+)\s*(?:\[(\d+)\])?\s*;")
BUF_RE = re.compile(r"constant\s+([\w$]+)\s*&\s*([\w$]+)\s*\[\[\s*buffer\((\d+)\)\s*\]\]")
TEX_DECL_RE = re.compile(r">\s*([\w$]+)\s*\[\[\s*texture\s*\((\d+)\)\s*\]\]")

PACKED_HITS: set[str] = set()


def _msl_member_size(ty: str, arr: int) -> tuple[int, int]:
    size, align = TYPE_INFO[ty]
    if arr:
        stride = (size + align - 1) & ~(align - 1)
        return stride * arr, align
    return size, align


def _emit_pads(lines: list[str], cur: int, target: int, tag: str, n: int) -> int:
    while cur < target:
        if cur % 16 == 0 and target - cur >= 16:
            k = (target - cur) // 16
            lines.append(
                f"    float4 pad_{tag}_{n}[{k}];" if k > 1 else f"    float4 pad_{tag}_{n};"
            )
            cur += 16 * k
        elif cur % 8 == 0 and target - cur >= 8:
            lines.append(f"    float2 pad_{tag}_{n};")
            cur += 8
        else:
            lines.append(f"    float pad_{tag}_{n};")
            cur += 4
        n += 1
    return n


def relayout_cb_structs(msl: str, offsets_by_slot: dict, tag_for_log: str) -> str:
    """Rebuild every constant buffer struct so named members sit at their exact
    serialized byteOffset (matching Unity's GpuProgramMetal::ApplyGpuProgram)."""
    structs = {m.group(2): m for m in STRUCT_RE.finditer(msl)}
    for bm in BUF_RE.finditer(msl):
        sname, inst, slot = bm.group(1), bm.group(2), int(bm.group(3))
        if slot not in offsets_by_slot:
            raise RuntimeError(f"{tag_for_log}: buffer({slot}) {inst} has no serialized CB")
        serial, cbsize = offsets_by_slot[slot]
        sm = structs[sname]
        placed, pads = [], []
        orig_off = 0
        for m in MEMBER_RE.finditer(sm.group(3)):
            ty, nm, arr = m.group(1), m.group(2), int(m.group(3)) if m.group(3) else 0
            if ty not in TYPE_INFO:
                raise RuntimeError(f"{tag_for_log}: MSL type {ty} unsupported")
            size, align = _msl_member_size(ty, arr)
            orig_off = (orig_off + align - 1) & ~(align - 1)
            key = nm[len(MTX_PREFIX):] if nm.startswith(MTX_PREFIX) else nm
            if key in serial:
                placed.append((serial[key], ty, nm, arr))
            elif re.search(r"\b" + re.escape(inst) + r"\s*\.\s*" + re.escape(nm) + r"\b", msl):
                pads.append((orig_off, ty, nm, arr))
            orig_off += size
        placed.sort()
        cbsize = max(cbsize, max((off + _msl_member_size(ty, arr)[0] for off, ty, _, arr in placed if arr or ty not in PACKED), default=0))
        spans = []
        repacked = []
        for i, (off, ty, nm, arr) in enumerate(placed):
            size, align = _msl_member_size(ty, arr)
            next_off = placed[i + 1][0] if i + 1 < len(placed) else cbsize
            if off % align or off + size > next_off:
                pty = None if arr else PACKED.get(ty)
                if pty is None or off % 4:
                    raise RuntimeError(f"{tag_for_log}: {nm}@{off} violates align {align} / next {next_off}")
                psize, palign = TYPE_INFO[pty]
                if off + psize > next_off and i + 1 < len(placed):
                    raise RuntimeError(f"{tag_for_log}: {nm}@{off}+{psize} overlaps {placed[i + 1][2]}@{next_off}")
                size, align = psize, palign
                repacked.append((nm, ty))
                ty = pty
                placed[i] = (off, ty, nm, arr)
            spans.append((off, off + size, ty, nm, arr))
        for a, b in zip(spans, spans[1:]):
            if a[1] > b[0]:
                raise RuntimeError(
                    f"{tag_for_log}: {a[3]} [{a[0]},{a[1]}) overlaps {b[3]}@{b[0]}"
                )
        cbsize = max(cbsize, max((s[1] for s in spans), default=0))
        gaps = []
        cur = 0
        for s in spans:
            if s[0] > cur:
                gaps.append((cur, s[0]))
            cur = max(cur, s[1])
        if cur < cbsize:
            gaps.append((cur, cbsize))

        def take(gi: int, o: int, sz: int) -> None:
            g0, g1 = gaps[gi]
            gaps[gi] = (o + sz, g1)
            if o > g0:
                gaps.insert(gi, (g0, o))

        for want, ty, nm, arr in pads:
            size, align = _msl_member_size(ty, arr)
            spot = None
            for gi, (g0, g1) in enumerate(gaps):
                if g0 <= want and want + size <= g1:
                    spot = want
                    take(gi, want, size)
                    break
            if spot is None:
                for gi, (g0, g1) in enumerate(gaps):
                    o = (g0 + align - 1) & ~(align - 1)
                    if o + size <= g1:
                        spot = o
                        take(gi, o, size)
                        break
            if spot is None:
                raise RuntimeError(
                    f"{tag_for_log}: no room for accessed pad {nm} ({size}B) within CB {cbsize}"
                )
            placed.append((spot, ty, nm, arr))
        placed.sort()

        lines = []
        cur = 0
        n = 0
        for off, ty, nm, arr in placed:
            if off > cur:
                n = _emit_pads(lines, cur, off, "fill", n)
            lines.append(f"    {ty} {nm}[{arr}];" if arr else f"    {ty} {nm};")
            cur = off + _msl_member_size(ty, arr)[0]
        body = "\n" + "\n".join(lines) + "\n"
        msl = msl.replace(sm.group(0), sm.group(1) + body + sm.group(4), 1)
        for nm, aty in repacked:
            msl = re.sub(
                r"\b" + re.escape(inst) + r"\s*\.\s*" + re.escape(nm) + r"\b",
                f"{aty}({inst}.{nm})",
                msl,
            )
            PACKED_HITS.add(f"{tag_for_log}:{inst}.{nm}@{aty}")
    return msl


def _compile_one_msl(item: tuple[str, str]) -> str | None:
    tag, msl = item
    with tempfile.NamedTemporaryFile(suffix=".metal", mode="w", delete=False) as tf:
        tf.write(msl)
        p = tf.name
    try:
        r = subprocess.run(
            ["xcrun", "metal", "-c", p, "-o", "/dev/null",
             "-std=macos-metal2.2", "-Wno-unused-variable"],
            capture_output=True,
            text=True,
        )
        if r.returncode != 0:
            return f"{tag}: xcrun metal failed:\n{r.stderr[:800]}"
    finally:
        if os.path.exists(p):
            os.unlink(p)
    return None


def convert_shader(d: dict, name: str, compile_items: list[tuple[str, str]] | None) -> str:
    plats = list(d.get("platforms", []))
    if plats != [4]:
        raise RuntimeError(f"{name}: expected platforms=[4], got {plats}")
    offs = d["offsets"][0]
    if isinstance(offs, list) and len(offs) != 1:
        raise RuntimeError(f"{name}: multi-segment blob ({len(offs)} segs) unsupported")

    pf = d["m_ParsedForm"]
    # Hidden/VR/BlitFromTex2DToTexArraySlice ships SubShader[0] (VS+FS) and SubShader[1] (GS fallback).
    # Metal does not support GS subprograms; keep only SubShader[0] and its entries.
    if name == "Hidden/VR/BlitFromTex2DToTexArraySlice" and len(pf.get("m_SubShaders", [])) > 1:
        pf["m_SubShaders"] = pf["m_SubShaders"][:1]

    blob = decompress_blob(d, 0)
    entries = parse_blob(blob)

    used_blobs = set()
    for ss in pf.get("m_SubShaders", []):
        for ps in ss.get("m_Passes", []):
            for key in ("progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing"):
                pr = ps.get(key) or {}
                for sp in pr.get("m_SubPrograms", []):
                    used_blobs.add(sp["m_BlobIndex"])

    if name == "Hidden/VR/BlitFromTex2DToTexArraySlice":
        max_idx = max(used_blobs)
        entries = entries[:max_idx + 1]

    n_vs = n_fs = 0
    new_channels_by_blob: dict[int, dict | None] = {}
    type_counts: dict[int, int] = {}

    for bi in sorted(used_blobs):
        e = entries[bi]
        gtype = e["type"]
        if gtype not in D3D_TO_METAL:
            raise RuntimeError(f"{name}: entry[{bi}] unsupported type {gtype}")
        pe = parse_code_entry(e["raw"])
        pt = parse_trailing(pe["trailing"])
        names, offsets = build_names_from_trailing(pt)
        tag = f"{name} e[{bi}]"
        msl, _ = dxbc_to_msl(pe["payload"], names, flags=HLSLCC_FLAGS)
        msl = relayout_cb_structs(msl, offsets, tag)

        # Verify texture slot identity through HLSLcc.
        tex_by_name = {v: k for k, v in names["textures"].items()}
        for m in TEX_DECL_RE.finditer(msl):
            tn, ti = m.group(1), int(m.group(2))
            reg = tex_by_name.get(tn)
            if reg is not None and reg != ti:
                raise RuntimeError(f"{tag}: texture {tn} reg {reg} != MSL {ti}")

        if compile_items is not None:
            compile_items.append((tag, msl))

        if gtype in FS_TYPES:
            trailing = pe["trailing"]
            n_fs += 1
        else:
            trailing, new_ch = rebuild_vs_trailing(pe["trailing"], pt, msl, tag)
            new_channels_by_blob[bi] = new_ch
            n_vs += 1

        new_gtype = D3D_TO_METAL[gtype]
        type_counts[gtype] = type_counts.get(gtype, 0) + 1
        payload = build_metal_payload(msl)
        new_raw = build_code_entry(
            new_gtype,
            (0, 0, 0, 0),
            pe["global_kws"],
            pe["local_kws"],
            payload,
            trailing,
        )
        entries[bi] = {"raw": new_raw, "seg": e["seg"], "type": new_gtype}

    # Update m_SubPrograms typetree (m_GpuProgramType and VS m_Channels).
    for ss in pf.get("m_SubShaders", []):
        for ps in ss.get("m_Passes", []):
            for key in ("progVertex", "progFragment"):
                pr = ps.get(key) or {}
                for sp in pr.get("m_SubPrograms", []):
                    t = sp["m_GpuProgramType"]
                    if t in D3D_TO_METAL:
                        sp["m_GpuProgramType"] = D3D_TO_METAL[t]
                    if key == "progVertex":
                        ch = new_channels_by_blob.get(sp["m_BlobIndex"])
                        if ch is not None:
                            sp["m_Channels"] = ch

    new_blob = serialize_blob(entries)
    comp = compress_blob(new_blob)
    d["platforms"] = [14]
    d["offsets"] = [[0]]
    d["compressedLengths"] = [[len(comp)]]
    d["decompressedLengths"] = [[len(new_blob)]]
    d["compressedBlob"] = list(comp)
    return (
        f"{n_vs} VS + {n_fs} FS -> Metal; types={type_counts}; "
        f"blob {len(blob)} -> {len(new_blob)}B raw / {len(comp)}B lz4"
    )


def patch_ggm_metal(src_ggm: str, dst_ggm: str) -> list[tuple[str, str]]:
    """Copy globalgamemanagers and apply the two 4-byte surgical Metal patches."""
    os.makedirs(os.path.dirname(dst_ggm), exist_ok=True)
    b = bytearray(open(src_ggm, "rb").read())
    changed = []

    V = struct.pack("<I", 142984712)
    sig = struct.pack("<I", 4) + struct.pack("<I", 2) + V + b"\x00\x00\x00\x00" + struct.pack("<I", 2) + V
    i = b.find(sig)
    if i < 0:
        raise RuntimeError("shaderPlatform signature not found in globalgamemanagers")
    assert struct.unpack_from("<I", b, i)[0] == 4
    b[i:i + 4] = struct.pack("<I", 14)
    changed.append((f"shaderPlatform@{i}", "4->14"))

    env = UnityPy.load(src_ggm)
    bs = [o for o in env.objects if getattr(o.type, "name", None) == "BuildSettings"]
    if not bs:
        raise RuntimeError("no BuildSettings object in globalgamemanagers")
    o = bs[0]
    obj = bytes(b[o.byte_start:o.byte_start + o.byte_size])
    tail = struct.pack("<ii", 1, 2)
    rel = obj.rfind(tail)
    if rel < 0 or rel + 8 != o.byte_size:
        raise RuntimeError("m_GraphicsAPIs [1,2] not at BuildSettings tail")
    gp = o.byte_start + rel + 4
    assert struct.unpack_from("<i", b, gp)[0] == 2
    b[gp:gp + 4] = struct.pack("<i", 16)
    changed.append((f"m_GraphicsAPIs@{gp}", "2->16 (Metal)"))

    with open(dst_ggm, "wb") as fh:
        fh.write(b)
    return changed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--src-data",
        default=os.path.join(ROOT, "extracted", "The House On Ash Tree Lane_Data"),
        help="Untouched Windows _Data directory",
    )
    ap.add_argument(
        "--dst-dir",
        default=os.path.join(ROOT, "build", "shaders", "metal_assets"),
        help="Output overlay directory for converted assets + patched globalgamemanagers",
    )
    ap.add_argument("--only", nargs="*", help="Only convert these shader names")
    ap.add_argument("--compile-check", action="store_true", help="Compile every MSL via xcrun metal")
    ap.add_argument("--jobs", type=int, default=8, help="Parallel xcrun metal workers")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    done = failed = 0
    compile_items: list[tuple[str, str]] | None = [] if args.compile_check else None

    for rel in SHADER_FILES:
        src_path = os.path.join(args.src_data, rel)
        env = UnityPy.load(src_path)
        changed = False
        for o in env.objects:
            if getattr(o.type, "name", None) != "Shader":
                continue
            d = o.read_typetree()
            name = d.get("m_ParsedForm", {}).get("m_Name", "")
            if args.only and name not in args.only:
                continue
            try:
                report = convert_shader(d, name, compile_items)
                print(f"== {name} ({rel} pid={o.path_id}): {report}")
                if not args.dry_run:
                    o.save_typetree(d)
                    changed = True
                done += 1
            except Exception as ex:
                print(f"!! {name} ({rel} pid={o.path_id}): {type(ex).__name__}: {ex}", file=sys.stderr)
                failed += 1
        if changed and not args.dry_run:
            data = env.file.save()  # materialize BEFORE writing
            dst_path = os.path.join(args.dst_dir, rel)
            os.makedirs(os.path.dirname(dst_path), exist_ok=True)
            with open(dst_path, "wb") as fh:
                fh.write(data)
            print(f"wrote {dst_path} ({len(data)}B)")

    if not args.dry_run and not args.only:
        src_ggm = os.path.join(args.src_data, "globalgamemanagers")
        dst_ggm = os.path.join(args.dst_dir, "globalgamemanagers")
        changes = patch_ggm_metal(src_ggm, dst_ggm)
        print(f"wrote {dst_ggm}: {changes}")

    if args.compile_check and compile_items:
        print(f"\nRunning xcrun metal compile check across {len(compile_items)} MSL shaders ({args.jobs} workers)...")
        with ProcessPoolExecutor(max_workers=args.jobs) as pool:
            errs = [e for e in pool.map(_compile_one_msl, compile_items, chunksize=16) if e]
        if errs:
            print(f"!! {len(errs)}/{len(compile_items)} MSL shader(s) failed xcrun metal:", file=sys.stderr)
            for e in errs[:10]:
                print(e, file=sys.stderr)
            return 1
        print(f"compile-check OK: {len(compile_items)}/{len(compile_items)} MSL shaders compiled cleanly.")

    if PACKED_HITS:
        print(f"\npacked_* re-layout used for {len(PACKED_HITS)} member(s):")
        for h in sorted(PACKED_HITS)[:20]:
            print(f"  {h}")
        if len(PACKED_HITS) > 20:
            print(f"  ... ({len(PACKED_HITS) - 20} more)")

    print(f"\n{done} shader(s) converted, {failed} failed" + (" [dry-run]" if args.dry_run else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
