# TODO — shader cross-compilation (deferred)

The shipped game assets are a **Windows** build: the game's custom shaders are compiled to
**Direct3D 11 bytecode**. Other platforms need their own shader variants:
- **macOS** → Metal
- **Linux** → OpenGLCore / Vulkan
- (Windows already has D3D11 — renders via CrossOver on this Mac, verified.)

The Mac player runtime ships Metal versions of Unity's **built-in** shaders, but NOT the game's
custom image-effect shaders. Those must be produced for each target API.

## Confirmed scope (investigated 2026-11-23/24)
- Game shaders live in `resources.assets` / `sharedassets*.assets` as `Shader` objects with a
  lz4-compressed `compressedBlob` (Unity's proprietary compiled-shader format).
- **1,239 DXBC variant chunks** total across the game shaders (one shader alone has 739).
- DXBC is extractable (magic `DXBC`, totalSize at +24); first chunk carved to `build/shaders/first.dxbc`.

## Pipeline needed (per target API)
1. Parse Unity's compiled-shader blob → extract each DXBC variant + its keyword/pass metadata.
2. DXBC → SPIR-V (no off-the-shelf tool here: `spirv-cross` CLI takes SPIR-V only;
   `vkd3d-shader` does DXBC→SPIR-V but isn't packaged — build from source).
3. SPIR-V → target (`spirv-cross --msl` for Metal; `--glsl`/`--vulkan` for Linux). Installed: spirv-cross.
4. Repack converted shaders into Unity's blob format with variant metadata so the player accepts them.
5. Make the build declare the target API (built `PlayerSettings` is stripped — 775 bytes, no API list).

## Status
- Native **macOS windowed launch is FIXED** (MainMenu.nib + NSPrincipalClass=PlayerApplication +
  NSMainNibFile=MainMenu). Engine + rebuilt DLL run windowed.
- Rendering on Mac/Linux blocked until the game's shaders have Metal/GL variants (this TODO).
- Reliable visible play today: **Windows build via CrossOver** (D3D11 → Apple GPU, verified).

## Alternative to cross-compilation
One-time contained Unity editor build per target compiles the shaders natively (Metal on Mac),
then the runtime stays editor-free. Requires relaxing the no-editor constraint for that bake.