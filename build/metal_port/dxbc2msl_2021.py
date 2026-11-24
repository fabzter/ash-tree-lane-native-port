#!/usr/bin/env python3
"""
dxbc2msl_2021.py - synthesize stripped RDEF reflection chunks from Unity 2021.1's
trailing GpuProgramParameters and translate DXBC -> Unity-ABI Metal MSL via hlslcc-cli.
"""
import os
import struct
import subprocess
import tempfile

_DEFAULT_HLSLCC = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "tools",
    "casualties-port",
    "tools",
    "hlslcc",
    "hlslcc-cli",
)
HLSLCC = os.environ.get("HLSLCC_CLI") or _DEFAULT_HLSLCC

# HLSLCC_FLAG_TRANSLATE_MATRICES (0x20000) |
# HLSLCC_FLAG_REMOVE_UNUSED_GLOBALS (0x10000) |
# HLSLCC_FLAG_METAL_SHADOW_SAMPLER_LINEAR (0x80000)
HLSLCC_FLAGS = 0x20000 | 0x10000 | 0x80000

SVT_BOOL = 1
SVT_INT = 2
SVT_FLOAT = 3
SVC_SCALAR = 0
SVC_VECTOR = 1
SVC_MATRIX_COLUMNS = 3

UNITY_TYPE_TO_SVT = {0: SVT_FLOAT, 1: SVT_INT, 2: SVT_BOOL}

# D3D10_SB resource dimension (dcl token bits 15:11) -> D3D_SRV_DIMENSION for RDEF
DCLDIM_TO_SRVDIM = {
    1: 1,   # BUFFER -> BUFFER
    2: 2,   # TEXTURE1D -> TEXTURE1D
    3: 4,   # TEXTURE2D -> TEXTURE2D
    4: 6,   # TEXTURE2DMS -> TEXTURE2DMS
    5: 8,   # TEXTURE3D -> TEXTURE3D
    6: 9,   # TEXTURECUBE -> TEXTURECUBE
    7: 3,   # TEXTURE1DARRAY -> TEXTURE1DARRAY
    8: 5,   # TEXTURE2DARRAY -> TEXTURE2DARRAY
    9: 7,   # TEXTURE2DMSARRAY -> TEXTURE2DMSARRAY
    10: 10, # TEXTURECUBEARRAY -> TEXTURECUBEARRAY
}


def parse_container(dxbc: bytes) -> dict[bytes, bytes]:
    assert dxbc[:4] == b"DXBC", dxbc[:4]
    nch = struct.unpack_from("<I", dxbc, 28)[0]
    offs = struct.unpack_from(f"<{nch}I", dxbc, 32)
    chunks = {}
    for o in offs:
        four = dxbc[o:o + 4]
        size = struct.unpack_from("<I", dxbc, o + 4)[0]
        chunks[four] = dxbc[o + 8:o + 8 + size]
    return chunks


def parse_dcls(shdr: bytes) -> dict:
    """Parse DXBC SHDR/SHEX resource, constant-buffer, and sampler declarations."""
    ver, length = struct.unpack_from("<II", shdr, 0)
    stage = (ver >> 16) & 0xFFFF  # 0 pixel, 1 vertex
    major = (ver >> 4) & 0xF
    minor = ver & 0xF
    out = {
        "stage": stage,
        "major": major,
        "minor": minor,
        "cbs": {},
        "textures": {},
        "tex_samples": {},
        "samplers": [],
    }
    p = 8
    end = min(length * 4, len(shdr))
    while p + 4 <= end:
        tok = struct.unpack_from("<I", shdr, p)[0]
        op = tok & 0x7FF
        if op == 0x35:  # OPCODE_CUSTOMDATA (length in next dword)
            if p + 8 > end:
                break
            olen = struct.unpack_from("<I", shdr, p + 4)[0]
            if olen == 0:
                break
            p += olen * 4
            continue
        olen = (tok >> 24) & 0x7F
        if olen == 0:
            break
        if op == 0x59:  # dcl_constantbuffer
            slot = struct.unpack_from("<I", shdr, p + 8)[0]
            size = struct.unpack_from("<I", shdr, p + 12)[0]
            out["cbs"][slot] = max(out["cbs"].get(slot, 0), size)
        elif op == 0x58:  # dcl_resource
            dim = (tok >> 11) & 0x1F
            sample_cnt = (tok >> 16) & 0x7F
            slot = struct.unpack_from("<I", shdr, p + 8)[0]
            srv_dim = DCLDIM_TO_SRVDIM.get(dim, 4)
            out["textures"][slot] = srv_dim
            if srv_dim in (6, 7):  # TEXTURE2DMS / TEXTURE2DMSARRAY
                out["tex_samples"][slot] = sample_cnt if sample_cnt else 4
        elif op == 0x5A:  # dcl_sampler
            slot = struct.unpack_from("<I", shdr, p + 8)[0]
            out["samplers"].append(slot)
        p += olen * 4
    return out


def build_names_from_trailing(parsed_trailing: dict) -> tuple[dict, dict]:
    """Build RDEF `names` and `{slot: ({member: offset}, cb_size)}` from trailing params."""
    cbs_by_name = {cb["name"]: cb for cb in parsed_trailing["cbs"] if cb["name"]}
    names = {"cbs": {}, "textures": {}, "samplers": {}}
    offsets_by_slot = {}

    for b in parsed_trailing["binds"]:
        if b["kind"] == "cb":
            cb = cbs_by_name.get(b["name"])
            if cb is None:
                continue
            members = []
            for p in cb["params"]:
                svt = UNITY_TYPE_TO_SVT.get(p["type"], SVT_FLOAT)
                if p["is_mat"]:
                    cls = SVC_MATRIX_COLUMNS
                    rows = p["rows"] or 4
                    cols = p["cols"] or 4
                else:
                    cols = p["cols"] or 1
                    rows = 1
                    cls = SVC_SCALAR if cols == 1 else SVC_VECTOR
                members.append((p["name"], p["offset"], cls, svt, rows, cols, p["arr"]))
            names["cbs"][b["index"]] = {
                "name": cb["name"],
                "size": cb["size"],
                "members": members,
            }
            offsets_by_slot[b["index"]] = ({m[0]: m[1] for m in members}, cb["size"])
        elif b["kind"] == "tex":
            names["textures"][b["index"]] = b["name"]
            if b["sampler"] != 0xFFFFFFFF:
                names["samplers"].setdefault(b["sampler"], "sampler" + b["name"])

    return names, offsets_by_slot


def _rdef_var_size(rows: int, cols: int, elems: int) -> int:
    """D3D11 constant-buffer variable span in bytes (16-byte stride for array elements)."""
    if rows > 1:
        return max(1, elems) * rows * 16
    if elems > 0:
        return (elems - 1) * 16 + cols * 4
    return cols * 4


def build_rdef(dcls: dict, names: dict, stage: int, major: int = 4) -> bytes:
    """Synthesize a D3D11 RDEF chunk compatible with HLSLcc's reflect.cpp."""
    var_dwords = 10 if major >= 5 else 6
    bindings = []  # (name, type, rettype, dim, numsamples, bindpoint)
    for slot in sorted(dcls["cbs"]):
        nm = names.get("cbs", {}).get(slot, {}).get("name", f"cb{slot}")
        bindings.append((nm, 0, 0, 0, 0, slot))
    for slot, dim in sorted(dcls["textures"].items()):
        nm = names.get("textures", {}).get(slot, f"tex{slot}")
        ns = dcls.get("tex_samples", {}).get(slot, 0xFFFFFFFF)
        bindings.append((nm, 2, 5, dim, ns, slot))
    for slot in sorted(dcls["samplers"]):
        nm = names.get("samplers", {}).get(slot, f"sampler{slot}")
        bindings.append((nm, 3, 0, 0, 0, slot))

    cbs = []
    for slot in sorted(dcls["cbs"]):
        info = names.get("cbs", {}).get(slot, {})
        nm = info.get("name", f"cb{slot}")
        size = max(info.get("size", 0), dcls["cbs"][slot] * 16)
        members = list(info.get("members", []))
        covered = [False] * ((size + 15) // 16)
        for (_, off, _, _, rows, cols, elems) in members:
            bytecount = _rdef_var_size(rows, cols, elems)
            for i in range(off // 16, min(len(covered), (off + bytecount + 15) // 16)):
                covered[i] = True
        run = None
        for i, c in enumerate(covered + [True]):
            if not c and run is None:
                run = i
            elif c and run is not None:
                count = i - run
                members.append((
                    f"pad_{nm}_{run}",
                    run * 16,
                    SVC_VECTOR,
                    SVT_FLOAT,
                    1,
                    4,
                    count if count > 1 else 0,
                ))
                run = None
        members.sort(key=lambda m: m[1])
        cbs.append((nm, size, members))

    hdr_size = 6 * 4
    bind_off = hdr_size
    bind_size = len(bindings) * 8 * 4
    cb_off = bind_off + bind_size
    cb_size = len(cbs) * 6 * 4
    var_off = cb_off + cb_size
    nvars = sum(len(m) for _, _, m in cbs)
    var_size = nvars * var_dwords * 4
    type_off = var_off + var_size
    type_size = nvars * 16
    str_off = type_off + type_size

    stroffs = {}
    strblob = bytearray()

    def intern(name: str) -> int:
        if name not in stroffs:
            stroffs[name] = str_off + len(strblob)
            strblob.extend(name.encode("ascii") + b"\0")
        return stroffs[name]

    target = ((0xFFFE if stage else 0xFFFF) << 16) | (major << 8)
    out = bytearray()
    out += struct.pack("<6I", len(cbs), cb_off, len(bindings), bind_off, target, 0)
    for nm, t, rt, dim, ns, bp in bindings:
        out += struct.pack("<8I", intern(nm), t, rt, dim, ns, bp, 1, 0)
    vi = 0
    for nm, size, members in cbs:
        out += struct.pack("<6I", intern(nm), len(members), var_off + vi * var_dwords * 4, size, 0, 0)
        vi += len(members)
    ti = 0
    for nm, size, members in cbs:
        for (mn, off, cls, typ, rows, cols, elems) in members:
            bytecount = _rdef_var_size(rows, cols, elems)
            out += struct.pack("<6I", intern(mn), off, bytecount, 2, type_off + ti * 16, 0)
            if var_dwords == 10:
                out += struct.pack("<4I", 0xFFFFFFFF, 0, 0xFFFFFFFF, 0)
            ti += 1
    for nm, size, members in cbs:
        for (mn, off, cls, typ, rows, cols, elems) in members:
            out += struct.pack("<6HI", cls, typ, rows, cols, elems, 0, 0)
    assert len(out) == str_off, (len(out), str_off)
    out += strblob
    return bytes(out)


def assemble_dxbc(chunks_in_order: list[tuple[bytes, bytes]]) -> bytes:
    n = len(chunks_in_order)
    hdr_len = 32 + 4 * n
    offs = []
    blob = bytearray()
    for four, data in chunks_in_order:
        offs.append(hdr_len + len(blob))
        blob += four + struct.pack("<I", len(data)) + data
    total = hdr_len + len(blob)
    out = bytearray(b"DXBC" + b"\0" * 16 + struct.pack("<III", 1, total, n))
    out += struct.pack(f"<{n}I", *offs)
    out += blob
    return bytes(out)


def dxbc_to_msl(payload: bytes, names: dict, flags: int = HLSLCC_FLAGS) -> tuple[str, str]:
    """Translate a Unity D3D11 code-entry payload (prefix + DXBC) into Metal MSL."""
    idx = payload.find(b"DXBC")
    if idx < 0:
        raise ValueError("no DXBC magic in payload")
    chunks = parse_container(payload[idx:])
    code_four = b"SHDR" if b"SHDR" in chunks else b"SHEX"
    dcls = parse_dcls(chunks[code_four])
    rdef = build_rdef(dcls, names, dcls["stage"], dcls["major"])
    order = [(b"RDEF", rdef)]
    for f in (b"ISGN", b"OSGN", b"OSG5", b"PCSG", code_four):
        if f in chunks:
            order.append((f, chunks[f]))
    full = assemble_dxbc(order)
    with tempfile.NamedTemporaryFile(suffix=".dxbc", delete=False) as tf:
        tf.write(full)
        path = tf.name
    try:
        args = [HLSLCC, path, f"{flags:x}"]
        r = subprocess.run(args, capture_output=True, text=True, timeout=60)
    finally:
        if os.path.exists(path):
            os.unlink(path)
    if r.returncode != 0:
        raise RuntimeError(f"hlslcc failed rc={r.returncode}: {r.stderr[:400]}")
    return r.stdout, r.stderr
