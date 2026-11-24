#!/usr/bin/env python3
"""
UnityPy asset & scene extractor for The House On Ash Tree Lane (Unity 2021.1.11f1 Mono).
Extracts only referenced textures (compressed PNG/JPG), color lightmaps, OGG Vorbis audio,
TTF font, legacy AnimationClips, Deflate-compressed binary meshes, materials, sprites, and
all 3 scenes with exact binary MonoBehaviour decoding (verified byte-for-byte).
"""

import json
import math
import os
import struct
import subprocess
import zlib
from pathlib import Path

import numpy as np
from PIL import Image
import UnityPy
from UnityPy.export.MeshExporter import MeshHandler

ROOT = Path("/Users/fabzter/src/rea_test_2-house-on")
DATA_DIR = ROOT / "extracted" / "The House On Ash Tree Lane_Data"
OUT_DIR = ROOT / "build" / "unityfree" / "assets"

TAGS = {
    0: "Untagged",
    1: "Respawn",
    2: "Finish",
    3: "EditorOnly",
    4: "MainCamera",
    5: "MainCamera",
    6: "Player",
    7: "GameController",
}


def load_sfiles():
    env = UnityPy.load(str(DATA_DIR))
    sfiles = {}
    for k, sf in env.files.items():
        if hasattr(sf, "objects"):
            sfiles[os.path.basename(k).lower()] = sf
    return env, sfiles


def resolve_pptr_raw(sf, fid, pid, sfiles):
    if pid == 0:
        return None, None
    if fid == 0:
        target_sf = sf
    else:
        if fid - 1 >= len(sf.externals):
            return None, None
        ext_name = os.path.basename(sf.externals[fid - 1].path.replace("\\", "/")).lower()
        target_sf = sfiles.get(ext_name)
    if target_sf is None:
        return None, None
    obj = target_sf.objects.get(pid)
    return target_sf, obj


def resolve_pptr(sf, pptr, sfiles):
    if not pptr:
        return None, None
    return resolve_pptr_raw(sf, pptr.get("m_FileID", 0), pptr.get("m_PathID", 0), sfiles)


def asset_key(sf, pid):
    return f"{os.path.basename(sf.name).lower()}:{pid}"


def safe_slug(s):
    out = "".join(c if (c.isalnum() or c in "-_.") else "_" for c in (s or "item"))
    return out[:64] or "item"


def export_texture_obj(sf, tex_obj, out_rel_base, is_ui=False, max_dim=1024):
    tex = tex_obj.read()
    if getattr(tex, "m_Width", 0) == 0 or getattr(tex, "m_Height", 0) == 0:
        return None
    sd = getattr(tex, "m_StreamData", None)
    if sd is not None and getattr(sd, "size", 0) > 0 and not getattr(sd, "path", ""):
        return None
    try:
        im = tex.image
    except Exception:
        return None
    if im is None:
        return None
    w, h = im.size
    if is_ui:
        max_dim = 2048
    if max(w, h) > max_dim:
        scale = max_dim / float(max(w, h))
        im = im.resize((max(1, int(round(w * scale))), max(1, int(round(h * scale)))), Image.Resampling.LANCZOS)

    has_alpha = False
    if im.mode in ("RGBA", "LA", "PA"):
        rgba = im.convert("RGBA")
        alpha = np.array(rgba.getchannel("A"))
        if int(alpha.min()) < 252:
            has_alpha = True
    if has_alpha or max(w, h) <= 64:
        rel = f"textures/{out_rel_base}.png"
        dst = OUT_DIR / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        im.convert("RGBA").save(dst, "PNG", optimize=True)
    else:
        rel = f"textures/{out_rel_base}.jpg"
        dst = OUT_DIR / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        im.convert("RGB").save(dst, "JPEG", quality=90 if is_ui else 85, optimize=True)
    return rel


def export_dir_lightmap_obj(tex_obj, rel_path):
    tex = tex_obj.read()
    try:
        im = tex.image
    except Exception:
        return False
    if im is None:
        return False
    im = im.convert("RGBA")
    arr = np.asarray(im, dtype=np.float32)
    h, w, _ = arr.shape
    target = 512
    if h == target and w == target:
        im_out = im
    elif h >= target and w >= target and h % target == 0 and w % target == 0:
        fh, fw = h // target, w // target
        blocks = arr.reshape(target, fh, target, fw, 4)
        valid = (blocks[..., 3] > 12.75)[..., None]  # w > 0.05 (0..255)
        cnt = valid.sum(axis=(1, 3))
        weighted_sum = (blocks * valid).sum(axis=(1, 3))
        fallback_mean = blocks.mean(axis=(1, 3))
        out_arr = np.where(cnt > 0, weighted_sum / np.maximum(cnt, 1), fallback_mean)
        im_out = Image.fromarray(np.clip(out_arr + 0.5, 0.0, 255.0).astype(np.uint8), mode="RGBA")
    else:
        im_out = im.resize((target, target), Image.Resampling.BILINEAR).convert("RGBA")
    dst = OUT_DIR / rel_path
    dst.parent.mkdir(parents=True, exist_ok=True)
    im_out.save(dst, "PNG", optimize=True)
    return True


def export_lightmap_obj(tex_obj, rel_path):
    tex = tex_obj.read()
    im = tex.image
    if im is None:
        return False
    im = im.convert("RGB")
    if max(im.size) > 1024:
        im = im.resize((1024, 1024), Image.Resampling.BILINEAR)
    arr = np.clip(np.asarray(im, dtype=np.float32) / 255.0, 0.0, 1.0)
    # Convert linear decoded HDR values into exact IEC sRGB at 4:4:4 chroma so facets stay crisp
    srgb_f = np.where(arr <= 0.0031308, arr * 12.92, 1.055 * np.power(np.maximum(arr, 1e-8), 1.0 / 2.4) - 0.055)
    srgb = np.clip(srgb_f * 255.0 + 0.5, 0.0, 255.0).astype(np.uint8)
    dst = OUT_DIR / rel_path
    dst.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(srgb, mode="RGB").save(dst, "JPEG", quality=85, subsampling=0, optimize=True)
    return True


def export_mesh(sf, mesh_obj, mesh_cache):
    key = asset_key(sf, mesh_obj.path_id)
    if key in mesh_cache:
        return mesh_cache[key]

    rel = f"meshes/{os.path.basename(sf.name).lower().replace('.assets','')}_{mesh_obj.path_id}.mesh"
    dst = OUT_DIR / rel
    dst.parent.mkdir(parents=True, exist_ok=True)

    m = mesh_obj.read()
    handler = MeshHandler(m)
    handler.process()
    vcount = int(handler.m_VertexCount or 0)
    verts = handler.m_Vertices or []
    norms = handler.m_Normals or []
    uv0 = handler.m_UV0 or []
    uv1 = handler.m_UV1 or []
    cols = handler.m_Colors or []
    sub_tris = handler.get_triangles() if vcount > 0 else []

    if vcount == 0 or len(verts) == 0:
        mesh_cache[key] = None
        return None

    submeshes = []
    flat_indices = []
    for tris in sub_tris:
        off = len(flat_indices)
        cnt = len(tris) * 3
        for tri in tris:
            flat_indices.extend((int(tri[0]), int(tri[1]), int(tri[2])))
        submeshes.append((off, cnt))
    if not submeshes:
        submeshes.append((0, 0))

    # Build interleaved float32 array (14 floats per vertex): pos3, norm3, uv0_2, uv1_2, col4
    v_arr = np.zeros((vcount, 14), dtype=np.float32)
    vp = np.asarray(verts, dtype=np.float32)[:, :3]
    vp = np.nan_to_num(vp, nan=0.0, posinf=0.0, neginf=0.0)
    v_arr[:, 0:3] = vp

    if len(norms) == vcount:
        np_arr = np.asarray(norms, dtype=np.float32)[:, :3]
        v_arr[:, 3:6] = np.nan_to_num(np_arr, nan=0.0)
    else:
        v_arr[:, 4] = 1.0

    if len(uv0) == vcount:
        uv0_arr = np.asarray(uv0, dtype=np.float32)[:, :2]
        v_arr[:, 6:8] = np.nan_to_num(uv0_arr, nan=0.0)

    if len(uv1) == vcount:
        uv1_arr = np.asarray(uv1, dtype=np.float32)[:, :2]
        v_arr[:, 8:10] = np.nan_to_num(uv1_arr, nan=0.0)
    else:
        v_arr[:, 8:10] = v_arr[:, 6:8]

    if len(cols) == vcount:
        c_arr = np.asarray(cols, dtype=np.float32)[:, :4]
        if c_arr.max() > 1.5:
            c_arr = c_arr / 255.0
        v_arr[:, 10:14] = np.nan_to_num(c_arr, nan=1.0)
    else:
        v_arr[:, 10:14] = 1.0

    bmin = vp.min(axis=0)
    bmax = vp.max(axis=0)
    bcenter = (bmin + bmax) * 0.5
    bext = (bmax - bmin) * 0.5

    idx_arr = np.asarray(flat_indices, dtype=np.uint32)
    buf = bytearray()
    buf.extend(struct.pack("<III", vcount, len(idx_arr), len(submeshes)))
    buf.extend(struct.pack("<6f", float(bcenter[0]), float(bcenter[1]), float(bcenter[2]),
                           float(bext[0]), float(bext[1]), float(bext[2])))
    for off, cnt in submeshes:
        buf.extend(struct.pack("<II", off, cnt))
    buf.extend(v_arr.tobytes())
    buf.extend(idx_arr.tobytes())

    comp_obj = zlib.compressobj(level=6, wbits=-15)
    compressed = comp_obj.compress(bytes(buf)) + comp_obj.flush()
    with open(dst, "wb") as f:
        f.write(b"MESH")
        f.write(struct.pack("<I", len(buf)))
        f.write(compressed)

    mesh_cache[key] = rel
    return rel


class BinReader:
    def __init__(self, data: bytes):
        self.data = data
        self.off = 0

    def i32(self) -> int:
        v = struct.unpack_from("<i", self.data, self.off)[0]
        self.off += 4
        return v

    def f32(self) -> float:
        v = struct.unpack_from("<f", self.data, self.off)[0]
        self.off += 4
        return float(v)

    def bool4(self) -> bool:
        return self.i32() != 0

    def color4(self):
        v = struct.unpack_from("<4f", self.data, self.off)
        self.off += 16
        return [float(x) for x in v]

    def pptr(self):
        fid, pid = struct.unpack_from("<iq", self.data, self.off)
        self.off += 12
        return (fid, pid)

    def string(self) -> str:
        n = self.i32()
        s = self.data[self.off:self.off + n].decode("utf-8", errors="replace")
        self.off += (n + 3) & ~3
        return s

    def skip(self, n: int):
        self.off += n


def ref_go(sf, fid, pid, sfiles):
    _, obj = resolve_pptr_raw(sf, fid, pid, sfiles)
    if obj is None:
        return None
    return {"goPid": obj.path_id}


def ref_comp(sf, fid, pid, sfiles):
    _, obj = resolve_pptr_raw(sf, fid, pid, sfiles)
    if obj is None:
        return None
    return {"compPid": obj.path_id}


def ref_asset(sf, fid, pid, sfiles):
    tsf, obj = resolve_pptr_raw(sf, fid, pid, sfiles)
    if obj is None:
        return None
    return {"assetKey": asset_key(tsf, obj.path_id)}


def decode_monobehaviour_fields(cname: str, payload: bytes, sf, sfiles):
    r = BinReader(payload)
    f = {}

    if cname == "Menu":
        f["fade"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "InstructionFade":
        f["anim1"] = ref_comp(sf, *r.pptr(), sfiles)
        f["timer"] = r.f32()
    elif cname == "DoorCollider":
        f["timer"] = r.f32()
        f["levelFade"] = ref_go(sf, *r.pptr(), sfiles)
        f["prompt"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "EndScript":
        f["fade"] = ref_comp(sf, *r.pptr(), sfiles)
        f["endScreen"] = ref_go(sf, *r.pptr(), sfiles)
        f["timer"] = r.f32()
    elif cname == "FirstPersonMovement":
        f["speed"] = r.f32()
        f["canRun"] = r.bool4()
        f["runSpeed"] = r.f32()
        f["runningKey"] = r.i32()
    elif cname == "FirstPersonLook":
        f["character"] = ref_comp(sf, *r.pptr(), sfiles)
        f["sensitivity"] = r.f32()
        f["smoothing"] = r.f32()
    elif cname == "Crouch":
        f["key"] = r.i32()
        f["movement"] = ref_comp(sf, *r.pptr(), sfiles)
        f["movementSpeed"] = r.f32()
        f["headToLower"] = ref_comp(sf, *r.pptr(), sfiles)
        f["crouchYHeadPosition"] = r.f32()
        f["colliderToLower"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "Jump":
        f["jumpStrength"] = r.f32()
        f["groundCheck"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "GroundCheck":
        f["distanceThreshold"] = r.f32()
        f["isGrounded"] = r.bool4()
    elif cname == "FirstPersonAudio":
        f["character"] = ref_comp(sf, *r.pptr(), sfiles)
        f["groundCheck"] = ref_comp(sf, *r.pptr(), sfiles)
        f["stepAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        f["runningAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        f["velocityThreshold"] = r.f32()
        f["landingAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        n = r.i32()
        f["landingSFX"] = [ref_asset(sf, *r.pptr(), sfiles) for _ in range(n)]
        f["jump"] = ref_comp(sf, *r.pptr(), sfiles)
        f["jumpAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        n = r.i32()
        f["jumpSFX"] = [ref_asset(sf, *r.pptr(), sfiles) for _ in range(n)]
        f["crouch"] = ref_comp(sf, *r.pptr(), sfiles)
        f["crouchStartAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        f["crouchedAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        f["crouchEndAudio"] = ref_comp(sf, *r.pptr(), sfiles)
        n = r.i32()
        f["crouchStartSFX"] = [ref_asset(sf, *r.pptr(), sfiles) for _ in range(n)]
        n = r.i32()
        f["crouchEndSFX"] = [ref_asset(sf, *r.pptr(), sfiles) for _ in range(n)]
    elif cname == "HallCollider":
        f["deadEnd"] = ref_go(sf, *r.pptr(), sfiles)
        f["activeStart"] = ref_go(sf, *r.pptr(), sfiles)
    elif cname == "HallNotes":
        f["prompt"] = ref_comp(sf, *r.pptr(), sfiles)
        f["note"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname in ("Note1", "Note2", "Note3"):
        f["prompt"] = ref_comp(sf, *r.pptr(), sfiles)
        f["note"] = ref_comp(sf, *r.pptr(), sfiles)
        f["read" + cname] = r.bool4()
    elif cname == "InteriorHouse":
        f["door"] = ref_go(sf, *r.pptr(), sfiles)
        f["windows"] = ref_go(sf, *r.pptr(), sfiles)
        f["wall"] = ref_go(sf, *r.pptr(), sfiles)
        f["hall"] = ref_go(sf, *r.pptr(), sfiles)
    elif cname == "PlayerStartTurn":
        pass
    elif cname == "SoundPlayer":
        f["soundEffect"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "flashlight":
        f["cam"] = ref_comp(sf, *r.pptr(), sfiles)
    elif cname == "Image":
        _mat = r.pptr()
        f["color"] = r.color4()
        _raycast = r.bool4()
        r.skip(24)  # padding / maskable + onCullStateChanged empty arrays
        f["sprite"] = ref_asset(sf, *r.pptr(), sfiles)
        f["imageType"] = r.i32()
        f["preserveAspect"] = r.bool4()
        r.skip(28)  # fillCenter, fillMethod, fillAmount, fillClockwise, fillOrigin, alphaHitTest, useSpriteMesh, pixelsPerUnitMult
    elif cname == "Text":
        _mat = r.pptr()
        f["color"] = r.color4()
        _raycast = r.bool4()
        r.skip(24)
        f["font"] = ref_asset(sf, *r.pptr(), sfiles)
        f["fontSize"] = r.i32()
        _fontStyle = r.i32()
        _bestFit = r.i32()
        _minSize = r.i32()
        _maxSize = r.i32()
        f["alignment"] = r.i32()
        r.skip(20)  # alignByGeo(bool4), richText(bool4), hOverflow(i32), vOverflow(i32), lineSpacing(f32)
        f["text"] = r.string()
    elif cname == "CanvasScaler":
        f["uiScaleMode"] = r.i32()
        f["referencePixelsPerUnit"] = r.f32()
        f["scaleFactor"] = r.f32()
        f["referenceResolution"] = [r.f32(), r.f32()]
        f["screenMatchMode"] = r.i32()
        f["matchWidthOrHeight"] = r.f32()
        r.skip(20)
    elif cname in ("EventSystem", "GraphicRaycaster", "StandaloneInputModule"):
        r.skip(len(payload))
    else:
        raise ValueError(f"Unhandled MonoBehaviour class: {cname} (len={len(payload)})")

    if r.off != len(payload):
        raise AssertionError(f"{cname} read {r.off} bytes != payload len {len(payload)}")
    return f


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    _, sfiles = load_sfiles()

    # 1. Pre-identify UI Sprite textures so they are exported PNG cleanly
    ui_tex_keys = set()
    sprites_json = {}
    for fn, sf in sfiles.items():
        for o in sf.objects.values():
            if o.type.name == "Sprite":
                d = o.read_typetree(check_read=False)
                tsf, tobj = resolve_pptr(sf, d.get("m_RD", {}).get("texture"), sfiles)
                if tobj:
                    ui_tex_keys.add(asset_key(tsf, tobj.path_id))

    tex_cache = {}
    def get_tex(sf, pptr, is_ui=False):
        tsf, tobj = resolve_pptr(sf, pptr, sfiles)
        if tobj is None:
            return None
        k = asset_key(tsf, tobj.path_id)
        if k in tex_cache:
            return tex_cache[k]
        t_ui = is_ui or (k in ui_tex_keys)
        name = safe_slug(getattr(tobj.read(), "m_Name", ""))
        rel_base = f"{os.path.basename(tsf.name).lower().replace('.assets','')}_{tobj.path_id}_{name}"
        rel = export_texture_obj(tsf, tobj, rel_base, is_ui=t_ui)
        tex_cache[k] = rel
        return rel

    # 2. Export Sprites
    for fn, sf in sfiles.items():
        for o in sf.objects.values():
            if o.type.name == "Sprite":
                d = o.read_typetree(check_read=False)
                rel = get_tex(sf, d.get("m_RD", {}).get("texture"), is_ui=True)
                rect = d.get("m_Rect", {})
                sprites_json[asset_key(sf, o.path_id)] = {
                    "name": d.get("m_Name", ""),
                    "texture": rel,
                    "rect": [rect.get("x", 0), rect.get("y", 0), rect.get("width", 0), rect.get("height", 0)],
                    "pixelsPerUnit": float(d.get("m_PixelsToUnits", 100.0)),
                }
    with open(OUT_DIR / "sprites.json", "w") as f:
        json.dump(sprites_json, f, indent=2)

    # 3. Export Materials (referenced only or non-internal)
    materials_json = {}
    for fn, sf in sfiles.items():
        if fn in ("unity default resources", "unity_builtin_extra"):
            continue
        for o in sf.objects.values():
            if o.type.name != "Material":
                continue
            d = o.read_typetree(check_read=False)
            sh_sf, sh_obj = resolve_pptr(sf, d.get("m_Shader"), sfiles)
            sh_name = "Standard"
            if sh_obj is not None:
                try:
                    sd = sh_obj.read_typetree(check_read=False)
                    sh_name = sd.get("m_ParsedForm", {}).get("m_Name") or sd.get("m_Name") or "Standard"
                except Exception:
                    sh_name = "Standard"
            mat_name = d.get("m_Name", "Material")
            props = d.get("m_SavedProperties", {})
            tex_envs = {k: v for k, v in props.get("m_TexEnvs", [])}
            floats = {k: float(v) for k, v in props.get("m_Floats", [])}
            colors = {k: v for k, v in props.get("m_Colors", [])}

            is_hidden = (
                sh_name.startswith("Hidden/")
                or mat_name in ("NoDraw", "Collider", "Trigger", "InvisibleFace")
                or "Picker" in sh_name
                or "Picker" in mat_name
                or "HideVertices" in sh_name
            )
            is_ciconia = sh_name.startswith("Ciconia Studio/")
            double_sided = is_ciconia or ("Particles" in sh_name)
            mode = floats.get("_Mode", 0.0)
            alpha_cutout = is_ciconia or abs(mode - 1.0) < 0.1
            alpha_blend = (mode >= 2.0 and not is_ciconia) or ("Particles/Alpha Blended" in sh_name)

            col_dict = (colors.get("_Diffusecolor") if is_ciconia else None) or colors.get("_Color") or {"r": 1, "g": 1, "b": 1, "a": 1}
            em_dict = colors.get("_EmissionColor") or {"r": 0, "g": 0, "b": 0, "a": 0}
            # Note Unity stores black emission when unused
            main_env = (tex_envs.get("_DiffuseMapCutoutA") if is_ciconia else None) or tex_envs.get("_MainTex")
            main_tex_rel = None
            main_tex_clamp = False
            main_tex_point = False
            flat_shaded = False
            scale = [1.0, 1.0]
            offset = [0.0, 0.0]
            if main_env and not is_hidden:
                main_tex_rel = get_tex(sf, main_env.get("m_Texture"), is_ui=False)
                tsf_m, tobj_m = resolve_pptr(sf, main_env.get("m_Texture"), sfiles)
                if tobj_m is not None:
                    try:
                        mtt = tobj_m.read_typetree(check_read=False)
                        ts = mtt.get("m_TextureSettings", {})
                        main_tex_clamp = int(ts.get("m_WrapU", 0)) == 1
                        tw = int(mtt.get("m_Width", 0))
                        th = int(mtt.get("m_Height", 0))
                        is_small_palette = max(tw, th) <= 64 and tw > 0
                        main_tex_point = (int(ts.get("m_FilterMode", 1)) == 0) or is_small_palette
                        flat_shaded = is_small_palette
                    except Exception:
                        pass
                sc = main_env.get("m_Scale", {"x": 1, "y": 1})
                of = main_env.get("m_Offset", {"x": 0, "y": 0})
                scale = [float(sc.get("x", 1)), float(sc.get("y", 1))]
                offset = [float(of.get("x", 0)), float(of.get("y", 0))]

            materials_json[asset_key(sf, o.path_id)] = {
                "name": mat_name,
                "shader": sh_name,
                "hidden": is_hidden,
                "doubleSided": double_sided,
                "alphaCutout": alpha_cutout,
                "alphaBlend": alpha_blend,
                "isStandard": sh_name == "Standard",
                "metallic": float(floats.get("_Metallic", 0.0)),
                "smoothness": float(floats.get("_Glossiness", floats.get("_Smoothness", 0.5))),
                "cutoff": float(floats.get("_Cutoff", 0.5)),
                "color": [float(col_dict.get("r", 1)), float(col_dict.get("g", 1)), float(col_dict.get("b", 1)), float(col_dict.get("a", 1))],
                "emission": [float(em_dict.get("r", 0)), float(em_dict.get("g", 0)), float(em_dict.get("b", 0)), float(em_dict.get("a", 0))],
                "mainTex": main_tex_rel,
                "mainTexClamp": main_tex_clamp,
                "mainTexPoint": main_tex_point,
                "flatShaded": flat_shaded,
                "mainTexScale": scale,
                "mainTexOffset": offset,
            }
    with open(OUT_DIR / "materials.json", "w") as f:
        json.dump(materials_json, f, indent=2)

    # 4. Export AudioClips via ffmpeg -> OGG Vorbis
    audio_dir = OUT_DIR / "audio"
    audio_dir.mkdir(parents=True, exist_ok=True)
    audio_map = {}
    for fn, sf in sfiles.items():
        for o in sf.objects.values():
            if o.type.name == "AudioClip":
                clip = o.read()
                samples = clip.samples
                if not samples:
                    continue
                _, wav_bytes = next(iter(samples.items()))
                cname = safe_slug(clip.m_Name)
                base_rel = f"audio/{os.path.basename(sf.name).lower().replace('.assets','')}_{o.path_id}_{cname}"
                ogg_rel = base_rel + ".ogg"
                ogg_dst = OUT_DIR / ogg_rel
                res = subprocess.run(
                    ["ffmpeg", "-y", "-v", "error", "-f", "wav", "-i", "pipe:0", "-ar", "44100", "-ac", "2", "-c:a", "vorbis", "-strict", "-2", "-b:a", "128k", str(ogg_dst)],
                    input=wav_bytes,
                )
                if res.returncode == 0 and ogg_dst.exists() and ogg_dst.stat().st_size > 256:
                    rel = ogg_rel
                else:
                    if ogg_dst.exists():
                        ogg_dst.unlink()
                    rel = base_rel + ".wav"
                    with open(OUT_DIR / rel, "wb") as wf:
                        wf.write(wav_bytes)
                audio_map[asset_key(sf, o.path_id)] = {"name": clip.m_Name, "path": rel}
    with open(OUT_DIR / "audio_map.json", "w") as f:
        json.dump(audio_map, f, indent=2)

    # 5. Export Font consola.ttf
    fonts_dir = OUT_DIR / "fonts"
    fonts_dir.mkdir(parents=True, exist_ok=True)
    fonts_json = {}
    for fn, sf in sfiles.items():
        if fn in ("unity default resources", "unity_builtin_extra"):
            continue
        for o in sf.objects.values():
            if o.type.name == "Font":
                d = o.read_typetree(check_read=False)
                font_bytes = bytes(d.get("m_FontData", b""))
                if font_bytes:
                    rel = f"fonts/{safe_slug(d.get('m_Name','consola'))}.ttf"
                    with open(OUT_DIR / rel, "wb") as ff:
                        ff.write(font_bytes)
                    fonts_json[asset_key(sf, o.path_id)] = rel
    with open(OUT_DIR / "fonts.json", "w") as f:
        json.dump(fonts_json, f, indent=2)

    # 6. Export AnimationClips
    anims_json = {}
    for fn, sf in sfiles.items():
        for o in sf.objects.values():
            if o.type.name == "AnimationClip":
                d = o.read_typetree(check_read=False)
                curves = []
                max_len = 0.0
                for fc in d.get("m_FloatCurves", []):
                    kfs = []
                    for k in fc.get("curve", {}).get("m_Curve", []):
                        t = float(k["time"])
                        max_len = max(max_len, t)
                        in_s = float(k.get("inSlope", 0.0))
                        out_s = float(k.get("outSlope", 0.0))
                        kfs.append({
                            "time": t if math.isfinite(t) else 0.0,
                            "value": float(k["value"]) if math.isfinite(k["value"]) else 0.0,
                            "inSlope": 1e9 if math.isinf(in_s) else (in_s if math.isfinite(in_s) else 0.0),
                            "outSlope": 1e9 if math.isinf(out_s) else (out_s if math.isfinite(out_s) else 0.0),
                        })
                    curves.append({
                        "attribute": fc.get("attribute", ""),
                        "path": fc.get("path", ""),
                        "keyframes": kfs,
                    })
                anims_json[asset_key(sf, o.path_id)] = {
                    "name": d.get("m_Name", ""),
                    "length": max(max_len, 0.01),
                    "wrapMode": int(d.get("m_WrapMode", 0)),
                    "curves": curves,
                }
    with open(OUT_DIR / "animations.json", "w") as f:
        json.dump(anims_json, f, indent=2, allow_nan=False)

    # 7. Export Scenes (level0, level1, level2) & referenced Meshes + Lightmaps
    mesh_cache = {}
    scenes_dir = OUT_DIR / "scenes"
    scenes_dir.mkdir(parents=True, exist_ok=True)
    mb_total = 0

    for idx, sname in enumerate(["level0", "level1", "level2"]):
        sf = sfiles[sname]
        gos = {}
        comps = {}
        render_settings = {}
        lightmaps = []
        dir_lightmaps = []

        for o in sf.objects.values():
            if o.type.name == "GameObject":
                d = o.read_typetree(check_read=False)
                tag_id = int(d.get("m_Tag", 0))
                gos[o.path_id] = {
                    "pid": o.path_id,
                    "name": d.get("m_Name", "GameObject"),
                    "activeSelf": bool(d.get("m_IsActive", True)),
                    "tag": TAGS.get(tag_id, "Untagged"),
                    "layer": int(d.get("m_Layer", 0)),
                    "compPtrs": [c["component"]["m_PathID"] for c in d.get("m_Component", [])],
                    "transform": None,
                    "components": [],
                }
            elif o.type.name == "RenderSettings":
                d = o.read_typetree(check_read=False)
                amb = d.get("m_AmbientSkyColor", {"r": 0.2, "g": 0.2, "b": 0.25, "a": 1})
                sky_sf, sky_obj = resolve_pptr(sf, d.get("m_SkyboxMaterial"), sfiles)
                fg = d.get("m_FogColor", {"r": 0.5, "g": 0.5, "b": 0.5, "a": 1})
                probe = d.get("m_AmbientProbe", {})
                sh_list = [float(probe.get(f"sh[{i:2d}]", 0.0)) for i in range(27)] if probe else []
                render_settings = {
                    "ambientLight": [amb["r"], amb["g"], amb["b"], amb["a"]],
                    "ambientIntensity": float(d.get("m_AmbientIntensity", 1.0)),
                    "reflectionIntensity": float(d.get("m_ReflectionIntensity", 1.0)),
                    "ambientProbe": sh_list,
                    "skyboxMaterial": asset_key(sky_sf, sky_obj.path_id) if sky_obj else None,
                    "fog": bool(d.get("m_Fog", False)),
                    "fogColor": [fg["r"], fg["g"], fg["b"], fg["a"]],
                    "fogDensity": float(d.get("m_FogDensity", 0.01)),
                }
            elif o.type.name == "LightmapSettings":
                d = o.read_typetree(check_read=False)
                for lmi, lm in enumerate(d.get("m_Lightmaps", [])):
                    lm_sf, lm_obj = resolve_pptr(sf, lm.get("m_Lightmap"), sfiles)
                    if lm_obj is not None:
                        rel = f"lightmaps/lm_{sname}_{lmi}.jpg"
                        if export_lightmap_obj(lm_obj, rel):
                            lightmaps.append(rel)
                        else:
                            lightmaps.append(None)
                    else:
                        lightmaps.append(None)
                    dm_sf, dm_obj = resolve_pptr(sf, lm.get("m_DirLightmap"), sfiles)
                    if dm_obj is not None:
                        drel = f"lightmaps/dm_{sname}_{lmi}.png"
                        if export_dir_lightmap_obj(dm_obj, drel):
                            dir_lightmaps.append(drel)
                        else:
                            dir_lightmaps.append(None)
                    else:
                        dir_lightmaps.append(None)
            elif o.type.name in ("Transform", "RectTransform"):
                d = o.read_typetree(check_read=False)
                lp = d["m_LocalPosition"]
                lr = d["m_LocalRotation"]
                ls = d["m_LocalScale"]
                go_pid = d["m_GameObject"]["m_PathID"]
                father_pid = d.get("m_Father", {}).get("m_PathID", 0)
                tdata = {
                    "pid": o.path_id,
                    "goPid": go_pid,
                    "isRectTransform": (o.type.name == "RectTransform"),
                    "parentTransformPid": father_pid if father_pid != 0 else None,
                    "childrenOrder": [c["m_PathID"] for c in d.get("m_Children", [])],
                    "localPosition": [float(lp["x"]), float(lp["y"]), float(lp["z"])],
                    "localRotation": [float(lr["x"]), float(lr["y"]), float(lr["z"]), float(lr["w"])],
                    "localScale": [float(ls["x"]), float(ls["y"]), float(ls["z"])],
                }
                if o.type.name == "RectTransform":
                    amin = d["m_AnchorMin"]; amax = d["m_AnchorMax"]
                    apos = d["m_AnchoredPosition"]; sd = d["m_SizeDelta"]; piv = d["m_Pivot"]
                    tdata["anchorMin"] = [float(amin["x"]), float(amin["y"])]
                    tdata["anchorMax"] = [float(amax["x"]), float(amax["y"])]
                    tdata["anchoredPosition"] = [float(apos["x"]), float(apos["y"])]
                    tdata["sizeDelta"] = [float(sd["x"]), float(sd["y"])]
                    tdata["pivot"] = [float(piv["x"]), float(piv["y"])]
                comps[o.path_id] = ("__Transform__", tdata)
            elif o.type.name == "Camera":
                d = o.read_typetree(check_read=False)
                bg = d["m_BackGroundColor"]
                comps[o.path_id] = ("Camera", {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "clearFlags": int(d.get("m_ClearFlags", 1)),
                    "backgroundColor": [bg["r"], bg["g"], bg["b"], bg["a"]],
                    "fov": float(d.get("field of view", 60.0)),
                    "near": float(d.get("near clip plane", 0.3)),
                    "far": float(d.get("far clip plane", 1000.0)),
                    "cullingMask": int(d.get("m_CullingMask", {}).get("m_Bits", 0xFFFFFFFF) if isinstance(d.get("m_CullingMask"), dict) else d.get("m_CullingMask", -1)),
                })
            elif o.type.name == "Light":
                d = o.read_typetree(check_read=False)
                col = d["m_Color"]
                comps[o.path_id] = ("Light", {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "lightType": int(d.get("m_Type", 1)),
                    "color": [col["r"], col["g"], col["b"], col["a"]],
                    "intensity": float(d.get("m_Intensity", 1.0)),
                    "range": float(d.get("m_Range", 10.0)),
                    "spotAngle": float(d.get("m_SpotAngle", 30.0)),
                })
            elif o.type.name == "MeshFilter":
                d = o.read_typetree(check_read=False)
                msf, mobj = resolve_pptr(sf, d.get("m_Mesh"), sfiles)
                mesh_rel = export_mesh(msf, mobj, mesh_cache) if mobj else None
                comps[o.path_id] = ("MeshFilter", {"pid": o.path_id, "enabled": True, "mesh": mesh_rel})
            elif o.type.name in ("MeshRenderer", "SkinnedMeshRenderer"):
                d = o.read_typetree(check_read=False)
                mats = []
                for mp in d.get("m_Materials", []):
                    msf, mobj = resolve_pptr(sf, mp, sfiles)
                    mats.append(asset_key(msf, mobj.path_id) if mobj else None)
                sb = d.get("m_StaticBatchInfo", {})
                lm_idx = int(d.get("m_LightmapIndex", 65535))
                if lm_idx == 65535 or lm_idx == 65534:
                    lm_idx = -1
                st = d.get("m_LightmapTilingOffset", {"x": 1, "y": 1, "z": 0, "w": 0})
                cdata = {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "materials": mats,
                    "lightmapIndex": lm_idx,
                    "lightmapScaleOffset": [float(st["x"]), float(st["y"]), float(st["z"]), float(st["w"])],
                    "isPartOfStaticBatch": int(sb.get("subMeshCount", 0)) > 0,
                    "staticBatchFirstSubMesh": int(sb.get("firstSubMesh", 0)),
                    "staticBatchSubMeshCount": int(sb.get("subMeshCount", 0)),
                }
                if o.type.name == "SkinnedMeshRenderer":
                    msf, mobj = resolve_pptr(sf, d.get("m_Mesh"), sfiles)
                    cdata["mesh"] = export_mesh(msf, mobj, mesh_cache) if mobj else None
                comps[o.path_id] = (o.type.name, cdata)
            elif o.type.name == "BoxCollider":
                d = o.read_typetree(check_read=False)
                c = d["m_Center"]; sz = d["m_Size"]
                comps[o.path_id] = ("BoxCollider", {
                    "pid": o.path_id, "enabled": bool(d.get("m_Enabled", True)),
                    "isTrigger": bool(d.get("m_IsTrigger", False)),
                    "center": [c["x"], c["y"], c["z"]], "size": [sz["x"], sz["y"], sz["z"]],
                })
            elif o.type.name == "CapsuleCollider":
                d = o.read_typetree(check_read=False)
                c = d["m_Center"]
                comps[o.path_id] = ("CapsuleCollider", {
                    "pid": o.path_id, "enabled": bool(d.get("m_Enabled", True)),
                    "isTrigger": bool(d.get("m_IsTrigger", False)),
                    "center": [c["x"], c["y"], c["z"]],
                    "radius": float(d.get("m_Radius", 0.5)),
                    "height": float(d.get("m_Height", 2.0)),
                    "direction": int(d.get("m_Direction", 1)),
                })
            elif o.type.name == "MeshCollider":
                d = o.read_typetree(check_read=False)
                msf, mobj = resolve_pptr(sf, d.get("m_Mesh"), sfiles)
                mesh_rel = export_mesh(msf, mobj, mesh_cache) if mobj else None
                comps[o.path_id] = ("MeshCollider", {
                    "pid": o.path_id, "enabled": bool(d.get("m_Enabled", True)),
                    "isTrigger": bool(d.get("m_IsTrigger", False)),
                    "convex": bool(d.get("m_Convex", False)),
                    "mesh": mesh_rel,
                })
            elif o.type.name == "Rigidbody":
                d = o.read_typetree(check_read=False)
                comps[o.path_id] = ("Rigidbody", {
                    "pid": o.path_id, "enabled": True,
                    "mass": float(d.get("m_Mass", 1.0)),
                    "drag": float(d.get("m_Drag", 0.0)),
                    "useGravity": bool(d.get("m_UseGravity", True)),
                    "isKinematic": bool(d.get("m_IsKinematic", False)),
                    "constraints": int(d.get("m_Constraints", 0)),
                })
            elif o.type.name == "AudioSource":
                d = o.read_typetree(check_read=False)
                csf, cobj = resolve_pptr(sf, d.get("m_audioClip"), sfiles)
                sb_keys = d.get("panLevelCustomCurve", {}).get("m_Curve", [])
                spatial = float(sb_keys[0]["value"]) if sb_keys else 0.0
                comps[o.path_id] = ("AudioSource", {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "clip": asset_key(csf, cobj.path_id) if cobj else None,
                    "playOnAwake": bool(d.get("m_PlayOnAwake", False)),
                    "loop": bool(d.get("Loop", False)),
                    "volume": float(d.get("m_Volume", 1.0)),
                    "pitch": float(d.get("m_Pitch", 1.0)),
                    "spatialBlend": spatial,
                    "minDistance": float(d.get("MinDistance", 1.0)),
                    "maxDistance": float(d.get("MaxDistance", 500.0)),
                })
            elif o.type.name == "Animation":
                d = o.read_typetree(check_read=False)
                def ak(pptr):
                    asf, aobj = resolve_pptr(sf, pptr, sfiles)
                    return asset_key(asf, aobj.path_id) if aobj else None
                clips = [ak(c) for c in d.get("m_Animations", [])]
                comps[o.path_id] = ("Animation", {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "clip": ak(d.get("m_Animation")),
                    "clips": [c for c in clips if c],
                    "playAutomatically": bool(d.get("m_PlayAutomatically", False)),
                })
            elif o.type.name == "Canvas":
                d = o.read_typetree(check_read=False)
                comps[o.path_id] = ("Canvas", {
                    "pid": o.path_id,
                    "enabled": bool(d.get("m_Enabled", True)),
                    "renderMode": int(d.get("m_RenderMode", 0)),
                    "sortingOrder": int(d.get("m_SortingOrder", 0)),
                })
            elif o.type.name == "MonoBehaviour":
                raw = bytes(o.get_raw_data())
                enabled = raw[12] != 0
                nlen = struct.unpack_from("<i", raw, 28)[0]
                hdr_end = 32 + ((nlen + 3) & ~3)
                payload = raw[hdr_end:]
                mb = o.read(check_read=False)
                cname = mb.m_Script.read().m_ClassName
                fields = decode_monobehaviour_fields(cname, payload, sf, sfiles)
                mb_total += 1
                comps[o.path_id] = (cname, {
                    "pid": o.path_id,
                    "enabled": enabled,
                    "isMonoBehaviour": True,
                    "fields": fields,
                })

        # Resolve Transform parenting + attach components in GameObject order
        tr_to_go = {}
        for cpid, (ctype, cdata) in comps.items():
            if ctype == "__Transform__":
                tr_to_go[cpid] = cdata["goPid"]

        for go in gos.values():
            for cpid in go["compPtrs"]:
                entry = comps.get(cpid)
                if not entry:
                    continue
                ctype, cdata = entry
                if ctype == "__Transform__":
                    ptpid = cdata.pop("parentTransformPid")
                    cdata["parentGoPid"] = tr_to_go.get(ptpid) if ptpid else None
                    cdata.pop("goPid", None)
                    go["transform"] = cdata
                else:
                    go["components"].append({"type": ctype, **cdata})
            del go["compPtrs"]

        # Map child sibling index on each child transform from parent's childrenOrder
        child_order_map = {}
        for go in gos.values():
            tr = go["transform"]
            if tr:
                for order_idx, child_tr_pid in enumerate(tr.get("childrenOrder", [])):
                    child_order_map[child_tr_pid] = order_idx
        for go in gos.values():
            tr = go["transform"]
            if tr:
                tr["siblingOrder"] = child_order_map.get(tr["pid"], 0)
                tr.pop("childrenOrder", None)

        scene_doc = {
            "sceneIndex": idx,
            "sceneName": sname,
            "renderSettings": render_settings,
            "lightmaps": lightmaps,
            "dirLightmaps": dir_lightmaps,
            "gameObjects": list(gos.values()),
        }
        with open(scenes_dir / f"scene_{idx}.json", "w") as f:
            json.dump(scene_doc, f)

        print(f"Scene {idx} ({sname}): {len(gos)} GOs, {len(lightmaps)} lightmaps")

    print(f"DONE: textures={len(tex_cache)}, meshes={len(mesh_cache)}, audio={len(audio_map)}, anims={len(anims_json)}, MonoBehavioursVerified={mb_total}")


if __name__ == "__main__":
    main()
