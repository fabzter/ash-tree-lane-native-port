# Unity-free rebuild — context & constraints (for the implementing agent)

Goal: **remove the Unity dependency entirely** and run the decompiled game on a lightweight
backend, producing a much smaller macOS (and Win/Linux) build. Current Mac .app is ~855 MB.

## Size reality (measured)
The bulk is ASSET data, not the engine: `level1.resS` 372 MB + `sharedassets2.resS` 405 MB +
`sharedassets1.resS` 34 MB (streaming textures/audio/meshes). Managed DLLs 21 MB. So a Unity-free
port MUST also **re-encode/extract assets** (textures → compressed GPU formats or PNG, audio → OGG,
meshes → compact binary) to actually shrink. Extract via UnityPy from `extracted/…_Data/`.

## UnityEngine API surface used (measured from decomp/csharp, 45 types)
- Post-fx (heaviest): RenderTexture(125), Graphics.Blit(56), Material(44), OnRenderImage(23), Shader(22).
- Lifecycle: MonoBehaviour(45), Start/Update/Awake/OnEnable/OnDisable/FixedUpdate/LateUpdate.
- Input(36)/KeyCode(25); math Vector3/Vector2/Quaternion/Mathf/Color.
- Audio: AudioSource(20)/AudioClip(6).
- Physics: Collider(16)/OnTriggerEnter(8)/OnTriggerExit(5)/Rigidbody(7)/Physics.Raycast(1).
- Camera(11), GameObject/Transform/GetComponent/CompareTag, SceneManager(5), UI Text/Image/Canvas,
  Animation(5), Cursor/Screen.

## Backend research (2026-11-24)
- **FNA** (recommended): C# XNA reimpl; **native Metal on macOS**; RenderTarget2D + Effect(shaders) +
  SpriteBatch + audio; single cross-platform project; small. Ms-PL.
- MonoGame: C#, broader ecosystem, but **OpenGL-only on macOS** (no Metal), MGFX shader format.
- raylib-cs: smallest binary, C bindings; Mac uses OpenGL; less high-level parity.
Recommendation: FNA. Keep decompiled C# verbatim behind a **UnityEngine-compatible shim** so parity
is preserved; the shim maps Unity calls onto FNA.

## Architecture to implement
1. `UnityEngine` shim assembly (namespace UnityEngine + UnityEngine.SceneManagement etc.) implementing
   the measured surface on FNA: GameObject/Component/MonoBehaviour lifecycle + Transform hierarchy,
   math structs, Input, AudioSource/AudioClip, Camera + rendering, RenderTexture + Graphics.Blit +
   Material/Shader (post-fx via FNA Effect), physics triggers + raycast (simple AABB/sphere or a lib),
   SceneManager (load the 3 scenes), basic UI.
2. Asset pipeline: extract textures/audio/meshes/scenes from Unity assets (UnityPy) into a compact
   runtime format; re-encode to shrink; load at runtime.
3. Post-fx shaders: port the image-effect shaders (Kuwahara/Bloom/DoG/PixelArtFilter) to FNA Effect
   (HLSL) — reuse MSL/HLSL knowledge from build/metal_port.
4. Build: dotnet publish self-contained (or bundled runtime) per platform; target small size.
5. Verify: game boots to title, renders, input works, scenes load, post-fx visible; compare to Unity build.

## Constraints
- No Unity editor / no Unity runtime at runtime. Bounded to this project dir.
- Use worker agents (sonnet/neon) via agy for parallel subsystems; you (Neon) architect + integrate.
- Honor original author (GingerEnslaver); do not redistribute game assets in the public repo.