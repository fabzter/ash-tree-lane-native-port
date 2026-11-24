# Lessons learned — no-editor Unity porting

Consolidated findings from porting *The House On Ash Tree Lane* (Unity 2021.1.11f1, Mono) from a
Windows build to native macOS Metal, plus cross-platform players, **without the Unity editor**.
Written for future agents and humans. Game © GingerEnslaver; technique credit: Andreansx
(`casualties-unknown-apple-silicon`).

## 1. Anatomy: what is and isn't portable
- A Unity player = native engine (`UnityPlayer.dll/.dylib/.so`) + serialized assets + managed DLLs.
- Only `Assembly-CSharp.dll` (game logic) is platform-agnostic. The engine and **compiled shader
  variants** are platform-specific. Swapping the DLL alone does NOT make a Windows build render on Mac.

## 2. Lib-only recomp (no editor)
- Decompile with ILSpy from *inside* `Managed/` (clean reference resolution). Recompile with Roslyn
  `-nostdlib -noconfig`, referencing every shipped `Managed/*.dll` except `Assembly-CSharp.dll`.
- Fidelity gates: byte-size match (39,424 B) + re-decompile-and-diff all 45 types identical.

## 3. Cross-platform players without the editor
- Unity's archive "Support for Editor" modules contain the standalone **PlaybackEngines**; download
  them, never install the editor. `download.unity3d.com/download_unity/<changeset>/…`.
- Probe URLs with `curl -sI`: a **39-byte body means 404** (don't trust a parsed "200").
- Mac `.pkg` → `pkgutil --expand-full`; it ships a **native arm64** Mono template.
- Linux Mono is a Windows-hosted **modified NSIS** `.exe` that 7zz AND libarchive cannot parse:
  carve at `EF BE AD DE "NullsoftInst"`, decode the solid-LZMA header (props 0x5D, dict 8MB, stream
  at +45), then parse file-data blocks (4-byte size, high bit = LZMA, 5-byte props header).
- Mono runtime layout differs: Mac `Contents/MonoBleedingEdge/etc`; Linux
  `<Game>_Data/MonoBleedingEdge/x86_64/` (NOT the build root like Windows).

## 4. macOS windowed launch — the real root cause
- A hand-assembled Mac app that stalls (idle `NSApplicationMain`, no window/log) is missing
  **`MainMenu.nib`** and the right principal class. FIX: copy `MainMenu.nib` from the module's
  `Source/Player/MacPlayer/MacPlayerEntryPoint/Resources/` into `Contents/Resources/`, and set
  `NSPrincipalClass=PlayerApplication` + `NSMainNibFile=MainMenu` (NOT plain `NSApplication`).
- **Lesson:** an early misdiagnosis blamed macOS 27 because the *bare* template also stalled when run
  via `Popen` (no bundle context). That test was misleading. Don't conclude "OS incompatibility"
  before checking the bundle's nib/principal-class against Unity's own `Info.plist` template.

## 5. Rendering: D3D11-only shaders → Null device crash
- With D3D11-only shader variants the Mac player picks **NullGfxDevice** and crashes in
  `_NSViewDrawRect`. `-force-metal` fails (`InitializeEngineGraphics failed`); the built
  `PlayerSettings` is stripped (no API toggle). The Mac player's *built-in* shaders are Metal, but
  the game's custom + `unity_builtin_extra` shaders are not.

## 6. The no-editor Metal shader port (the hard part, solved)
- Unity's Metal backend compiles **MSL source at load** (`newLibraryWithSource:`) — inject MSL in
  Unity's ABI, NOT precompiled metallib.
- Unity **strips the RDEF reflection chunk** from DXBC; synthesize it from SHDR/SHEX
  `dcl_constantbuffer/dcl_resource/dcl_sampler`, then convert DXBC→MSL via **Unity HLSLcc**.
- Repack into the *target Unity version's* Metal sub-program blob: entry =
  `wrapper(32B) | 0xF00DCAFE payload | trailing input-binding table`. The trailing table is
  platform-independent — **preserve it**; blob must be **LZ4-block-compressed**.
- Patch `globalgamemanagers` **surgically (4-byte writes, never re-serialize)** to declare Metal.
- Verify MSL with `xcrun metal` (native Mac compiler). Gate = Player.log
  `Initializing Metal device caps: <GPU>` (not Null) + a rendered frame.
- Blob format differs per Unity version: disassemble `UnityPlayer.dylib`
  (`LoadGpuProgramFromData`, `GpuProgramMetal::CreateFromShaderLabSource`) to adapt 2022.3→2021.1.

## 7. Process & delegation lessons
- **Research before concluding "impossible."** Existing public ports proved feasibility; the answer
  was a known technique, not a research dead end.
- **Delegate heavy RE to a stronger model** via Herdr (`agy` = Antigravity "Gemini 4 Neon"). It
  reverse-engineered the blob format and implemented the pipeline. Let it work; check regularly.
- Herdr traps: a **quota/models overlay** swallows input (dismiss with `esc`) — but `esc` mid-turn
  **cancels the agent's work**. Read the pane before sending keys.
- Verify independently (Player.log + screenshot), don't trust a single signal.

## 8. Environmental, not build defects
- Linux under **qemu** (amd64-on-arm64) hits a Mono JIT `tramp-amd64.c` assert after reload; runs on
  real x86_64 hardware. macOS batchmode `-nographics` uses a Null device (shader errors expected).

## 9. Ethics
- Honor the original author (GingerEnslaver) and the technique's author (Andreansx). Do not
  redistribute the game's assets/binaries/verbatim source; publish only tooling + notes + credit.