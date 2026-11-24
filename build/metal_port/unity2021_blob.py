#!/usr/bin/env python3
"""
unity2021_blob.py - parse and build Unity 2021.1.11f1 shader blobs, code entries,
Metal 0xF00DCAFE payloads, and trailing ParserBindChannels + GpuProgramParameters.

Verified against `Resources/unity default resources` (which ships paired D3D11 plat=4
and Metal plat=14 entries for 2021.1.11f1) and `UnityPlayer.dylib` (`LoadGpuProgramFromData`
@ 0x65ee0c, `CreateMetalShaderShaderLab` @ 0xd26fe0, `CreateFromShaderLabSource` @ 0xd260a4).
"""
import re
import struct
import lz4.block

WRAPPER_MAGIC = 0x0C07513C
PAYLOAD_MAGIC = 0xF00DCAFE
ENTRY_NAME = b"xlatMtlMain"
NAME_OFFSET = 44
SRC_OFFSET = 56
BLOB_COUNT = 4
HEADER_FLAGS = 0x05

METAL_VS = 23
METAL_FS = 24
VS_TYPES = {15: METAL_VS, 16: METAL_VS}
FS_TYPES = {17: METAL_FS, 18: METAL_FS}
D3D_TO_METAL = {**VS_TYPES, **FS_TYPES}

CHAN_SEM = {
    0: ("POSITION", 0),
    1: ("NORMAL", 0),
    2: ("TANGENT", 0),
    3: ("COLOR", 0),
    4: ("TEXCOORD", 0),
    5: ("TEXCOORD", 1),
    6: ("TEXCOORD", 2),
    7: ("TEXCOORD", 3),
    8: ("TEXCOORD", 4),
    9: ("TEXCOORD", 5),
    10: ("TEXCOORD", 6),
    11: ("TEXCOORD", 7),
}
SEM_CHAN = {v: k for k, v in CHAN_SEM.items()}


def decompress_blob(shader_dict: dict, plat_index: int) -> bytes:
    """Decompress one platform's LZ4-block shader subprogram blob."""
    blob = bytes(shader_dict["compressedBlob"])
    offs = shader_dict["offsets"][plat_index]
    clens = shader_dict["compressedLengths"][plat_index]
    dlens = shader_dict["decompressedLengths"][plat_index]
    if isinstance(offs, int):
        offs, clens, dlens = [offs], [clens], [dlens]
    out = bytearray()
    for o, c, dl in zip(offs, clens, dlens):
        seg = blob[o:o + c]
        out += seg if c == dl else lz4.block.decompress(seg, uncompressed_size=dl)
    return bytes(out)


def compress_blob(raw_blob: bytes) -> bytes:
    """Compress a shader blob in Unity's raw LZ4 block format (no size prefix)."""
    return lz4.block.compress(raw_blob, mode="high_compression", store_size=False)


def parse_blob(blob: bytes) -> list[dict]:
    """Parse a decompressed Unity shader blob into [{raw, seg, type}, ...]."""
    n = struct.unpack_from("<I", blob, 0)[0]
    entries = []
    for i in range(n):
        o, l, s = struct.unpack_from("<III", blob, 4 + 12 * i)
        entries.append({
            "raw": blob[o:o + l],
            "seg": s,
            "type": struct.unpack_from("<i", blob, o + 4)[0],
        })
    return entries


def serialize_blob(entries: list[dict]) -> bytes:
    """Serialize [{raw, seg, ...}, ...] back into a raw Unity shader blob."""
    n = len(entries)
    table = bytearray(struct.pack("<I", n))
    off = 4 + 12 * n
    payloads = bytearray()
    for e in entries:
        raw = e["raw"]
        table += struct.pack("<III", off, len(raw), e["seg"])
        payloads += raw
        off += len(raw)
    return bytes(table + payloads)


def _read_kw_list(raw: bytes, pos: int) -> tuple[list[str], int]:
    cnt = struct.unpack_from("<I", raw, pos)[0]
    pos += 4
    kws = []
    for _ in range(cnt):
        l = struct.unpack_from("<I", raw, pos)[0]
        pos += 4
        kws.append(raw[pos:pos + l].decode("ascii"))
        pos += (l + 3) & ~3
    return kws, pos


def _write_kw_list(out: bytearray, kws: list[str]) -> None:
    out += struct.pack("<I", len(kws))
    for k in kws:
        kb = k.encode("ascii")
        out += struct.pack("<I", len(kb)) + kb
        while len(out) % 4:
            out += b"\0"


def parse_code_entry(raw: bytes) -> dict:
    """Parse one Unity 2021.1 subprogram entry (wrapper + global/local kws + payload + trailing)."""
    magic, gtype = struct.unpack_from("<II", raw, 0)
    if magic != WRAPPER_MAGIC:
        raise ValueError(f"bad wrapper magic {magic:#010x} (expected {WRAPPER_MAGIC:#010x})")
    stats = struct.unpack_from("<4I", raw, 8)
    pos = 24
    global_kws, pos = _read_kw_list(raw, pos)
    local_kws, pos = _read_kw_list(raw, pos)
    plen = struct.unpack_from("<I", raw, pos)[0]
    pos += 4
    payload = raw[pos:pos + plen]
    end_pos = pos + ((plen + 3) & ~3)
    trailing = raw[end_pos:]
    return {
        "type": gtype,
        "stats": stats,
        "global_kws": global_kws,
        "local_kws": local_kws,
        "payload": payload,
        "trailing": trailing,
    }


def build_code_entry(
    gtype: int,
    stats: tuple[int, int, int, int],
    global_kws: list[str],
    local_kws: list[str],
    payload: bytes,
    trailing: bytes,
) -> bytes:
    """Rebuild one Unity 2021.1 subprogram entry with 4-byte padded payload before trailing."""
    out = bytearray(struct.pack("<II4I", WRAPPER_MAGIC, gtype, *stats))
    _write_kw_list(out, global_kws)
    _write_kw_list(out, local_kws)
    out += struct.pack("<I", len(payload))
    out += payload
    while len(out) % 4:
        out += b"\0"
    out += trailing
    return bytes(out)


def build_metal_payload(msl_source: str) -> bytes:
    """Pack MSL source into Unity 2021.1's 44-byte 0xF00DCAFE Metal payload."""
    header = struct.pack(
        "<11I",
        PAYLOAD_MAGIC,
        NAME_OFFSET,
        BLOB_COUNT,
        SRC_OFFSET,
        0, 0, 0, 0, 0,
        HEADER_FLAGS,
        0xFFFFFFFF,
    )
    name = ENTRY_NAME + b"\0"
    assert len(header) == NAME_OFFSET
    assert NAME_OFFSET + len(name) == SRC_OFFSET
    return header + name + msl_source.encode("utf-8")


def parse_metal_payload(payload: bytes) -> dict:
    """Parse a Unity 2021.1 Metal 0xF00DCAFE payload for round-trip verification."""
    hdr = struct.unpack_from("<11I", payload, 0)
    if hdr[0] != PAYLOAD_MAGIC:
        raise ValueError(f"bad payload magic {hdr[0]:#010x}")
    name_off, count, src_off, flags = hdr[1], hdr[2], hdr[3], hdr[9]
    name = payload[name_off:payload.index(b"\0", name_off)].decode("ascii", "replace")
    source = payload[src_off:].decode("utf-8", "replace")
    return {
        "name_offset": name_off,
        "count": count,
        "src_offset": src_off,
        "flags": flags,
        "entry_name": name,
        "source": source,
    }


def _rd_str(b: bytes, off: int) -> tuple[str, int]:
    n = struct.unpack_from("<I", b, off)[0]
    s = b[off + 4:off + 4 + n].decode("ascii")
    return s, off + 4 + ((n + 3) & ~3)


def parse_trailing(trailing: bytes) -> dict:
    """Parse Unity 2021.1 trailing section: ParserBindChannels + GpuProgramParameters.

    Returns params_offset (start of GpuProgramParameters, which can be preserved verbatim),
    plus decoded channels, CBs, and resource bindings.
    """
    off = 0
    src_map, n_ch = struct.unpack_from("<II", trailing, off)
    off += 8
    chans = []
    for _ in range(n_ch):
        chans.append(struct.unpack_from("<II", trailing, off))
        off += 8
    params_offset = off

    n_cbs = struct.unpack_from("<I", trailing, off)[0]
    off += 4
    cbs = []
    for _ in range(n_cbs):
        cb_name, off = _rd_str(trailing, off)
        cb_size, n_params = struct.unpack_from("<II", trailing, off)
        off += 8
        params = []
        for _ in range(n_params):
            pname, off = _rd_str(trailing, off)
            ptype, rows, cols, is_mat, arr, poff = struct.unpack_from("<6I", trailing, off)
            off += 24
            params.append({
                "name": pname,
                "type": ptype,
                "rows": rows,
                "cols": cols,
                "is_mat": is_mat,
                "arr": arr,
                "offset": poff,
            })
        n_structs = struct.unpack_from("<I", trailing, off)[0]
        off += 4
        for _ in range(n_structs):
            _sname, off = _rd_str(trailing, off)
            _s_idx, _s_stride, _s_arr, n_mem = struct.unpack_from("<4I", trailing, off)
            off += 16
            for _ in range(n_mem):
                _mname, off = _rd_str(trailing, off)
                off += 24
        cbs.append({"name": cb_name, "size": cb_size, "params": params})

    n_binds = struct.unpack_from("<I", trailing, off)[0]
    off += 4
    binds = []
    for _ in range(n_binds):
        bname, off = _rd_str(trailing, off)
        bkind = struct.unpack_from("<I", trailing, off)[0]
        off += 4
        if bkind == 0:
            bidx, bsampler, bdim = struct.unpack_from("<3I", trailing, off)
            off += 12
            binds.append({
                "kind": "tex",
                "name": bname,
                "index": bidx,
                "sampler": bsampler,
                "dim": bdim,
            })
        elif bkind == 1:
            bidx, bextra = struct.unpack_from("<2I", trailing, off)
            off += 8
            binds.append({"kind": "cb", "name": bname, "index": bidx, "extra": bextra})
        elif bkind == 2:
            bidx = struct.unpack_from("<I", trailing, off)[0]
            off += 4
            binds.append({"kind": "buf", "name": bname, "index": bidx})
        elif bkind == 3:
            bidx, bextra = struct.unpack_from("<2I", trailing, off)
            off += 8
            binds.append({"kind": "uav", "name": bname, "index": bidx, "extra": bextra})
        elif bkind == 4:
            bidx = struct.unpack_from("<I", trailing, off)[0]
            off += 4
            binds.append({"kind": "smp", "name": bname, "index": bidx})
        else:
            raise ValueError(f"unknown trailing bind kind {bkind} at {off:#x}")

    if off != len(trailing):
        raise ValueError(f"trailing parse consumed {off}/{len(trailing)} bytes")

    return {
        "src_map": src_map,
        "chans": chans,
        "params_offset": params_offset,
        "cbs": cbs,
        "binds": binds,
    }


_ATTR_RE = re.compile(r"[\w$]+\s+([A-Z][A-Z_]*?)(\d*)\s*\[\[\s*attribute\((\d+)\)\s*\]\]")


def rebuild_vs_trailing(
    trailing: bytes,
    parsed_trailing: dict,
    msl: str,
    tag: str,
) -> tuple[bytes, dict | None]:
    """Rebuild VS ParserBindChannels for Metal (target = MSL attribute + 13), keeping
    GpuProgramParameters verbatim. Returns (new_trailing, typetree_m_Channels_or_None)."""
    attrs = _ATTR_RE.findall(msl)
    params_off = parsed_trailing["params_offset"]
    if not attrs:
        # Procedural VS without stage_in attributes: keep ParserBindChannels intact.
        return trailing, None

    mask = parsed_trailing["src_map"]
    pairs = []
    for sem, semidx, attr in sorted(attrs, key=lambda a: int(a[2])):
        chan = SEM_CHAN.get((sem, int(semidx or 0)))
        if chan is None:
            if sem.startswith("SV_"):
                continue
            raise RuntimeError(f"{tag}: VS input {sem}{semidx}: no Unity channel mapping")
        pairs.append((chan, int(attr) + 13))
        mask |= 1 << chan

    if not pairs:
        return trailing, None

    head = bytearray(struct.pack("<II", mask, len(pairs)))
    for chan, target in pairs:
        head += struct.pack("<II", chan, target)
    new_trailing = bytes(head) + trailing[params_off:]
    m_channels = {
        "m_Channels": [{"source": c, "target": t} for c, t in pairs],
        "m_SourceMap": mask,
    }
    return new_trailing, m_channels
