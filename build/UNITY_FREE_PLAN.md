# Unity-Free Rebuild Architecture Plan — *The House On Ash Tree Lane*

## 1. Executive Summary & Backend Decision
- **Objective**: Completely remove the Unity Engine (`UnityPlayer.dll`, Unity Mac player binary, and built-in shaders/assets bloat) while running the verbatim decompiled C# gameplay & post-processing code ([decomp/csharp/](file:///Users/fabzter/src/rea_test_2-house-on/decomp/csharp), 45 types) inside a compact, self-contained macOS Apple Silicon (`osx-arm64`) C# application under `build/unityfree/`.
- **Chosen Backend**: **SDL3 (`SDL_GPU` Native Metal)** via `ppy.SDL3-CS` (`2026.1002.1`, MIT) + `StbImageSharp` (`2.30.16`, MIT) + `StbTrueTypeSharp` (`1.26.13`, MIT) + `NVorbis` (`0.10.5`, MIT), linked against `/opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib` (2.4 MB).
- **Justification vs FNA / MonoGame / raylib-cs**:
  1. **Native Apple Silicon Metal without `fxc.exe` or Wine**: FNA's legacy prebuilt runtime archive (`fnalibs.tar.bz2`) returns HTTP 404, FNA3D itself has migrated onto `SDL_GPU`, and FNA `Effect` shaders require compiled Direct3D 9 FXBC (`fx_2_0`/`fx_3_0`) bytecode produced by Windows `fxc.exe`. Both MonoGame (`mgfxc` + OpenGL-only macOS) and `raylib-cs` (OpenGL-only macOS) drop native Metal support. Direct **SDL3 `SDL_GPU`** compiles and runs native **MSL** (`SDL_GPU_SHADERFORMAT_MSL`) shaders on Metal with offline compile checking via `xcrun metal -c -std=macos-metal2.2`.
  2. **Minimal Dependency Footprint**: A single 2.4 MB `libSDL3.0.dylib` handles Cocoa windowing, relative mouse capture (`SDL_SetWindowRelativeMouseMode`), keyboard/mouse events, Metal GPU command buffers/pipelines/render targets, and CoreAudio F32 PCM streaming (`SDL_OpenAudioDeviceStream`).
  3. **Cross-Platform Portability**: The same `SDL_GPU` API natively targets Vulkan on Linux/Windows and D3D12 on Windows with identical C# orchestration code.

---

## 2. Measured Size Reduction Targets
The shipped Unity `.app` is **~855 MB** primarily due to uncompressed/DXT/BC6H Unity `.resS` streaming assets and bundled Unity runtime binaries:

| Asset / Binary Category | Original Unity Build | Unity-Free Target & Strategy |
|---|---|---|
| Textures (109 material/UI/sprite maps) | 448.58 MB | UI/Sprites (`Menu`, `Instructions`, `EndScreen`, `Note1–10`) → PNG max 2048px; Diffuse/Cutout → JPEG q85 / PNG alpha max 1024px; Normal/ORM → JPEG q80 max 512px (**~8–15 MB**) |
| Lightmaps (`level1`: 126 color+dir, `level2`: 1 color+dir) | 352.50 MB | Color lightmaps only (drop directional maps), tone-mapped & downscaled to 256×256 JPEG q85 (**~2 MB**) |
| Unreferenced textures (7) | 1.08 MB | Omitted (**0 MB**) |
| Meshes (205 meshes, 544k verts) | ~20 MB | Interleaved binary `.mesh` + Deflate compression (**~5–8 MB**) |
| AudioClips (15 SFX/ambience) | 0.20 MB | Transcoded via `ffmpeg` (`libvorbis -q:a 5`) `.ogg`, decoded via `NVorbis` (**~0.25 MB**) |
| Font (`consola.ttf`), Animations, Materials, Scenes | ~1 MB | Raw `.ttf` + compact JSON (`animations.json`, `materials.json`, `sprites.json`, `scene_{0..2}.json`) (**~1 MB**) |
| **Total Extracted Assets** | **~830 MB** | **~15–30 MB** |
| Engine + Runtime (`.NET 10` self-contained `osx-arm64` + `libSDL3.0.dylib`) | ~25 MB Unity + Mono | `.NET` runtime + `UnityShim` + `GameScripts` + `libSDL3.0.dylib` (**~65–85 MB total `.app`** — >85% smaller overall) |

---

## 3. Solution & Directory Architecture
Everything lives inside `build/unityfree/`:

```text
build/unityfree/
├── AshTreeLane.sln
├── extractor/
│   └── extract_assets.py        # UnityPy + PIL + ffmpeg asset & scene extractor
├── assets/                      # Extracted compact runtime assets (gitignored / non-public)
│   ├── textures/                # PNG (UI/Notes/Cutout) & JPEG (opaque diffuse/normal)
│   ├── lightmaps/               # lm_level1_*.jpg, lm_level2_*.jpg (256x256)
│   ├── audio/                   # *.ogg Vorbis clips (15 files)
│   ├── meshes/                  # {key}.mesh Deflate-compressed binary meshes
│   ├── fonts/consola.ttf        # Extracted TrueType font
│   ├── animations.json          # Legacy AnimationClips (Level Fade In / Out)
│   ├── materials.json           # Material definitions, colors, cutoffs, texture refs, hidden flag
│   ├── sprites.json             # Sprite ID -> texture path, rect, pixelsPerUnit
│   └── scenes/                  # scene_0.json, scene_1.json, scene_2.json
└── src/
    ├── Shaders/                 # Native Apple Metal (.metal) shaders validated via xcrun metal -c
    │   ├── standard_3d.metal
    │   ├── skybox_procedural.metal
    │   ├── ui_quad.metal
    │   ├── postfx_common.metal
    │   └── postfx_effects.metal
    ├── UnityShim/               # Clean-room UnityEngine / UnityEngine.UI / SceneManagement shim
    │   ├── UnityShim.csproj
    │   ├── Backend/
    │   │   ├── SdlNative.cs     # NativeLibrary resolver for libSDL3.0.dylib
    │   │   └── EngineHost.cs    # Shared host service locator & subsystem bridge interfaces
    │   ├── Core/                # Math, Attributes, Object/Component/GameObject/Transform, Animation, SceneManager, UI
    │   ├── Physics/             # Colliders, Rigidbody, Physics.Raycast, capsule-vs-mesh/OBB + triggers
    │   ├── AudioInput/          # Input/KeyCode/SDL3 relative mouse + NVorbis/SDL3 software audio mixer
    │   ├── Rendering/           # Camera/Shader/Material/RT/Graphics.Blit, FontAtlas, PostFxShaders, MetalRenderer
    │   └── Scene/               # AssetBank, SceneLoader (reflectively binds all GameScripts)
    ├── GameScripts/
    │   └── GameScripts.csproj   # Compiles ../../../decomp/csharp/*.cs verbatim (0 modifications!)
    └── AshTreeLane/
        ├── AshTreeLane.csproj   # Executable host bundling libSDL3.0.dylib & Shaders/*.metal
        └── Program.cs           # Main SDL3 loop, fixed physics, lifecycle, CLI test flags & F1-F6 post-fx
```

---

## 4. Measured Engine Semantics & Critical Port Details

### 4.1 MonoBehaviour Stripped Binary Schemas (All 71 Scene Instances)
Built Unity players strip MonoBehaviour typetrees (`has_typetree=0`). Each payload follows a 32B+aligned-`m_Name` header (`m_GameObject(12) + m_Enabled(4) + m_Script(12) + m_Name(4+pad4)`), decoded cleanly in `extract_assets.py` with an exact `offset == len(payload)` assertion:
- **Gameplay scripts**: `Menu`, `InstructionFade`, `DoorCollider`, `EndScript`, `FirstPersonMovement`, `FirstPersonLook`, `Crouch`, `Jump`, `GroundCheck`, `FirstPersonAudio`, `HallCollider`, `HallNotes`, `Note1`, `Note2`, `Note3`, `InteriorHouse`, `PlayerStartTurn`, `SoundPlayer`, `flashlight`.
- **UI scripts**: `Image`, `Text`, `CanvasScaler` (plus structural no-ops `EventSystem`, `GraphicRaycaster`, `StandaloneInputModule`).

### 4.2 Static Batching & Coordinate System
- **Unity Static Batching**: Objects in `level1` and `level2` whose `MeshRenderer` has `m_StaticBatchInfo.subMeshCount > 0` reference a shared `Combined Mesh` whose vertices are already baked in world space. The renderer MUST use `Matrix4x4.identity` as the model matrix (not `Transform.localToWorldMatrix`) and draw only submeshes `[firstSubMesh .. firstSubMesh + subMeshCount - 1]`.
- **Coordinate System & UV Orientation**: Unity is left-handed (`+X` right, `+Y` up, `+Z` forward). The renderer uses a matching left-handed view matrix and Metal `[0,1]` depth perspective projection. Because `StbImageSharp` decodes images top-down while Unity texture coordinates place `v=0` at the bottom, 3D shaders flip `v = 1.0 - v`, whereas 2D top-left UI quads sample `v` directly (`0` top → `1` bottom).
- **Built-in Tags**: Unity internal tag IDs map `0 → "Untagged"`, `5 → "MainCamera"`, `6 → "Player"`, `7 → "GameController"`.
- **Cutout Double-Sided Shaders**: `Ciconia Studio/Double Sided/Transparent/Diffuse Bump Cutout` binds `_DiffuseMapCutoutA` as albedo, enables alpha-clip discard (`_Cutoff`), and disables backface culling.
- **Hidden Editor/Trigger Materials**: `Hidden/ProBuilder/*`, `NoDraw`, `Trigger`, `Collider`, `InvisibleFace`, and `*HDRP` picker materials are tagged `hidden: true` and skipped during rendering.

### 4.3 SDL3 `SDL_GPU` Metal Binding Contract
Per `/opt/homebrew/opt/sdl3/include/SDL3/SDL_gpu.h`, graphics MSL shaders created via `SDL_CreateGPUShader` bind resources by stage in sequential slot order:
- Vertex shader: vertex inputs use `[[stage_in]]`; uniform buffers bind at `[[buffer(0)]]`, `[[buffer(1)]]`, ...; sampled textures/samplers (if any) at `[[texture(0)]]`, `[[sampler(0)]]`.
- Fragment shader: sampled textures bind at `[[texture(0)]]`, `[[texture(1)]]`, ... matching samplers at `[[sampler(0)]]`, `[[sampler(1)]]`, ...; uniform buffers bind at `[[buffer(0)]]`, `[[buffer(1)]]`, ....
- `SDL_GPUShaderCreateInfo.num_uniform_buffers` and `num_samplers` MUST equal the exact bindings declared and statically used/bound in each pipeline.

---

## 5. Verification Plan
1. **Asset & Compilation Gate**: `extract_assets.py` verifies `offset == len(payload)` across all 71 MonoBehaviour payloads; all `.metal` shaders pass `xcrun metal -c -std=macos-metal2.2`; `dotnet build AshTreeLane.sln -c Release` succeeds with 0 edits to `decomp/csharp/*.cs`.
2. **Scene Boot & Visual Parity**:
   - `level0`: Boot directly to title screen, verify PNG capture against [game_rebuilt.png](file:///Users/fabzter/src/rea_test_2-house-on/notes/game_rebuilt.png).
   - `level0 → level1`: Inject key press (e.g. `space`), verify `Menu.cs` triggers `Level Fade Out` and `SceneManager.LoadScene(1)`.
   - `level1`: Verify lightmapped exterior house/foliage/cutout ferns, `Instructions` overlay, and ground collision + trigger activation.
   - `level2`: Verify dark house interior illuminated by the `Spot` flashlight (`flashlight.cs`), trigger colliders, and notes.
3. **Post-FX Execution**: Run `--postfx` / `F1–F6` live toggles (`Bloom`, `Kuwahara`, `PixelArtFilter`, `DifferenceOfGaussians`, `Vignette`, etc.) exercising the verbatim `decomp/csharp/` `OnRenderImage` + `Graphics.Blit` chain onto Metal MSL shaders.
4. **Standalone Bundle**: `dotnet publish -c Release -r osx-arm64 --self-contained`, assemble `.app`, ad-hoc sign, launch standalone, and verify total `.app` footprint (<100 MB target vs 855 MB original).

---

## 6. Verified Build & Runtime Measurements
- **Verbatim Compilation**: `AshTreeLane.sln` (`UnityShim` + `GameScripts` compiling all 45 `decomp/csharp/*.cs` unmodified + `AshTreeLane`) builds with **0 warnings / 0 errors**. (`diff -rq decomp/csharp decomp/rebuilt-ref` confirms only `Assembly-CSharp.csproj` and `Properties/AssemblyInfo.cs` differ; all 45 decompiled scripts remain identical).
- **MSL Shader Compilation**: All `.metal` files (`standard_3d.metal`, `skybox_procedural.metal`, `ui_quad.metal`, `postfx_common.metal` + `postfx_effects.metal`) pass `xcrun metal -c -std=macos-metal2.2`.
- **Visual Fidelity & Low-Poly Tree Facet Parity (`standard_3d.metal` + `extract_assets.py` + `MetalRenderer.cs`)**:
  - Fixed static-batch `lightmapScaleOffset` double-application (`vu.lightmapST = (1,1,0,0)` when `isPartOfStaticBatch`).
  - Exported the `8×8` `tree_diffuse` palette losslessly (`sharedassets1_16_tree_diffuse.png`) with `CLAMP` wrap and `POINT`/`NEAREST` (`max_lod = 0`, no mipmaps) sampling, plus hardware `[[flat]]` normals for `FlatShaded` palette materials so every low-poly polygon facet on `Poplar_Tree`/`Fir_Tree` stays crisp.
  - Added full-resolution `1024×1024` IEC-sRGB `4:4:4` JPEG color lightmaps + `512×512` gutter-masked directional lightmaps (`m_DirLightmap`, `dm_*.png`), exact Unity 2021.1 `DecodeDirectionalLightmap`, Linear-workflow IEC sRGB conversion, `m_AmbientProbe` L2 SH (`shadeSH9`), mipmap generation (`SDL_GenerateMipmapsForGPUTexture`), and Unity `BRDF1_Unity_PBS` indirect specular IBL for all `Standard` materials.
  - Quantitative parity vs color-managed `unity_ref_level1.png`: `Poplar_Tree` foliage mean RGB `[34.0, 46.8, 23.6]` (`std [4.4, 5.0, 5.7]`, `edge(>2)=26.9%`) vs Unity `[34.9, 47.9, 24.3]` (`std [4.5, 4.7, 5.4]`, `edge(>2)=25.2%`); `Grey House` `[34.7, 42.3, 42.2]` vs Unity `[36.1, 42.0, 41.7]`; `Grass` `[13.8, 50.8, 6.3]` vs Unity `[17.1, 55.3, 6.4]`.
- **Asset Reduction (`extract_assets.py`)**: All 71 MonoBehaviour payloads across `level0`/`level1`/`level2` decoded with exact `offset == len(payload)` assertions; 49 referenced textures (**15 MB**), 207 meshes (**10 MB**), 127 color + 127 directional lightmaps (**21 MB**), 3 scene JSONs (**784 KB**), `consola.ttf` (**452 KB**), 15 Vorbis audio clips (**356 KB**) -> **47 MB** total extracted assets.
- **Self-Contained macOS App Footprint**:
  - `build/dist/macos/The House On Ash Tree Lane.app` (Unity runtime): **847 MB**
  - `build/unityfree/dist/macos/The House On Ash Tree Lane.app` (Unity-free SDL3/Metal): **131 MB** (self-contained `.NET 10` `osx-arm64` + `libSDL3.0.dylib` + `47 MB` assets) — **84.5% reduction (~6.47× smaller)**.
- **Verified Gameplay & Post-FX Screenshots** (`build/unityfree/verify/`):
  - Title screen (`level0.png`), `level0 -> level1` input transition, `level1` baked-lightmap exterior (`level1_exterior.png`, `level1_trees_v3.png`, `level1_exterior_v3.png`), side-by-side zoomed comparison vs live Unity (`zoom_trees_cmp_v3.png`), grounded walking (`level1_walk.png`), `DoorTrigger` `"Press E "` prompt (`level1_door_prompt.png`), `E`-key transition into `level2` (`level2.png`, `level2_v3.png`, `level2_interior.png`), live post-fx (`fx_bloom.png`, `fx_kuwahara.png`, `fx_pixelart.png`, `fx_dog.png`, `fx_vignette.png`), and signed standalone `.app` boot (`app_bundle_level1.png`).
