# QoL — controller, resolution, menu (Unity-free SDL3)

All in the shim/host; `decomp/csharp` stays verbatim. Verified via `--test-gamepad` (ALL CHECKS PASSED).

## Controller (SDL3 SDL_Gamepad)
- Init `SDL_INIT_VIDEO|AUDIO|JOYSTICK|GAMEPAD`; hints BEFORE SDL_Init:
  `SDL_JOYSTICK_HIDAPI=1`, `HIDAPI_PS4=1`, `HIDAPI_PS5=1`, **`HIDAPI_SWITCH=0`**,
  `ALLOW_BACKGROUND_EVENTS=1`.
  - **Pitfall**: `HIDAPI_SWITCH=1` lets the Switch driver hijack a PS4 pad → "Couldn't load stick
    calibration" / "Couldn't find mapping". Keep Switch OFF.
  - **Pitfall**: default macOS IOKit driver misses Bluetooth pads; HIDAPI needed.
  - **Pitfall**: bundled libSDL3 from NuGet/publish can be stale; build_app.sh copies the current
    homebrew libSDL3.0.dylib into the .app.
- **Poll axes each frame** (`SDL_GetGamepadAxis`, raw `SDL_GetJoystickAxis` fallback) — the
  `SDL_EVENT_GAMEPAD_AXIS_MOTION` marshaling is unreliable for BT PS4 (Apple MFi). Left stick →
  Horizontal/Vertical (move); right stick → Mouse X/Y (look).
- Mapping: A=jump/space, X=E interact, D-pad=WASD fallback, Start=pause menu. Radial deadzone 0.18.
- Raw-joystick fallback for pads with no SDL mapping.

## Resolution config
- CLI `--width/--height/--fullscreen/--windowed`; `UserSettings` JSON persisted + loaded at startup.
- Runtime change recreates window + SDL_GPU swapchain, retaining textures/meshes/pipelines.

## Pause / settings menu
- Esc or Start; `Time.timeScale=0` pauses sim; overlay via UI quad + FontAtlas.
- Pages: Resume / Resolution (presets + fullscreen) / Controller toggle / Quit.
- Navigate: keyboard (arrows/Enter/Esc), gamepad (D-pad/stick/A/B/Start), mouse hover/click.

## Verify
- `bash build/unityfree/build_app.sh`; run with `--test-gamepad` (virtual pad asserts axes/buttons/
  pause/raw-fallback) and physical pad; screenshots `verify/qol_*.png`.