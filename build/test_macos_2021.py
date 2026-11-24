#!/usr/bin/env python3
"""Test whether a newer Unity Mac player (2021.3 LTS) shows a window on macOS 27,
using the game's 2021.1 assets + rebuilt DLL. The 2021.1 player's Cocoa launch
delegate never fires on Tahoe; 2021.3 LTS has newer macOS launch support."""
import glob
import os
import plistlib
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
GAME = "The House On Ash Tree Lane"
GAME_DATA = os.path.join(ROOT, "extracted", GAME + "_Data")
REBUILT = os.path.join(ROOT, "decomp", "rebuilt", "Assembly-CSharp.dll")
PLAYERS = os.path.join(HERE, "players")
V = "2021.3.48f1"
PKG = os.path.join(PLAYERS, f"UnitySetup-Mac-Mono-Support-for-Editor-{V}.pkg")
EXTRACT = os.path.join(PLAYERS, "extracted", "mac-2021.3")
OUT = os.path.join(HERE, "dist", "macos-2021.3-test")


def main():
    if not os.path.isdir(EXTRACT):
        subprocess.run(["pkgutil", "--expand-full", PKG, EXTRACT], check=True)
    payload = os.path.join(EXTRACT, "TargetSupport.pkg.tmp", "Payload")
    tmpl = os.path.join(payload, "Variations", "macos_arm64_nondevelopment_mono", "UnityPlayer.app")
    if not os.path.isdir(tmpl):
        # fall back to whatever arm64 nondevelopment variation exists
        cands = glob.glob(os.path.join(payload, "Variations", "*arm64*nondevelopment_mono", "UnityPlayer.app"))
        tmpl = cands[0]
    os.makedirs(OUT, exist_ok=True)
    app = os.path.join(OUT, GAME + ".app")
    if os.path.exists(app):
        shutil.rmtree(app)
    shutil.copytree(tmpl, app, symlinks=True)
    c = os.path.join(app, "Contents")
    os.rename(os.path.join(c, "MacOS", "UnityPlayer"), os.path.join(c, "MacOS", GAME))
    dst = os.path.join(c, "Resources", "Data")
    if os.path.exists(dst):
        shutil.rmtree(dst)
    shutil.copytree(GAME_DATA, dst, symlinks=True)
    shutil.copy2(REBUILT, os.path.join(dst, "Managed", "Assembly-CSharp.dll"))
    mbe = os.path.join(c, "MonoBleedingEdge")
    if os.path.exists(mbe):
        shutil.rmtree(mbe)
    shutil.copytree(os.path.join(payload, "MonoBleedingEdge"), mbe, symlinks=True)
    pl = {"CFBundleName": GAME, "CFBundleDisplayName": GAME, "CFBundleExecutable": GAME,
          "CFBundleIdentifier": "com.gingerenslaver.ashtreelane.test", "CFBundleVersion": "1.0",
          "CFBundleShortVersionString": "1.0", "CFBundlePackageType": "APPL",
          "LSMinimumSystemVersion": "10.13", "NSHighResolutionCapable": True,
          "NSPrincipalClass": "NSApplication"}
    plistlib.dump(pl, open(os.path.join(c, "Info.plist"), "wb"))
    subprocess.run(["codesign", "--force", "--deep", "-s", "-", app], check=False, capture_output=True)
    print("assembled:", app)
    log = "/tmp/ash_2021.log"
    if os.path.exists(log):
        os.remove(log)
    subprocess.run(["open", "-n", app, "--args", "-logfile", log])
    time.sleep(10)
    print("log size:", os.path.getsize(log) if os.path.exists(log) else "MISSING")
    if os.path.exists(log) and os.path.getsize(log):
        print(open(log, errors="ignore").read()[:1500])


if __name__ == "__main__":
    main()