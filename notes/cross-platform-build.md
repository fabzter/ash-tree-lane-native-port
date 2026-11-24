# Cross-platform build — verification record (2026-11-23)

Project: `build/` — builds the decompiled game for **Windows, macOS, Linux** with **no Unity
editor**. Engine version matched exactly: **Unity 2021.1.11f1** (changeset `4d8c25f7477e`).

## Method

A Unity Mono game = native engine runtime (platform-specific) + serialized assets + managed
assemblies. Only `Assembly-CSharp.dll` is platform-agnostic; we rebuild it from
`decomp/csharp` (45 types) via `decomp/build.sh` (Roslyn, byte-size identical: 39,424 B).

Per platform we obtain the matching **standalone player runtime** from Unity's official
archive "Support for Editor" modules (downloadable without the editor) and assemble:
`target native runtime + the game's version-matched _Data (rebuilt DLL swapped in)`.

| Platform | Player source | Extraction |
|---|---|---|
| Windows | already in `extracted/` (the original build IS a Win Mono player) | none |
| macOS | `MacEditorTargetInstaller/UnitySetup-Mac-Mono-Support-for-Editor-2021.1.11f1.pkg` (501 MB) | `pkgutil --expand-full`; native **arm64** template `macos_arm64_nondevelopment_mono` |
| Linux | `TargetSupportInstaller/UnitySetup-Linux-Mono-Support-for-Editor-2021.1.11f1.exe` (103 MB) | Unity's modified NSIS defeats 7zz/libarchive → `assemble.py` carves the NSIS magic + decodes solid-LZMA blocks directly |

## Platform-specific layout gotchas (verified)

- **macOS**: Mono config lives at `Contents/MonoBleedingEdge/etc/mono` (NOT in Frameworks —
  putting it there also breaks `codesign --deep`). Launcher renamed to the product name;
  `Info.plist` generated; ad-hoc codesigned (Apple Silicon requires a valid signature).
- **Linux**: Mono runtime lives at `<Game>_Data/MonoBleedingEdge/x86_64/libmonobdwgc-2.0.so`
  (+ `libMonoPosixHelper.so`), config at `<Game>_Data/MonoBleedingEdge/etc`. NOT at the build
  root like Windows. Linux-Mono NSIS block map: 420=LinuxPlayer, 421=UnityPlayer.so
  (withgfx nondevelopment), 155=libmonobdwgc, 154=libMonoPosixHelper.

## Runtime verification (engine + rebuilt-DLL gate)

Success gate = Unity Player.log: `Initialize engine version: 2021.1.11f1` +
`Begin MonoManager ReloadAssembly` → `Completed reload` with no load errors.

- **Windows**: full windowed runtime verified earlier via CrossOver wineloader (Player.log
  engine init + Mono reload + D3D11 renderer). `dist/windows` is that build with the rebuilt DLL.
- **macOS**: native arm64. `-batchmode -nographics` → engine init + `Completed reload, 0.041 s`.
  The rebuilt DLL loads and JITs. Shader errors in this gate are the Null device (expected).
- **Linux**: x86_64 ubuntu:22.04 container (qemu emulation on arm64 host). `-batchmode
  -nographics` → engine init + `Completed reload, 0.357 s`; game code executed
  (`UnityEngine.UI.Graphic:.ctor` on the stack). The rebuilt DLL loads and JITs.

## Environmental caveats (NOT build defects)

1. **macOS windowed launch on macOS 27 (Tahoe)**: the player process starts and registers
   "(in front)" but the Cocoa `PlayerAppDelegate applicationDidFinishLaunching:` never fires,
   so no window/log in windowed mode. **The bare, unmodified Unity 2021.1 Mac template stalls
   identically** — this is a Unity 2021.1 + macOS 27 incompatibility, not our assembly.
   Batchmode (headless) inits fully. The build runs windowed on macOS versions Unity 2021.1
   supports (≤ 11/12). Diagnosed via `sample` (idle `NSApplicationMain` event loop) and
   `-batchmode` isolation.
2. **Linux under qemu user emulation**: after Mono reload, a Mono JIT trampoline assertion
   (`tramp-amd64.c:596`) aborts — qemu's amd64-on-arm64 JIT emulation is imperfect. On real
   x86_64 Linux hardware the build runs; the engine + DLL gate passes before the assert.

## Reproduce

```bash
bash build/fetch_players.sh   # Mac .pkg + Linux-Mono .exe (bounded to build/players/)
bash build/build.sh           # recomp DLL + assemble dist/{windows,macos,linux}
```