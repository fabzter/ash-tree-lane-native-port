# Native macOS Metal Shader Port Plan — *The House On Ash Tree Lane* (Unity 2021.1.11f1)

## Goal

Make `build/dist/macos/The House On Ash Tree Lane.app` render natively on Apple Silicon Metal without using the Unity Editor, keeping all source assets in `extracted/The House On Ash Tree Lane_Data/` untouched and bounding all build artifacts to `/Users/fabzter/src/rea_test_2-house-on`.

Currently the macOS app launches windowed via `MainMenu.nib` + `NSPrincipalClass=PlayerApplication`, falls back to `NullGfxDevice` because the Windows build ships D3D11-only shaders and D3D11 settings in `globalgamemanagers`, and crashes in `_NSViewDrawRect`.

---

## 1. Shader Inventory & Stock-vs-Custom Split

Audited across all serialized files in `extracted/The House On Ash Tree Lane_Data/`:

| Source File | Shaders | Platforms | Entries / Types | Handling |
|---|---:|---|---|---|
| `Resources/unity default resources` | 6 (+4 ComputeShaders) | `[4, 5, 9, 14, 15, 18]` | Already carries Metal (`14`) | Keep as-is (verified identical set to Mac player's `unity default resources`) |
| `Resources/unity_builtin_extra` | 42 | `[4]` (D3D11-only) | 403 subprograms (`15, 16, 17, 18, 19`) | Convert offline via HLSLcc (see below) |
| `resources.assets` | 9 | `[4]` (D3D11-only) | `Standard`, `Legacy Shaders/Particles/Alpha Blended`, `Unlit/Texture`, 6 `ProBuilder/*` | Convert offline via HLSLcc |
| `sharedassets0.assets` | 1 | `[4]` (D3D11-only) | `Skybox/Procedural` | Convert offline via HLSLcc |
| `sharedassets2.assets` | 2 | `[4]` (D3D11-only) | `Ciconia Studio/Double Sided/Transparent/Diffuse Bump Cutout` (pids 179, 180) | Convert offline via HLSLcc |

Total: **60 Shaders** (6 already Metal + **54 D3D11-only**). Total D3D11 blob entries across the 54 shaders: **1,580** (`15`: 720, `17`: 853, `16`: 3, `18`: 3, `19`: 1), **0 multi-segment blobs**.

### Why both stock (`unity_builtin_extra`) and game shaders use the offline HLSLcc path
- The extracted Mac player module (`build/players/extracted/mac/TargetSupport.pkg.tmp/Payload/Variations/macos_arm64_nondevelopment_mono/UnityPlayer.app/Contents/Resources/`) only ships `unity default resources` (the 6 built-in core shaders + 4 compute shaders). It does **not** ship `unity_builtin_extra` (which Unity generates per-project in the Editor).
- Unlike Unity 2022.3 builds where parameter tables are split across `m_CommonParameters` and `m_ParameterBlobIndices`, Unity 2021.1.11f1 embeds the complete reflection table (`ParserBindChannels` + `GpuProgramParameters`) directly in the trailing bytes of **every single D3D11 sub-program code entry**.
- Therefore, all 42 `unity_builtin_extra` shaders and all 12 game shaders (`resources.assets`, `sharedassets0.assets`, `sharedassets2.assets`) can be converted uniformly with **zero donor build required**.

### Special cases audited
1. **Geometry shader (`gtype=19`)**: Only 1 entry exists across the entire game: `Hidden/VR/BlitFromTex2DToTexArraySlice` (`Resources/unity_builtin_extra`) `m_SubShaders[1]`, which is a fallback GS subshader for GPUs without `SV_RenderTargetArrayIndex` from vertex shaders. Metal supports VS RT-array index, and `m_SubShaders[0]` uses VS+FS only (`entries[0]` and `entries[1]`). Dropping `m_SubShaders[1]` leaves a valid Metal-compatible shader with 0 GS entries.
2. **Unused post-processing scripts**: `MonoBehaviour` audit across `level0`, `level1`, `level2` shows none of the image-effect scripts in `decomp/csharp/` (`Kuwahara`, `Bloom`, `ExtendedDoG`, `Ditherer`, etc.) are attached to scenes, and all 100+ `Material` shader references resolve to the 54 converted shaders plus `unity default resources`. No `Assembly-CSharp.dll` modifications are needed.

---

## 2. Unity 2021.1.11f1 vs 2022.3 Format Differences (Verified against `unity default resources` & `UnityPlayer.dylib`)

Comparing paired D3D11 (`plat=4`) and Metal (`plat=14`) entries in `Resources/unity default resources` and disassembling `LoadGpuProgramFromData` (`0x65ee0c`), `CreateMetalShaderShaderLab` (`0xd26fe0`), `GpuProgramMetal::CreateFromShaderLabSource` (`0xd260a4`), and `CreateMTLLibraryFromSource` (`0xd26804`):

1. **Shader Typetree (`m_ParsedForm`)**:
   - Unity 2021.1 stores variants in `progVertex.m_SubPrograms` / `progFragment.m_SubPrograms` (no `m_PlayerSubPrograms`, no `m_ParameterBlobIndices`).
   - `m_CommonParameters` and `m_SubPrograms[*].m_Parameters` are empty; all reflection/binding parameters live inside each blob entry's trailing bytes.
   - Each `sp` in `m_SubPrograms` stores `m_BlobIndex`, `m_GpuProgramType` (`15/16 -> 23`, `17/18 -> 24`), and `m_Channels` (`{"m_Channels": [{"source": chan, "target": tgt}], "m_SourceMap": mask}`). For Metal VS, `target = attr + 13`, and **both** the blob entry's trailing `ParserBindChannels` and `sp["m_Channels"]` must be updated.

2. **Blob Table & Entry Outer Wrapper**:
   - Blob container header: `u32 count`, followed by `count * (u32 offset, u32 length, u32 segment)`.
   - Wrapper magic: **`0x0C07513C`** (2022.3 used `0x0C0A75BA`).
   - Layout:
     - `u32 magic = 0x0C07513C`
     - `u32 gpuProgramType` (`23` = Metal VS, `24` = Metal FS)
     - `4 * u32 stats` (`(0, 0, 0, 0)` for Metal)
     - `u32 globalKwCount` + `globalKwCount * (u32 len + bytes padded to 4)`
     - **`u32 localKwCount` + `localKwCount * (u32 len + bytes padded to 4)`** (2021.1 serializes *both* global and local keyword lists)
     - `u32 payloadLen`
     - `bytes payload[payloadLen]`
     - **0–3 zero pad bytes** aligning the end of payload to a 4-byte boundary (`(payloadLen + 3) & ~3`, verified in `LoadGpuProgramFromData` @ `0x65f3a8`)
     - `bytes trailing` = `ParserBindChannels` + `GpuProgramParameters`.

3. **Metal `0xF00DCAFE` Payload Header**:
   - **11 dwords (44 bytes)** in 2021.1 (2022.3 used 12 dwords / 48 bytes):
     ```python
     struct.pack("<11I", 0xF00DCAFE, 44, 4, 56, 0, 0, 0, 0, 0, 0x05, 0xFFFFFFFF)
     ```
     - `hdr[1] = 44` (`nameOffset`), followed by `b"xlatMtlMain\0"` (12 bytes -> ends at `56`)
     - `hdr[2] = 4` (version >= 4)
     - `hdr[3] = 56` (`srcOffset`), followed by UTF-8 MSL source (`srcLen = payloadLen - 56`)
     - `hdr[9] = 0x05` (bit 2 = `0x04` tells `CreateFromShaderLabSource` to use binary `GpuProgramParameters` without running legacy text buffer upgrades)
     - `hdr[10] = 0xFFFFFFFF`.

4. **MSL Dialect**:
   - Unity 2021.1 natively defines `#define XLT_REMAP_O {...}` at `MTLLibrary` compile time (`CreateMTLLibraryFromSource` @ `0xd26804`). HLSLcc's `#ifndef XLT_REMAP_O ... constexpr constant uint xlt_remap_o[] = XLT_REMAP_O;` is used verbatim — **no** 2022.3 `rp_output_remap_mask` rewrite and **no** `invariant` VS rewrite.

5. **Trailing `ParserBindChannels` + `GpuProgramParameters` Binary Format**:
   - Verified across all 1,668 entries (1,580 D3D11 + 88 Metal in `unity default resources`) with 0 parse errors:
     - `ParserBindChannels`: `u32 sourceMap`, `u32 nCh`, `nCh * (u32 source, u32 target)`
     - `u32 nCBs`: for each CB:
       - `str cbName` (`u32 len + bytes padded to 4`; CB[0] is `""`, size 0, 0 params, 0 structs)
       - `u32 cbSize`, `u32 nParams`
       - `nParams * (str name, u32 ptype, u32 rows, u32 cols, u32 isMat, u32 arraySize, u32 byteOffset)` (`ptype`: `0=float`, `1=int`, `2=bool`)
       - `u32 nStructs` (always `0`)
     - `u32 nBinds`: for each bind:
       - `str name`, `u32 kind`
       - `kind == 0` (texture): `u32 texIndex, u32 samplerIndex, u32 packedDim` (`0xFFFFFFFF` if unsampled, e.g. `Texture2DMS`)
       - `kind == 1` (CB bind): `u32 cbSlot, u32 extra` (`0`).

---

## 3. RDEF Reflection Synthesis & CB Struct Re-layout

Unity strips the `RDEF` chunk from compiled D3D11 DXBC blobs (leaving `ISGN`, `OSGN`/`OSG5`, `SHDR`/`SHEX`). `dxbc2msl_2021.py` reconstructs `RDEF`:
1. Parse `SHDR`/`SHEX` `dcl_constantbuffer` (`0x59`), `dcl_resource` (`0x58`), and `dcl_sampler` (`0x5A`) to determine exact active slots, shader stage, shader model (`major=4` vs `5`), and resource dimensions (including `Texture2DMS` -> `D3D_SRV_DIMENSION_TEXTURE2DMS`).
2. Populate CB names, member names/types/offsets, texture names, and sampler names (`"sampler" + texName` mapped via `tex.sampler`, handling decoupled texture/sampler slots and comparison samplers) from the entry's own trailing `GpuProgramParameters`.
3. Compute D3D11-accurate array byte extents (`(elems - 1) * 16 + cols * 4` for scalar/vector arrays, `elems * rows * 16` for matrices) and fill uncovered 16-byte rows with pad `float4`s so every DXBC `cbN[idx]` load resolves.
4. Translate via `hlslcc-cli` (`TRANSLATE_MATRICES | REMOVE_UNUSED_GLOBALS | METAL_SHADOW_SAMPLER_LINEAR`).
5. **Re-layout MSL CB structs** (`relayout_cb_structs`): rebuild every `constant X& [[buffer(slot)]]` struct so each named member sits at its exact serialized `byteOffset` (because Unity's `GpuProgramMetal::ApplyGpuProgram` writes constants by serialized `byteOffset` and binds CBs by slot). Any D3D-packed `float2/3/4` at a 4-mod-8/16 offset uses MSL `packed_float2/3/4` (`packed_int*`, `packed_uint*`) with reads wrapped as `floatN(cb.member)`.
6. **Rebuild VS `ParserBindChannels`** from the generated MSL's `[[attribute(n)]]` declarations (`target = attr + 13`), updating both the entry's trailing `ParserBindChannels` and `sp["m_Channels"]` in `m_ParsedForm`, while preserving `GpuProgramParameters` verbatim.

---

## 4. `globalgamemanagers` Patching & Integration

- Patch `globalgamemanagers` surgically (4 bytes each, never re-serialized with UnityPy):
  1. `GraphicsSettings.m_ShaderDefinesPerShaderCompiler[0].shaderPlatform`: `4 -> 14` (Metal)
  2. `BuildSettings.m_GraphicsAPIs[0]`: `2 -> 16` (`kGraphicsDeviceMetal`)
- Output converted assets (`globalgamemanagers`, `resources.assets`, `sharedassets0.assets`, `sharedassets2.assets`, `Resources/unity_builtin_extra`) to `build/shaders/metal_assets/` (source `extracted/` remains pristine).
- Update `build/assemble.py` (`build_macos`) to overlay `build/shaders/metal_assets/` onto `Contents/Resources/Data/`, stamp `CFBundleVersion` / `CFBundleShortVersionString` to `1.0.2`, and `codesign --force --deep -s -`.

---

## 5. Verification Gates

1. **Compile gate**: 54/54 shaders convert with 0 errors and 100% of generated MSL programs pass `xcrun metal -c -std=macos-metal2.2`.
2. **Binary structure gate (`verify_metal_2021.py`)**: 60/60 shaders in the built `.app` carry platform `14`, valid `0x0C07513C` / `0xF00DCAFE` headers, valid trailing `GpuProgramParameters`, and `globalgamemanagers` targets Metal (`shaderPlatform=14`, `m_GraphicsAPIs=[16]`).
3. **Signature gate**: `codesign --verify --deep --strict` passes.
4. **Runtime gate**: Launch `build/dist/macos/The House On Ash Tree Lane.app` with `-logfile`; verify Metal device initialization (no `NullGfxDevice`), Mono reload, 0 shader/pipeline errors, and no `_NSViewDrawRect` crash.
