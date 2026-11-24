# Unity-free SDL3 rebuild — gotchas & pitfalls

Companion to `lessons-learned.md`. Everything learned removing Unity and running the verbatim
decompiled C# on SDL3 `SDL_GPU` (native Metal), plus matching the original's looks.
Game © GingerEnslaver; technique credit Andreansx. Skills: `unity-free-sdl3-rebuild`,
`port-fidelity-gap-audit`, `unity-no-editor-metal-shader-port`, `unity-mono-lib-only-recomp`.

## Backend & size
- **Measure first**: the 855 MB .app is ~777 MB of `.resS` **assets**, not the engine. Ditching Unity
  only wins if you also re-encode assets (textures→PNG/JPEG, lightmaps→small JPEG, meshes→Deflate,
  audio→OGG). 830→28 MB (48 MB with full-res lightmaps).
- **SDL3 `SDL_GPU`** compiles native **MSL** on Metal (`SDL_GPU_SHADERFORMAT_MSL`); offline-check with
  `xcrun metal -c -std=macos-metal2.2`. FNA needs Windows `fxc.exe` (FXBC) + its `fnalibs` 404s;
  MonoGame/raylib are OpenGL-only on macOS. Hence SDL3.
- .NET self-contained runtime ~100 MB+; final .app ~116–136 MB. Full lightmaps trade ~20 MB for fidelity.

## Shim / extraction pitfalls
- Keep `decomp/csharp/*.cs` **verbatim** (0 edits); the clean-room `UnityEngine` shim absorbs all API.
- Stripped MonoBehaviour payloads: 32 B header + aligned `m_Name`, then raw fields; assert
  `offset == len(payload)` for all 71 instances.
- **UnityPy PRE-FLIPS textures** — do not double-flip.
- **Static batching**: baked world-space verts → model matrix = identity; draw only
  `[firstSubMesh .. +subMeshCount)`.
- SDL_GPU MSL binding contract: vertex `[[stage_in]]`; uniforms `[[buffer(n)]]`, textures/samplers
  `[[texture(n)]]`/`[[sampler(n)]]` in sequential slot order per stage.

## Crashes
- SIGSEGV after scene load = Blit/post-fx or SDL_Quit teardown. **lldb --batch HANGS on CoreCLR** —
  isolate WITHOUT a debugger: `--no-postfx`/`--null-render` + per-step logging; bisect the SDL_GPU call
  (missing pipeline / unbound texture / released resource / wrong buffer slot).

## Visual fidelity ("it runs ≠ it matches")
- **Lightmap resolution**: original 1024² keeps low-poly facets crisp; 256² downscale blurs facets
  (bilinear bleed). Keep native res (+ directional lightmaps) for the stylish look.
- **Flat normals**: hardware `[[flat]]` in MSL for foliage; smoothing makes it blobby.
- **Palette textures** (8×8): point/nearest, lossless, no over-blur mipmaps.
- **Default post-fx**: original enables the painterly stack by default; match it (F1–F6).
- **Lighting**: ambient SH probe + Unity PBS indirect specular (SH9) for Standard materials.
- Verify quantitatively: foliage/grass mean/std/percentiles/histograms vs an original capture;
  side-by-side composite (`verify/side_by_side_v3.png`).

## Run & controls
- open the .app; WASD/mouse, E interact; F1 none/F2 bloom/F3 kuwahara/F4 pixelart/F5 dog/F6 vignette.
- CLI stills: `--scene 0/1/2 --postfx X --frames N --screenshot out.png [--send-key e:5]`.

## Delegation
- Neon (Antigravity) orchestrates; Hydrogen/sonnet workers per subsystem. Workers do long asset
  analysis before writing files — don't assume stalled; check the pane, nudge gently.
- Herdr: quota/models overlay swallows input (esc) but esc mid-turn cancels work; read before sending.