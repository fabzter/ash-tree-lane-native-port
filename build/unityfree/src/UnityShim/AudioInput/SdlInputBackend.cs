using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SDL;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.AudioInput
{
    public unsafe sealed class SdlInputBackend : IInputBackend, IDisposable
    {
        private readonly HashSet<int> _held = new();
        private readonly HashSet<int> _gpMappedHeld = new();
        private readonly HashSet<int> _down = new();
        private readonly HashSet<int> _up = new();
        private readonly HashSet<int> _synthHeld = new();

        private readonly Dictionary<uint, nint> _gamepads = new();
        private readonly Dictionary<uint, nint> _rawJoysticks = new();
        private readonly HashSet<uint> _failedOpenIds = new();
        private readonly HashSet<SDL_GamepadButton> _gpButtonsHeld = new();
        private readonly HashSet<SDL_GamepadButton> _gpButtonsDown = new();
        private readonly List<SDL_GamepadButton> _menuGpButtonsDown = new();

        private float _mouseDx, _mouseDy;
        private float _gpLeftX, _gpLeftY, _gpRightX, _gpRightY;
        private float _horizSmoothed, _vertSmoothed;
        private float _lastDt = 1f / 60f;
        private bool _controllerEnabled = true;
        private bool _pauseToggleRequested;
        private byte _rawHatPrev;

        public float GamepadDeadzone { get; set; } = 0.18f;
        public float GamepadLookSpeed { get; set; } = 65f;
        public bool MenuPaused { get; set; }

        public bool ControllerEnabled
        {
            get => _controllerEnabled;
            set
            {
                _controllerEnabled = value;
                if (!value)
                {
                    ClearGamepadMappedState();
                }
            }
        }

        public int ConnectedGamepadCount => _gamepads.Count + _rawJoysticks.Count;
        public string ActiveGamepadName { get; private set; } = "None";

        public bool AnyKey => _held.Count > 0 || _down.Count > 0 || (_controllerEnabled && !MenuPaused && _gpMappedHeld.Count > 0);
        public bool AnyKeyDown => _down.Count > 0;

        public void InitializeGamepads() => RescanControllers();

        public void RescanControllers()
        {
            SDL3.SDL_UpdateJoysticks();
            SDL3.SDL_UpdateGamepads();

            int gcount = 0;
            SDL_JoystickID* gids = SDL3.SDL_GetGamepads(&gcount);
            if (gids != null)
            {
                try
                {
                    for (int i = 0; i < gcount; i++) OpenGamepadOrFallback(gids[i]);
                }
                finally
                {
                    SDL3.SDL_free((nint)gids);
                }
            }

            int jcount = 0;
            SDL_JoystickID* jids = SDL3.SDL_GetJoysticks(&jcount);
            if (jids != null)
            {
                try
                {
                    for (int i = 0; i < jcount; i++) OpenGamepadOrFallback(jids[i]);
                }
                finally
                {
                    SDL3.SDL_free((nint)jids);
                }
            }
        }

        public bool ConsumePauseToggleRequest()
        {
            bool v = _pauseToggleRequested;
            _pauseToggleRequested = false;
            return v;
        }

        public IReadOnlyList<SDL_GamepadButton> DrainMenuGamepadButtons() => _menuGpButtonsDown;

        private void ClearGamepadMappedState()
        {
            foreach (int kc in _gpMappedHeld)
            {
                if (!_held.Contains(kc)) _up.Add(kc);
            }
            _gpMappedHeld.Clear();
            _gpLeftX = 0f;
            _gpLeftY = 0f;
            _gpRightX = 0f;
            _gpRightY = 0f;
        }

        private void OpenGamepadOrFallback(SDL_JoystickID id)
        {
            uint key = (uint)id;
            if (_gamepads.ContainsKey(key) || _rawJoysticks.ContainsKey(key)) return;

            if (SDL3.SDL_IsGamepad(id))
            {
                SDL_Gamepad* gp = SDL3.SDL_OpenGamepad(id);
                if (gp != null)
                {
                    _gamepads[key] = (nint)gp;
                    _failedOpenIds.Remove(key);
                    byte* pName = SDL3.Unsafe_SDL_GetGamepadName(gp);
                    string name = pName != null ? (Marshal.PtrToStringUTF8((nint)pName) ?? "Gamepad") : "Gamepad";
                    ActiveGamepadName = name;
                    Console.WriteLine($"[Input] Gamepad connected ({id}): {name}");
                    return;
                }
                if (!_failedOpenIds.Contains(key))
                {
                    Console.Error.WriteLine($"[Input] SDL_OpenGamepad({id}) failed ({SdlNative.GetErrorString()}), trying raw joystick fallback...");
                }
            }

            OpenRawJoystick(id);
        }

        private void OpenRawJoystick(SDL_JoystickID id)
        {
            uint key = (uint)id;
            if (_rawJoysticks.ContainsKey(key) || _gamepads.ContainsKey(key)) return;
            SDL_Joystick* joy = SDL3.SDL_OpenJoystick(id);
            if (joy != null)
            {
                _rawJoysticks[key] = (nint)joy;
                _failedOpenIds.Remove(key);
                byte* pName = SDL3.Unsafe_SDL_GetJoystickName(joy);
                string name = pName != null ? (Marshal.PtrToStringUTF8((nint)pName) ?? "Raw Joystick") : "Raw Joystick";
                int na = SDL3.SDL_GetNumJoystickAxes(joy);
                int nb = SDL3.SDL_GetNumJoystickButtons(joy);
                int nh = SDL3.SDL_GetNumJoystickHats(joy);
                if (ActiveGamepadName == "None") ActiveGamepadName = name;
                else if (_gamepads.Count == 0) ActiveGamepadName = name;
                Console.WriteLine($"[Input] Raw joystick connected ({id}): {name} (axes={na}, btns={nb}, hats={nh})");
            }
            else if (_failedOpenIds.Add(key))
            {
                Console.Error.WriteLine($"[Input] SDL_OpenJoystick({id}) failed: {SdlNative.GetErrorString()}");
            }
        }

        private void CloseController(SDL_JoystickID id)
        {
            uint key = (uint)id;
            _failedOpenIds.Remove(key);
            bool removed = false;
            if (_gamepads.Remove(key, out nint gpPtr))
            {
                if (gpPtr != 0) SDL3.SDL_CloseGamepad((SDL_Gamepad*)gpPtr);
                Console.WriteLine($"[Input] Gamepad disconnected ({id})");
                removed = true;
            }
            if (_rawJoysticks.Remove(key, out nint jPtr))
            {
                if (jPtr != 0) SDL3.SDL_CloseJoystick((SDL_Joystick*)jPtr);
                _rawHatPrev = 0;
                Console.WriteLine($"[Input] Raw joystick disconnected ({id})");
                removed = true;
            }
            if (removed)
            {
                ClearGamepadMappedState();
                _gpButtonsHeld.Clear();
                RefreshActiveGamepadName();
            }
        }

        private void RefreshActiveGamepadName()
        {
            ActiveGamepadName = "None";
            foreach (nint ptr in _gamepads.Values)
            {
                if (ptr == 0) continue;
                byte* pName = SDL3.Unsafe_SDL_GetGamepadName((SDL_Gamepad*)ptr);
                ActiveGamepadName = pName != null ? (Marshal.PtrToStringUTF8((nint)pName) ?? "Gamepad") : "Gamepad";
                return;
            }
            foreach (nint ptr in _rawJoysticks.Values)
            {
                if (ptr == 0) continue;
                byte* pName = SDL3.Unsafe_SDL_GetJoystickName((SDL_Joystick*)ptr);
                ActiveGamepadName = pName != null ? (Marshal.PtrToStringUTF8((nint)pName) ?? "Raw Joystick") : "Raw Joystick";
                return;
            }
        }

        public void PollConnectedControllers()
        {
            if (ConnectedGamepadCount == 0) return;

            float lx = 0f, ly = 0f, rx = 0f, ry = 0f;
            static float PickMaxAbs(float cur, float cand) => MathF.Abs(cand) > MathF.Abs(cur) ? cand : cur;

            foreach (nint ptr in _gamepads.Values)
            {
                if (ptr == 0) continue;
                SDL_Gamepad* gp = (SDL_Gamepad*)ptr;
                lx = PickMaxAbs(lx, Math.Clamp(SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX) / 32767f, -1f, 1f));
                ly = PickMaxAbs(ly, Math.Clamp(SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY) / 32767f, -1f, 1f));
                rx = PickMaxAbs(rx, Math.Clamp(SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX) / 32767f, -1f, 1f));
                ry = PickMaxAbs(ry, Math.Clamp(SDL3.SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY) / 32767f, -1f, 1f));
            }
            foreach (nint ptr in _rawJoysticks.Values)
            {
                if (ptr == 0) continue;
                SDL_Joystick* joy = (SDL_Joystick*)ptr;
                int naxes = SDL3.SDL_GetNumJoystickAxes(joy);
                if (naxes >= 2)
                {
                    lx = PickMaxAbs(lx, Math.Clamp(SDL3.SDL_GetJoystickAxis(joy, 0) / 32767f, -1f, 1f));
                    ly = PickMaxAbs(ly, Math.Clamp(SDL3.SDL_GetJoystickAxis(joy, 1) / 32767f, -1f, 1f));
                }
                if (naxes >= 4)
                {
                    rx = PickMaxAbs(rx, Math.Clamp(SDL3.SDL_GetJoystickAxis(joy, 2) / 32767f, -1f, 1f));
                    ry = PickMaxAbs(ry, Math.Clamp(SDL3.SDL_GetJoystickAxis(joy, 3) / 32767f, -1f, 1f));
                }
            }

            _gpLeftX = lx;
            _gpLeftY = ly;
            _gpRightX = rx;
            _gpRightY = ry;
        }

        public string GetRawAxisDebugString() => $"L=({_gpLeftX:F2},{_gpLeftY:F2}) R=({_gpRightX:F2},{_gpRightY:F2})";

        public void BeginFrame()
        {
            _down.Clear();
            _up.Clear();
            _gpButtonsDown.Clear();
            _menuGpButtonsDown.Clear();
            _pauseToggleRequested = false;
            _mouseDx = 0f;
            _mouseDy = 0f;
        }

        public void ClearAllKeys()
        {
            _held.Clear();
            _gpMappedHeld.Clear();
            _down.Clear();
            _up.Clear();
            _synthHeld.Clear();
            _gpButtonsHeld.Clear();
            _gpButtonsDown.Clear();
            _menuGpButtonsDown.Clear();
            _rawHatPrev = 0;
            _mouseDx = 0f;
            _mouseDy = 0f;
            _horizSmoothed = 0f;
            _vertSmoothed = 0f;
        }

        public bool IsGamepadButtonDown(SDL_GamepadButton btn) => _gpButtonsDown.Contains(btn);

        public (float x, float y) GetLeftStickWithDeadzone()
        {
            if (!_controllerEnabled) return (0f, 0f);
            var (lx, ly) = ApplyRadialDeadzone(_gpLeftX, _gpLeftY, GamepadDeadzone);
            return (lx, -ly); // +Y = Up / Forward
        }

        private void ProcessGamepadButton(SDL_GamepadButton btn, bool isDown)
        {
            if (btn == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_INVALID) return;
            if (isDown)
            {
                if (_gpButtonsHeld.Add(btn))
                {
                    _gpButtonsDown.Add(btn);
                    if (_controllerEnabled)
                    {
                        if (btn == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START)
                        {
                            _pauseToggleRequested = true;
                        }
                        else if (MenuPaused)
                        {
                            _menuGpButtonsDown.Add(btn);
                        }
                    }
                }
            }
            else
            {
                _gpButtonsHeld.Remove(btn);
            }

            if (!_controllerEnabled || MenuPaused) return;

            int mappedKc = MapGamepadButtonToKeyCode(btn);
            if (mappedKc != 0)
            {
                if (isDown)
                {
                    if (_gpMappedHeld.Add(mappedKc) && !_held.Contains(mappedKc)) _down.Add(mappedKc);
                }
                else
                {
                    if (_gpMappedHeld.Remove(mappedKc) && !_held.Contains(mappedKc)) _up.Add(mappedKc);
                }
            }
        }

        public void HandleEvent(in SDL_Event e)
        {
            var type = e.Type;
            if (type == SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
            {
                OpenGamepadOrFallback(e.gdevice.which);
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED)
            {
                CloseController(e.gdevice.which);
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_JOYSTICK_ADDED)
            {
                OpenGamepadOrFallback(e.jdevice.which);
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_JOYSTICK_REMOVED)
            {
                CloseController(e.jdevice.which);
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION)
            {
                float norm = Math.Clamp(e.gaxis.value / 32767f, -1f, 1f);
                switch (e.gaxis.Axis)
                {
                    case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX: _gpLeftX = norm; break;
                    case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY: _gpLeftY = norm; break;
                    case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX: _gpRightX = norm; break;
                    case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY: _gpRightY = norm; break;
                }
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN || type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP)
            {
                ProcessGamepadButton(e.gbutton.Button, type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN);
                return;
            }

            if (type == SDL_EventType.SDL_EVENT_JOYSTICK_AXIS_MOTION)
            {
                if (!_rawJoysticks.ContainsKey((uint)e.jaxis.which)) return;
                float norm = Math.Clamp(e.jaxis.value / 32767f, -1f, 1f);
                switch (e.jaxis.axis)
                {
                    case 0: _gpLeftX = norm; break;
                    case 1: _gpLeftY = norm; break;
                    case 2: _gpRightX = norm; break;
                    case 3: _gpRightY = norm; break;
                }
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_DOWN || type == SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_UP)
            {
                if (!_rawJoysticks.ContainsKey((uint)e.jbutton.which)) return;
                SDL_GamepadButton btn = e.jbutton.button switch
                {
                    0 => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH,
                    1 => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
                    2 => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST,
                    3 => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH,
                    6 or 7 or 9 => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START,
                    _ => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_INVALID
                };
                ProcessGamepadButton(btn, type == SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_DOWN);
                return;
            }
            if (type == SDL_EventType.SDL_EVENT_JOYSTICK_HAT_MOTION)
            {
                if (!_rawJoysticks.ContainsKey((uint)e.jhat.which)) return;
                byte hv = e.jhat.value;
                byte diff = (byte)(hv ^ _rawHatPrev);
                _rawHatPrev = hv;
                if ((diff & 1) != 0) ProcessGamepadButton(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, (hv & 1) != 0);
                if ((diff & 2) != 0) ProcessGamepadButton(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, (hv & 2) != 0);
                if ((diff & 4) != 0) ProcessGamepadButton(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, (hv & 4) != 0);
                if ((diff & 8) != 0) ProcessGamepadButton(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, (hv & 8) != 0);
                return;
            }

            if (MenuPaused) return;

            if (type == SDL_EventType.SDL_EVENT_KEY_DOWN || type == SDL_EventType.SDL_EVENT_KEY_UP)
            {
                bool isDown = type == SDL_EventType.SDL_EVENT_KEY_DOWN;
                if (isDown && e.key.repeat) return;
                int kc = MapScancode(e.key.scancode);
                if (kc == 0) return;
                if (isDown)
                {
                    if (_held.Add(kc) && !_gpMappedHeld.Contains(kc)) _down.Add(kc);
                }
                else
                {
                    if (_held.Remove(kc) && !_gpMappedHeld.Contains(kc)) _up.Add(kc);
                }
            }
            else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
            {
                _mouseDx += e.motion.xrel;
                _mouseDy += e.motion.yrel;
            }
            else if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN || type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
            {
                bool isDown = type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;
                int mc = e.button.button switch
                {
                    1 => (int)KeyCode.Mouse0,
                    3 => (int)KeyCode.Mouse1,
                    2 => (int)KeyCode.Mouse2,
                    _ => 0
                };
                if (mc != 0)
                {
                    if (isDown) { if (_held.Add(mc)) _down.Add(mc); }
                    else { if (_held.Remove(mc)) _up.Add(mc); }
                }
            }
        }

        private static int MapGamepadButtonToKeyCode(SDL_GamepadButton btn) => btn switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => (int)KeyCode.Space,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => (int)KeyCode.E,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => (int)KeyCode.W,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => (int)KeyCode.S,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => (int)KeyCode.A,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => (int)KeyCode.D,
            _ => 0
        };

        private static (float x, float y) ApplyRadialDeadzone(float rx, float ry, float dz)
        {
            float mag = MathF.Sqrt(rx * rx + ry * ry);
            if (mag <= dz || mag < 1e-6f) return (0f, 0f);
            float scaled = Math.Clamp((mag - dz) / Math.Max(1e-4f, 1f - dz), 0f, 1f);
            float inv = scaled / mag;
            return (rx * inv, ry * inv);
        }

        public void InjectSyntheticKey(int keyCode, bool down)
        {
            if (down)
            {
                if (_held.Add(keyCode)) _down.Add(keyCode);
                _synthHeld.Add(keyCode);
            }
            else
            {
                if (_held.Remove(keyCode)) _up.Add(keyCode);
                _synthHeld.Remove(keyCode);
            }
        }

        public void InjectSyntheticMouseDelta(float dx, float dy)
        {
            _mouseDx += dx;
            _mouseDy += dy;
        }

        public void UpdateSmoothedAxes(float dt)
        {
            _lastDt = Math.Clamp(dt, 0.001f, 0.1f);
            float targetH = RawHoriz();
            float targetV = RawVert();
            float step = MathF.Min(1f, MathF.Max(0.15f, dt * 10f));
            _horizSmoothed = MathF.Abs(targetH) > 0.01f ? Mathf.MoveTowards(_horizSmoothed, targetH, step) : Mathf.MoveTowards(_horizSmoothed, 0f, step * 1.5f);
            _vertSmoothed = MathF.Abs(targetV) > 0.01f ? Mathf.MoveTowards(_vertSmoothed, targetV, step) : Mathf.MoveTowards(_vertSmoothed, 0f, step * 1.5f);
        }

        public bool GetKey(int keyCode) => _held.Contains(keyCode) || (_controllerEnabled && !MenuPaused && _gpMappedHeld.Contains(keyCode));
        public bool GetKeyDown(int keyCode) => _down.Contains(keyCode);
        public bool GetKeyUp(int keyCode) => _up.Contains(keyCode);

        public bool GetKeyDownName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int kc = NameToKeyCode(name);
            return kc != 0 && _down.Contains(kc);
        }

        private float RawHoriz()
        {
            if (MenuPaused) return 0f;
            float v = 0f;
            if (GetKey((int)KeyCode.A) || GetKey((int)KeyCode.LeftArrow)) v -= 1f;
            if (GetKey((int)KeyCode.D) || GetKey((int)KeyCode.RightArrow)) v += 1f;
            if (_controllerEnabled)
            {
                var (lx, _) = ApplyRadialDeadzone(_gpLeftX, _gpLeftY, GamepadDeadzone);
                v += lx;
            }
            return Math.Clamp(v, -1f, 1f);
        }

        private float RawVert()
        {
            if (MenuPaused) return 0f;
            float v = 0f;
            if (GetKey((int)KeyCode.S) || GetKey((int)KeyCode.DownArrow)) v -= 1f;
            if (GetKey((int)KeyCode.W) || GetKey((int)KeyCode.UpArrow)) v += 1f;
            if (_controllerEnabled)
            {
                var (_, ly) = ApplyRadialDeadzone(_gpLeftX, _gpLeftY, GamepadDeadzone);
                v -= ly; // SDL LEFTY up is negative -> Unity Vertical forward is positive
            }
            return Math.Clamp(v, -1f, 1f);
        }

        private float LookMouseX()
        {
            if (MenuPaused) return 0f;
            float v = _mouseDx * 0.1f;
            if (_controllerEnabled)
            {
                var (rx, _) = ApplyRadialDeadzone(_gpRightX, _gpRightY, GamepadDeadzone);
                v += rx * GamepadLookSpeed * _lastDt;
            }
            return v;
        }

        private float LookMouseY()
        {
            if (MenuPaused) return 0f;
            float v = -_mouseDy * 0.1f;
            if (_controllerEnabled)
            {
                var (_, ry) = ApplyRadialDeadzone(_gpRightX, _gpRightY, GamepadDeadzone);
                v -= ry * GamepadLookSpeed * _lastDt; // SDL RIGHTY up is negative -> +Mouse Y
            }
            return v;
        }

        public float GetAxis(string axisName) => axisName switch
        {
            "Horizontal" => _horizSmoothed,
            "Vertical" => _vertSmoothed,
            "Mouse X" => LookMouseX(),
            "Mouse Y" => LookMouseY(),
            _ => 0f
        };

        public float GetAxisRaw(string axisName) => axisName switch
        {
            "Horizontal" => RawHoriz(),
            "Vertical" => RawVert(),
            "Mouse X" => LookMouseX(),
            "Mouse Y" => LookMouseY(),
            _ => 0f
        };

        public bool GetButtonDown(string buttonName) => buttonName switch
        {
            "Jump" => _down.Contains((int)KeyCode.Space),
            _ => false
        };

        public void SetCursorLocked(bool locked)
        {
            if (EngineHost.WindowHandle != null)
            {
                SDL3.SDL_SetWindowRelativeMouseMode(EngineHost.WindowHandle, locked);
            }
        }

        public static int NameToKeyCode(string name)
        {
            return name.Trim().ToLowerInvariant() switch
            {
                "space" => (int)KeyCode.Space,
                "return" or "enter" => (int)KeyCode.Return,
                "escape" or "esc" => (int)KeyCode.Escape,
                "left shift" or "leftshift" => (int)KeyCode.LeftShift,
                "right shift" or "rightshift" => (int)KeyCode.RightShift,
                "left ctrl" or "leftcontrol" => (int)KeyCode.LeftControl,
                "up" or "uparrow" => (int)KeyCode.UpArrow,
                "down" or "downarrow" => (int)KeyCode.DownArrow,
                "left" or "leftarrow" => (int)KeyCode.LeftArrow,
                "right" or "rightarrow" => (int)KeyCode.RightArrow,
                "w" => (int)KeyCode.W, "a" => (int)KeyCode.A, "s" => (int)KeyCode.S,
                "d" => (int)KeyCode.D, "e" => (int)KeyCode.E, "f" => (int)KeyCode.F,
                _ => name.Length == 1 && name[0] is >= 'a' and <= 'z' ? (int)name[0] : 0
            };
        }

        public static SDL_Scancode KeyCodeToScancode(int keyCode) => (KeyCode)keyCode switch
        {
            KeyCode.Escape => SDL_Scancode.SDL_SCANCODE_ESCAPE,
            KeyCode.Return => SDL_Scancode.SDL_SCANCODE_RETURN,
            KeyCode.Space => SDL_Scancode.SDL_SCANCODE_SPACE,
            KeyCode.UpArrow => SDL_Scancode.SDL_SCANCODE_UP,
            KeyCode.DownArrow => SDL_Scancode.SDL_SCANCODE_DOWN,
            KeyCode.LeftArrow => SDL_Scancode.SDL_SCANCODE_LEFT,
            KeyCode.RightArrow => SDL_Scancode.SDL_SCANCODE_RIGHT,
            KeyCode.W => SDL_Scancode.SDL_SCANCODE_W,
            KeyCode.A => SDL_Scancode.SDL_SCANCODE_A,
            KeyCode.S => SDL_Scancode.SDL_SCANCODE_S,
            KeyCode.D => SDL_Scancode.SDL_SCANCODE_D,
            KeyCode.E => SDL_Scancode.SDL_SCANCODE_E,
            KeyCode.F => SDL_Scancode.SDL_SCANCODE_F,
            _ => SDL_Scancode.SDL_SCANCODE_UNKNOWN
        };

        private static int MapScancode(SDL_Scancode sc)
        {
            if (sc >= SDL_Scancode.SDL_SCANCODE_A && sc <= SDL_Scancode.SDL_SCANCODE_Z)
                return (int)KeyCode.A + (int)(sc - SDL_Scancode.SDL_SCANCODE_A);
            if (sc >= SDL_Scancode.SDL_SCANCODE_1 && sc <= SDL_Scancode.SDL_SCANCODE_9)
                return (int)KeyCode.Alpha1 + (int)(sc - SDL_Scancode.SDL_SCANCODE_1);
            if (sc == SDL_Scancode.SDL_SCANCODE_0) return (int)KeyCode.Alpha0;
            return sc switch
            {
                SDL_Scancode.SDL_SCANCODE_RETURN => (int)KeyCode.Return,
                SDL_Scancode.SDL_SCANCODE_ESCAPE => (int)KeyCode.Escape,
                SDL_Scancode.SDL_SCANCODE_BACKSPACE => (int)KeyCode.Backspace,
                SDL_Scancode.SDL_SCANCODE_TAB => (int)KeyCode.Tab,
                SDL_Scancode.SDL_SCANCODE_SPACE => (int)KeyCode.Space,
                SDL_Scancode.SDL_SCANCODE_RIGHT => (int)KeyCode.RightArrow,
                SDL_Scancode.SDL_SCANCODE_LEFT => (int)KeyCode.LeftArrow,
                SDL_Scancode.SDL_SCANCODE_DOWN => (int)KeyCode.DownArrow,
                SDL_Scancode.SDL_SCANCODE_UP => (int)KeyCode.UpArrow,
                SDL_Scancode.SDL_SCANCODE_F1 => (int)KeyCode.F1,
                SDL_Scancode.SDL_SCANCODE_F2 => (int)KeyCode.F2,
                SDL_Scancode.SDL_SCANCODE_F3 => (int)KeyCode.F3,
                SDL_Scancode.SDL_SCANCODE_F4 => (int)KeyCode.F4,
                SDL_Scancode.SDL_SCANCODE_F5 => (int)KeyCode.F5,
                SDL_Scancode.SDL_SCANCODE_F6 => (int)KeyCode.F6,
                SDL_Scancode.SDL_SCANCODE_F12 => (int)KeyCode.F12,
                SDL_Scancode.SDL_SCANCODE_LCTRL => (int)KeyCode.LeftControl,
                SDL_Scancode.SDL_SCANCODE_LSHIFT => (int)KeyCode.LeftShift,
                SDL_Scancode.SDL_SCANCODE_LALT => (int)KeyCode.LeftAlt,
                SDL_Scancode.SDL_SCANCODE_RCTRL => (int)KeyCode.RightControl,
                SDL_Scancode.SDL_SCANCODE_RSHIFT => (int)KeyCode.RightShift,
                _ => 0
            };
        }

        public void Dispose()
        {
            foreach (nint ptr in _gamepads.Values)
            {
                if (ptr != 0) SDL3.SDL_CloseGamepad((SDL_Gamepad*)ptr);
            }
            _gamepads.Clear();
            foreach (nint ptr in _rawJoysticks.Values)
            {
                if (ptr != 0) SDL3.SDL_CloseJoystick((SDL_Joystick*)ptr);
            }
            _rawJoysticks.Clear();
        }
    }
}
