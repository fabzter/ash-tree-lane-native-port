# Cross-platform build project — The House On Ash Tree Lane

Builds the decompiled game for **Windows, macOS, and Linux** with **no Unity editor**.
Engine matched exactly: **Unity 2021.1.11f1** (changeset `4d8c25f7477e`).

## Idea

A Unity Mono game = native engine runtime (platform-specific) + serialized assets + managed
assemblies. The managed code (`Assembly-CSharp.dll`) is platform-agnostic; we rebuild it from
our decompiled sources (`../decomp/csharp`, 45 types) with Roslyn (`../decomp/build.sh`,
byte-size identical: 39,424 B).

For each target we obtain the matching **Unity 2021.1.11f1 standalone player runtime** from
Unity's official "Support for Editor" modules (downloadable without the editor), then assemble:

```
target player native runtime (UnityPlayer.dll / .dylib / .so + launcher + Mono runtime)
  + the game's version-matched <Game>_Data (assets + Managed libs, rebuilt DLL swapped in)
```

- **Windows**: `../extracted` already IS a Windows Mono player — no download.
- **macOS**: `UnitySetup-Mac-Mono-Support-for-Editor-2021.1.11f1.pkg` → native **arm64** template.
- **Linux**: `UnitySetup-Linux-Mono-Support-for-Editor-2021.1.11f1.exe` (Unity's modified NSIS;
  `assemble.py` carves + LZMA-decodes it directly — 7zz/libarchive can't parse it).

## Usage

```bash
bash fetch_players.sh        # download Mac/Linux player modules (bounded to players/)
bash build.sh                # recomp DLL + assemble dist/{windows,macos,linux}
bash build.sh windows        # one platform
```

Outputs land in `dist/<platform>/`. See `../notes/cross-platform-build.md` for the full
verification record, platform layout gotchas, and the NSIS block map.

## Verification (engine + rebuilt-DLL gate)

Gate = Unity Player.log: `Initialize engine version: 2021.1.11f1` + `MonoManager ReloadAssembly
... Completed reload` with no load errors.

- **Windows**: full windowed runtime verified via CrossOver (D3D11 renderer + Mono reload).
- **macOS**: native arm64; batchmode → engine init + `Completed reload, 0.041 s`.
- **Linux**: x86_64 container; batchmode → engine init + `Completed reload, 0.357 s`, game code ran.

## Environmental caveats (not build defects)

- **macOS 27 windowed**: the stock Unity 2021.1 Mac template's Cocoa launch delegate doesn't
  fire on macOS 27 (the bare template stalls identically). Batchmode inits fully; the build
  runs windowed on macOS ≤ 11/12. Details in the notes.
- **Linux under qemu** (arm64 host): a Mono JIT trampoline assert after reload is a qemu
  emulation artifact; on real x86_64 hardware the build runs.
- Cross-platform *shader* rendering uses the variants baked in the original Windows assets;
  per-platform shader recompilation would require the editor (out of scope).