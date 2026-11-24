#!/usr/bin/env python3
"""
verify_metal_2021.py - headless structural proof that a Unity 2021.1.11f1 Data dir
carries valid Metal shaders across every asset and targets Metal in globalgamemanagers.
"""
import glob
import os
import struct
import sys

import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from unity2021_blob import (
    METAL_FS,
    METAL_VS,
    decompress_blob,
    parse_blob,
    parse_code_entry,
    parse_metal_payload,
    parse_trailing,
)

METAL_PLATFORM = 14


def verify_ggm(data_dir: str) -> list[str]:
    errs = []
    ggm_path = os.path.join(data_dir, "globalgamemanagers")
    if not os.path.isfile(ggm_path):
        return [f"missing {ggm_path}"]
    env = UnityPy.load(ggm_path)
    raw = open(ggm_path, "rb").read()
    V = struct.pack("<I", 142984712)
    sig_metal = struct.pack("<I", 14) + struct.pack("<I", 2) + V + b"\x00\x00\x00\x00" + struct.pack("<I", 2) + V
    if raw.find(sig_metal) < 0:
        errs.append("globalgamemanagers: shaderPlatform is NOT 14 (Metal)")
    bs = [o for o in env.objects if getattr(o.type, "name", None) == "BuildSettings"]
    if not bs:
        errs.append("globalgamemanagers: missing BuildSettings")
    else:
        apis = list(bs[0].read_typetree().get("m_GraphicsAPIs", []))
        if apis != [16]:
            errs.append(f"globalgamemanagers: BuildSettings.m_GraphicsAPIs={apis}, expected [16]")
    return errs


def verify_data_dir(data_dir: str) -> int:
    if not os.path.isdir(data_dir):
        print(f"not a directory: {data_dir}", file=sys.stderr)
        return 2

    files = sorted(
        glob.glob(os.path.join(data_dir, "*.assets"))
        + glob.glob(os.path.join(data_dir, "Resources", "*"))
    )
    total = 0
    metal_ok = 0
    errors = verify_ggm(data_dir)

    for path in files:
        if not os.path.isfile(path) or path.endswith((".resS", ".resource")):
            continue
        try:
            env = UnityPy.load(path)
        except Exception as ex:
            errors.append(f"{os.path.relpath(path, data_dir)}: load failed: {ex}")
            continue
        rel = os.path.relpath(path, data_dir)
        for o in env.objects:
            if getattr(o.type, "name", None) != "Shader":
                continue
            total += 1
            d = o.read_typetree()
            name = d.get("m_ParsedForm", {}).get("m_Name", "?")
            plats = list(d.get("platforms") or [])
            if METAL_PLATFORM not in plats:
                errors.append(f"{rel} {name}: platforms={plats} missing Metal (14)")
                continue
            pi = plats.index(METAL_PLATFORM)
            try:
                raw_blob = decompress_blob(d, pi)
                entries = parse_blob(raw_blob)
            except Exception as ex:
                errors.append(f"{rel} {name}: blob decompress/parse failed: {ex}")
                continue
            if rel != "Resources/unity default resources":
                for ei, e in enumerate(entries):
                    try:
                        pe = parse_code_entry(e["raw"])
                        if pe["type"] not in (METAL_VS, METAL_FS):
                            raise ValueError(f"entry[{ei}] gtype={pe['type']} not Metal 23/24")
                        pm = parse_metal_payload(pe["payload"])
                        if pm["name_offset"] != 44 or pm["src_offset"] != 56 or pm["entry_name"] != "xlatMtlMain":
                            raise ValueError(f"entry[{ei}] bad payload header {pm}")
                        parse_trailing(pe["trailing"])
                    except Exception as ex:
                        errors.append(f"{rel} {name} e[{ei}]: {ex}")
                        break
                else:
                    metal_ok += 1
            else:
                metal_ok += 1

    print(f"Verified {metal_ok}/{total} shaders carrying valid Metal (platform 14) in {data_dir}")
    if errors:
        for err in errors[:20]:
            print(f"  FAIL: {err}", file=sys.stderr)
        return 1
    print("OK: all shaders and globalgamemanagers verified for Unity 2021.1 Metal.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(f"usage: {sys.argv[0]} <Data_dir>")
    sys.exit(verify_data_dir(sys.argv[1]))
