# Repository Guidelines

Reverse-engineering workspace for **The House On Ash Tree Lane** (© GingerEnslaver,
itch.io) — a Unity puzzle/exploration game. Goals: (1) full decompilation + **lib-only
recompilation** into a functionally identical program; (2) **cross-platform players**
(Windows/macOS/Linux); (3) a **no-editor native macOS Metal port** of the D3D11-only shaders.
Everything is bounded to this directory; no system-wide Unity editor is used.
See `README.md` (credit + intent) and `notes/lessons-learned.md` (durable findings).

## Project Overview

- Target: `https://gingerenslaver.itch.io/ashtreelane` — free Windows x64 download.
- Engine: **Unity 2021.1.11f1, Mono scripting backend** (not IL2CPP), 3 levels.
- Approach: decompile the game C# (`Assembly-CSharp.dll`) → recompile against the shipped
  Unity managed libraries → swap the rebuilt DLL into the original build. Engine
  (`UnityPlayer.dll`) and all assets/scenes stay untouched.
- Equivalence criterion: **functional equivalence** (byte-identical Unity builds are not
  achievable). Verified: rebuilt DLL is byte-size identical and all 45 game types decompile
  identically.

## Cross-platform players & native Metal port (no editor)

- `build/fetch_players.sh` downloads Unity 2021.1.11f1 standalone **player runtimes** from the
  official archive ("Support for Editor" modules); `build/assemble.py` + `build/build.sh` assemble
  `build/dist/{windows,macos,linux}` (target native runtime + game `_Data` + rebuilt DLL).
- **macOS windowed launch** requires `MainMenu.nib` + `NSPrincipalClass=PlayerApplication` +
  `NSMainNibFile=MainMenu` (see `build/assemble.py build_macos`); without them the Cocoa delegate
  never fires.
- **Native Metal render**: the Windows assets are D3D11-only → Mac picks NullGfxDevice and crashes.
  `build/metal_port/` (dxbc2msl_2021.py, unity2021_blob.py, convert_shaders.py, verify_metal_2021.py)
  converts 54 shaders / 1580 DXBC entries to Metal via Unity HLSLcc + RDEF synthesis + Unity-2021.1
  blob repack + surgical globalgamemanagers patch. Plan: `build/PORT_PLAN.md`.
- Technique credit: `Andreansx/casualties-unknown-apple-silicon` (cloned under `build/tools/`).
- Durable knowledge: skills `unity-mono-lib-only-recomp` + `unity-no-editor-metal-shader-port`
  (in `fabzter/agent-skills`); findings in `notes/lessons-learned.md`,
  `notes/cross-platform-build.md`.

## Architecture & Data Flow

- The shipped build is a standard Unity Mono player: `UnityPlayer.dll` (native engine) +
  `<Game>_Data/Managed/*.dll` (managed assemblies) + serialized assets (`level*`,
  `sharedassets*`, `resources.assets`).
- Game logic lives entirely in `Managed/Assembly-CSharp.dll` (~38 KB, 45 types): a
  first-person controller, puzzle/scene flow, and a post-processing image-effect stack.
- Recomp data flow: `decomp/csharp/*.cs` → `decomp/build.sh` (Roslyn `csc -nostdlib`,
  references every shipped `Managed/*.dll`) → `decomp/rebuilt/Assembly-CSharp.dll` →
  swapped into `extracted/.../Managed/`.

## Key Directories

- `original/` — pristine downloaded `AshTreeLane.zip` (SHA-256 recorded in `notes/`).
- `extracted/` — unpacked build. **Note:** `Managed/Assembly-CSharp.dll` here is currently
  the *rebuilt* DLL; the pristine original is backed up at `decomp/Assembly-CSharp.original.dll`.
- `decomp/csharp/` — ILSpy decompilation of the original assembly (our sources, 45 scripts).
- `decomp/rebuilt/` — our recompiled `Assembly-CSharp.dll`.
- `decomp/rebuilt-ref/` — clean re-decompilation of the rebuilt DLL (for parity diffing).
- `decomp/build.sh` — the lib-only compile script.
- `tools/` — AssetRipper 2.0.0 (mac arm64), acquired but not needed for the lib-only path.
- `notes/` — acquisition artifacts, hashes, this project's research logs.

## Development Commands

```bash
# Recompile our sources against the shipped Unity libs (no Unity editor):
bash decomp/build.sh                      # -> decomp/rebuilt/Assembly-CSharp.dll

# Decompile an assembly (ILSpy auto-resolves refs from the DLL's own directory):
~/.dotnet/tools/ilspycmd "<path>/Assembly-CSharp.dll" -p -o <outdir>

# Parity check (all 45 game types should be identical):
diff -rq decomp/csharp decomp/rebuilt-ref

# Restore the pristine original DLL into the build:
cp decomp/Assembly-CSharp.original.dll "extracted/The House On Ash Tree Lane_Data/Managed/Assembly-CSharp.dll"
```

## Code Conventions & Common Patterns

- Decompiled C# is kept verbatim from ILSpy (no hand edits) so parity diffing stays meaningful.
- The compile references **all** shipped `Managed/*.dll` except `Assembly-CSharp.dll` itself
  (referencing it causes CS0436 type-conflict warnings).
- Compile flags: `-nostdlib -noconfig -target:library -unsafe+ -langversion:latest -optimize+`
  so the output's assembly references match the Unity Mono runtime exactly.
- Image-effect scripts (Kuwahara, Bloom, DoG, PixelArtFilter, …) are a post-processing pack;
  game-flow scripts are `Menu`, `PlayerController`, `DoorCollider`, `HallNotes`, `Note1-3`,
  `InteriorHouse`, `EndScript`.

## Important Files

- `decomp/build.sh` — recomp entry point.
- `decomp/Assembly-CSharp.original.dll` — pristine original (backup; SHA-256 in notes).
- `extracted/The House On Ash Tree Lane_Data/globalgamemanagers` — encodes Unity version.
- `extracted/The House On Ash Tree Lane_Data/Managed/` — reference assemblies for the compile.

## Runtime/Tooling Preferences

- **No Unity editor.** Lib-only recomp via the installed .NET SDK's Roslyn
  (`/opt/homebrew/Cellar/dotnet/10.0.402/libexec/sdk/10.0.402/Roslyn/bincore/csc.dll`).
- ILSpy: `dotnet tool install -g ilspycmd` (invoked via `~/.dotnet/tools/ilspycmd`).
- macOS arm64 host. Windows build verified via **CrossOver** wineloader; the **native Mac Metal
  build runs directly** (no Wine/Rosetta). Linux full-run needs real x86_64 hardware (qemu JIT assert).
- Keep all installs/artifacts bounded to this directory.

## Testing & QA

- Parity gate: `diff -rq decomp/csharp decomp/rebuilt-ref` → only `.csproj` and
  `Properties/AssemblyInfo.cs` may differ; **all 45 game types must be identical**.
- Size gate: rebuilt DLL must be 39,424 bytes (matches original).
- Empirical runtime equivalence (side-by-side play + screenshot diff) requires a Windows
  runtime and is the optional final confirmation step.
- **Metal render gate (macOS)**: Player.log must show `Initializing Metal device caps: <GPU>`
  (NOT `NullGfxDevice`) and a rendered frame; MSL must pass `xcrun metal -c`. A Null-device
  `_NSViewDrawRect` crash means shaders still lack Metal variants.