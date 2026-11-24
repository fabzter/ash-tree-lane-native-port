# QoL Plan — Unity-Free SDL3 Build (`build/unityfree/`)

Keep `decomp/csharp/*.cs` 100% verbatim; implement all QoL features inside `UnityShim` and the `AshTreeLane` host.

## 1. SDL3 Controller Support (`SdlInputBackend` + `EngineHost`)
- Initialize SDL with `SDL_INIT_GAMEPAD` alongside `SDL_INIT_VIDEO | SDL_INIT_AUDIO`, and enumerate already-connected pads via `SDL_GetGamepads`.
- Handle `SDL_EVENT_GAMEPAD_ADDED` / `SDL_EVENT_GAMEPAD_REMOVED` (hotplug), `SDL_EVENT_GAMEPAD_AXIS_MOTION`, and `SDL_EVENT_GAMEPAD_BUTTON_DOWN` / `UP`.
- Radial analog deadzone (`0.18f`) + right-stick look sensitivity (`GamepadLookSpeed = 65f`):
  - Left stick -> `Input.GetAxis("Horizontal")` / `Input.GetAxis("Vertical")` (inverting SDL `-Y` up).
  - Right stick -> `Input.GetAxis("Mouse X")` / `Input.GetAxis("Mouse Y")` (`Input.GetAxisRaw` included).
  - `SOUTH` (A) -> `KeyCode.Space` & `Input.GetButtonDown("Jump")`.
  - `WEST` (X) -> `KeyCode.E` (interact prompts/doors/notes).
  - D-pad Up/Down/Left/Right -> `KeyCode.W`/`S`/`A`/`D` fallback.
  - `START` -> toggle pause/settings menu (isolated from gameplay `Input`).
- Support runtime `ControllerEnabled` toggle without breaking keyboard/mouse input.

## 2. Resolution & Fullscreen Configuration (`MetalRenderer`, `UserSettings`, `Program`)
- Add `--width <W>`, `--height <H>`, `--fullscreen`, `--windowed`, `--config <path>`, `--no-controller`, and `--test-gamepad` CLI flags.
- Add `UserSettings` JSON config (`settings.json` stored next to `.app` or under `build/unityfree/`, defaulting to `1280x720`, windowed, controller enabled) loaded at startup and persisted on menu changes.
- Add `MetalRenderer.RecreateWindowAndSwapchain(int width, int height, bool fullscreen, bool restoreCursorLocked)` that waits for GPU idle, releases transient screen targets (`_sceneRt`, `_finalRt`, `_depthTex`), releases/destroys the old `SDL_Window*`, creates & claims the new window on the existing `SDL_GPUDevice*` (retaining loaded textures, meshes, lightmaps, and Metal pipelines), and updates `Screen.width`/`Screen.height`.

## 3. Pause / Settings Overlay Menu (`PauseMenuOverlay`, `MetalRenderer.OverlayDrawContext`)
- Open/close on `Escape` or Gamepad `START`.
- Pause simulation cleanly while open: set `Time.timeScale = 0f`, `Time.deltaTime = 0f`, suppress gameplay input injection, and skip physics + `Update`/`FixedUpdate`/`LateUpdate`/animation stepping while continuing `RenderFrame()` and `Time.unscaledTime`.
- Render overlay via `MetalRenderer`'s existing 2D UI quad & `FontAtlas` pipeline (`OverlayDrawContext`):
  - Main page: `Resume`, `Resolution` submenu, `Controller` enable toggle, `Quit`.
  - Resolution page: presets (`800x600`, `1024x576`, `1280x720`, `1600x900`, `1920x1080`), `Fullscreen` toggle, `Back`.
  - Navigate via keyboard (`Up`/`Down`/`Enter`/`Esc`), gamepad (`D-pad`/Left Stick/`South`/`East`/`Start`), and mouse hover/click.

## 4. Verification
- Build & sign bundle with `bash build/unityfree/build_app.sh`.
- Capture and inspect: pause menu (`qol_pause_main.png`), resolution submenu (`qol_pause_resolution.png`), runtime `1600x900` switch + config persistence (`qol_runtime_1600x900.png`, `qol_persisted_1600x900.png`), CLI overrides (`qol_cli_1024x576.png`, `qol_fullscreen.png`), and SDL3 virtual gamepad hotplug/axis/button/pause simulation (`--test-gamepad`, `qol_gamepad_look.png`).
- Confirm `diff -rq decomp/csharp decomp/rebuilt-ref` still reports verbatim 45-script parity.
