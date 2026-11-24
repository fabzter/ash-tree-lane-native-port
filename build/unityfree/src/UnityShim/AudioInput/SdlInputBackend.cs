using System;
using System.Collections.Generic;
using SDL;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.AudioInput
{
    public unsafe sealed class SdlInputBackend : IInputBackend
    {
        private readonly HashSet<int> _held = new();
        private readonly HashSet<int> _down = new();
        private readonly HashSet<int> _up = new();
        private readonly HashSet<int> _synthHeld = new();
        private float _mouseDx, _mouseDy;
        private float _horizSmoothed, _vertSmoothed;

        public bool AnyKey => _held.Count > 0 || _down.Count > 0;
        public bool AnyKeyDown => _down.Count > 0;

        public void BeginFrame()
        {
            _down.Clear();
            _up.Clear();
            _mouseDx = 0f;
            _mouseDy = 0f;
        }

        public void ClearAllKeys()
        {
            _held.Clear();
            _down.Clear();
            _up.Clear();
            _synthHeld.Clear();
        }

        public void HandleEvent(in SDL_Event e)
        {
            var type = e.Type;
            if (type == SDL_EventType.SDL_EVENT_KEY_DOWN || type == SDL_EventType.SDL_EVENT_KEY_UP)
            {
                bool isDown = type == SDL_EventType.SDL_EVENT_KEY_DOWN;
                if (isDown && e.key.repeat) return;
                int kc = MapScancode(e.key.scancode);
                if (kc == 0) return;
                if (isDown)
                {
                    if (_held.Add(kc)) _down.Add(kc);
                }
                else
                {
                    if (_held.Remove(kc)) _up.Add(kc);
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
            float targetH = RawHoriz();
            float targetV = RawVert();
            float step = MathF.Min(1f, MathF.Max(0.15f, dt * 10f));
            _horizSmoothed = MathF.Abs(targetH) > 0.01f ? Mathf.MoveTowards(_horizSmoothed, targetH, step) : Mathf.MoveTowards(_horizSmoothed, 0f, step * 1.5f);
            _vertSmoothed = MathF.Abs(targetV) > 0.01f ? Mathf.MoveTowards(_vertSmoothed, targetV, step) : Mathf.MoveTowards(_vertSmoothed, 0f, step * 1.5f);
        }

        public bool GetKey(int keyCode) => _held.Contains(keyCode);
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
            float v = 0f;
            if (_held.Contains((int)KeyCode.A) || _held.Contains((int)KeyCode.LeftArrow)) v -= 1f;
            if (_held.Contains((int)KeyCode.D) || _held.Contains((int)KeyCode.RightArrow)) v += 1f;
            return v;
        }

        private float RawVert()
        {
            float v = 0f;
            if (_held.Contains((int)KeyCode.S) || _held.Contains((int)KeyCode.DownArrow)) v -= 1f;
            if (_held.Contains((int)KeyCode.W) || _held.Contains((int)KeyCode.UpArrow)) v += 1f;
            return v;
        }

        public float GetAxis(string axisName) => axisName switch
        {
            "Horizontal" => _horizSmoothed,
            "Vertical" => _vertSmoothed,
            "Mouse X" => _mouseDx * 0.1f,
            "Mouse Y" => -_mouseDy * 0.1f,
            _ => 0f
        };

        public float GetAxisRaw(string axisName) => axisName switch
        {
            "Horizontal" => RawHoriz(),
            "Vertical" => RawVert(),
            "Mouse X" => _mouseDx * 0.1f,
            "Mouse Y" => -_mouseDy * 0.1f,
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
    }
}
