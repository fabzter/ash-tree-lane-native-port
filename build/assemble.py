#!/usr/bin/env python3
"""Assemble cross-platform builds of the decompiled game from Unity 2021.1.11f1 standalone
player runtimes + the game's version-matched _Data (rebuilt Assembly-CSharp.dll swapped in).

No Unity editor is used. Player modules are extracted from Unity's official archive packages.
"""
import argparse
import glob
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
GAME = "The House On Ash Tree Lane"
GAME_DATA = os.path.join(ROOT, "extracted", GAME + "_Data")
REBUILT = os.path.join(ROOT, "decomp", "rebuilt", "Assembly-CSharp.dll")
PLAYERS = os.path.join(HERE, "players")
DIST = os.path.join(HERE, "dist")


def extract_modules():
    """Extract the Mac (.pkg) and Linux (.tar.xz) player modules; idempotent."""
    lx = os.path.join(PLAYERS, "extracted", "linux")
    txz = glob.glob(os.path.join(PLAYERS, "*Linux*.tar.xz"))
    if txz and not os.path.isdir(lx):
        os.makedirs(lx, exist_ok=True)
        subprocess.run(["tar", "-xf", txz[0], "-C", lx], check=True)
    mc = os.path.join(PLAYERS, "extracted", "mac")
    pkg = glob.glob(os.path.join(PLAYERS, "*Mac*.pkg"))
    if pkg and not os.path.isdir(mc):
        os.makedirs(mc, exist_ok=True)
        subprocess.run(["pkgutil", "--expand-full", pkg[0], mc], check=True)
    return lx, mc


def discover():
    lx, mc = extract_modules()
    for label, base in (("LINUX", lx), ("MAC", mc)):
        print(f"==== {label} module layout ====")
        if not os.path.isdir(base):
            print("  (not extracted yet)")
            continue
        for root, dirs, files in os.walk(base):
            depth = root[len(base):].count(os.sep)
            if depth > 5:
                dirs[:] = []
                continue
            rel = os.path.relpath(root, base)
            interesting = [f for f in files if f.startswith("UnityPlayer") or f in
                           ("UnityPlayer.dll", "UnityPlayer.so", "UnityPlayer.dylib") or
                           "Player" in f or f.endswith((".so", ".dylib", ".dll")) or
                           "mono" in root.lower()]
            if interesting or depth <= 3:
                print(f"  {rel}/  ({len(files)} files)")
                for f in interesting[:8]:
                    print(f"      {f}")


def copy_game_data(dst_data):
    """Copy the game's version-matched _Data, swapping in the rebuilt DLL."""
    if os.path.exists(dst_data):
        shutil.rmtree(dst_data)
    shutil.copytree(GAME_DATA, dst_data, symlinks=True)
    shutil.copy2(REBUILT, os.path.join(dst_data, "Managed", "Assembly-CSharp.dll"))


def build_windows():
    out = os.path.join(DIST, "windows")
    os.makedirs(out, exist_ok=True)
    # The original extracted build IS the Windows player; copy it whole, swap rebuilt DLL.
    src = os.path.join(ROOT, "extracted")
    for entry in os.listdir(src):
        s = os.path.join(src, entry)
        d = os.path.join(out, entry)
        if os.path.isdir(s):
            if os.path.exists(d):
                shutil.rmtree(d)
            shutil.copytree(s, d, symlinks=True)
        else:
            shutil.copy2(s, d)
    shutil.copy2(REBUILT, os.path.join(out, GAME + "_Data", "Managed", "Assembly-CSharp.dll"))
    print("windows ->", out)


MAC_PAYLOAD = os.path.join(PLAYERS, "extracted", "mac", "TargetSupport.pkg.tmp", "Payload")
MAC_APP_TMPL = os.path.join(MAC_PAYLOAD, "Variations",
                            "macos_arm64_nondevelopment_mono", "UnityPlayer.app")
METAL_ASSETS = os.path.join(HERE, "shaders", "metal_assets")


def overlay_metal_assets(dst_data):
    """Overlay converted Metal shaders and Metal-patched globalgamemanagers onto dst_data."""
    if not os.path.isdir(METAL_ASSETS):
        raise FileNotFoundError(
            f"Missing {METAL_ASSETS}; run `python3 build/metal_port/convert_shaders.py --compile-check` first"
        )
    count = 0
    for root, _, files in os.walk(METAL_ASSETS):
        for fname in files:
            src = os.path.join(root, fname)
            rel = os.path.relpath(src, METAL_ASSETS)
            dst = os.path.join(dst_data, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            count += 1
    print(f"overlaid {count} Metal asset file(s) from {METAL_ASSETS}")


def build_macos(use_metal=True):
    import plistlib
    out = os.path.join(DIST, "macos")
    os.makedirs(out, exist_ok=True)
    app = os.path.join(out, GAME + ".app")
    if os.path.exists(app):
        shutil.rmtree(app)
    # Native arm64 Mono player skeleton (launcher + UnityPlayer.dylib + mono dylibs).
    shutil.copytree(MAC_APP_TMPL, app, symlinks=True)
    contents = os.path.join(app, "Contents")
    # Rename the launcher to the product name (real Unity Mac convention).
    os.rename(os.path.join(contents, "MacOS", "UnityPlayer"),
              os.path.join(contents, "MacOS", GAME))
    # Game's version-matched Data (rebuilt DLL swapped in).
    dst_data = os.path.join(contents, "Resources", "Data")
    copy_game_data(dst_data)
    if use_metal:
        overlay_metal_assets(dst_data)
    # MainMenu.nib + icon: NSApplicationMain loads the nib (NSMainNibFile) to wire up the
    # PlayerAppDelegate; without it the delegate never fires and no window appears.
    entry_res = os.path.join(MAC_PAYLOAD, "Source", "Player", "MacPlayer",
                             "MacPlayerEntryPoint", "Resources")
    shutil.copytree(os.path.join(entry_res, "MainMenu.nib"),
                    os.path.join(contents, "Resources", "MainMenu.nib"), symlinks=True)
    shutil.copy2(os.path.join(entry_res, "PlayerIcon.icns"),
                 os.path.join(contents, "Resources", "PlayerIcon.icns"))
    # Mono runtime config at Contents/MonoBleedingEdge (player reads etc/mono here).
    mbe_dst = os.path.join(contents, "MonoBleedingEdge")
    if os.path.exists(mbe_dst):
        shutil.rmtree(mbe_dst)
    shutil.copytree(os.path.join(MAC_PAYLOAD, "MonoBleedingEdge"), mbe_dst, symlinks=True)
    # Info.plist from Unity's MacPlayerEntryPoint template. CRITICAL: NSPrincipalClass is
    # Unity's PlayerApplication subclass + NSMainNibFile=MainMenu (NOT plain NSApplication).
    plist = {
        "CFBundleName": GAME,
        "CFBundleDisplayName": GAME,
        "CFBundleExecutable": GAME,
        "CFBundleIdentifier": "com.gingerenslaver.ashtreelane",
        "CFBundleIconFile": "PlayerIcon.icns",
        "CFBundleVersion": "1.0.2",
        "CFBundleShortVersionString": "1.0.2",
        "CFBundlePackageType": "APPL",
        "CFBundleDevelopmentRegion": "English",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleSupportedPlatforms": ["MacOSX"],
        "LSMinimumSystemVersion": "10.13.0",
        "NSHighResolutionCapable": True,
        "NSMainNibFile": "MainMenu",
        "NSPrincipalClass": "PlayerApplication",
        "NSAppTransportSecurity": {"NSAllowsArbitraryLoads": True},
    }
    with open(os.path.join(contents, "Info.plist"), "wb") as f:
        plistlib.dump(plist, f)
    # Ad-hoc codesign (Apple Silicon needs a valid signature; MonoBleedingEdge sits at
    # Contents root so --deep doesn't mistake etc/mono for nested code).
    subprocess.run(["codesign", "--force", "--deep", "-s", "-", app], check=True)
    print("macos ->", app)


LINUX_MONO_FILES = os.path.join(PLAYERS, "extracted", "linux-mono", "files")


def extract_linux_mono():
    """Extract the Linux-Mono NSIS installer (Unity's modified NSIS defeats 7zz/libarchive).
    Carve the NSIS archive at its magic, then parse solid-LZMA data blocks directly."""
    if os.path.isdir(LINUX_MONO_FILES) and len(os.listdir(LINUX_MONO_FILES)) > 500:
        return LINUX_MONO_FILES
    import lzma
    import struct
    exe = glob.glob(os.path.join(PLAYERS, "*Linux-Mono*.exe"))[0]
    d = open(exe, "rb").read()
    i = d.find(b"\xef\xbe\xad\xdeNullsoftInst")
    raw = d[i - 4:]
    bsz = struct.unpack("<I", raw[36:40])[0] & 0x7FFFFFFF
    off = 40 + bsz  # start of file-data blocks (after compressed header block)
    os.makedirs(LINUX_MONO_FILES, exist_ok=True)
    idx = 0
    while off < len(raw) - 4:
        sz = struct.unpack("<I", raw[off:off + 4])[0]
        off += 4
        comp = sz & 0x80000000
        n = sz & 0x7FFFFFFF
        if n == 0:
            continue
        if off + n > len(raw):
            break
        blob = raw[off:off + n]
        off += n
        if comp:
            try:
                dec = lzma.LZMADecompressor(
                    format=lzma.FORMAT_RAW,
                    filters=[{"id": lzma.FILTER_LZMA1, "dict_size": 1 << 23,
                              "lc": 3, "lp": 0, "pb": 2}])
                data = dec.decompress(blob[5:])
            except Exception:
                data = blob
        else:
            data = blob
        open(os.path.join(LINUX_MONO_FILES, f"{idx:03d}.bin"), "wb").write(data)
        idx += 1
    return LINUX_MONO_FILES


# Verified block map for UnitySetup-Linux-Mono-Support-for-Editor-2021.1.11f1.exe:
#   420 = LinuxPlayer (withgfx nondevelopment launcher, ET_EXEC)
#   421 = UnityPlayer.so (withgfx nondevelopment Mono, full GL/Vulkan)
#   155 = libmonobdwgc-2.0.so   154 = libMonoPosixHelper.so
def build_linux():
    files = extract_linux_mono()

    def fb(i):
        return os.path.join(files, f"{i:03d}.bin")

    out = os.path.join(DIST, "linux")
    if os.path.exists(out):
        shutil.rmtree(out)
    os.makedirs(out)
    shutil.copy2(fb(420), os.path.join(out, GAME))
    os.chmod(os.path.join(out, GAME), 0o755)
    shutil.copy2(fb(421), os.path.join(out, "UnityPlayer.so"))
    copy_game_data(os.path.join(out, GAME + "_Data"))
    mbe = os.path.join(out, GAME + "_Data", "MonoBleedingEdge")
    emb = os.path.join(mbe, "x86_64")
    os.makedirs(emb)
    shutil.copy2(fb(155), os.path.join(emb, "libmonobdwgc-2.0.so"))
    shutil.copy2(fb(154), os.path.join(emb, "libMonoPosixHelper.so"))
    # Mono config is cross-platform; reuse the Mac module's etc/mono.
    shutil.copytree(os.path.join(MAC_PAYLOAD, "MonoBleedingEdge", "etc"),
                    os.path.join(mbe, "etc"), symlinks=True)
    print("linux ->", out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--platform", choices=["windows", "macos", "linux"])
    ap.add_argument("--discover", action="store_true")
    ap.add_argument("--no-metal", action="store_true", help="Skip Metal asset overlay for macOS")
    args = ap.parse_args()
    if args.discover:
        discover()
        return
    os.makedirs(DIST, exist_ok=True)
    if args.platform == "macos":
        build_macos(use_metal=not args.no_metal)
    else:
        {"windows": build_windows, "linux": build_linux}[args.platform]()


if __name__ == "__main__":
    main()