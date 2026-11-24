using System;
using System.Collections.Generic;
using SDL;
using UnityEngine;
using UnityShim.AudioInput;
using UnityShim.Rendering;

namespace AshTreeLane
{
    internal enum MenuPage
    {
        Main,
        Resolution
    }

    internal sealed class PauseMenuOverlay
    {
        public static readonly (int W, int H)[] Presets =
        {
            (800, 600),
            (1024, 576),
            (1280, 720),
            (1600, 900),
            (1920, 1080)
        };

        private readonly UserSettings _settings;
        private readonly string _configPath;
        private readonly Action<int, int, bool, bool> _applyDisplay;
        private readonly Action _requestQuit;
        private readonly List<(float x, float y, float w, float h, int idx)> _rowHitBoxes = new();

        private CursorLockMode _savedCursorLock = CursorLockMode.None;
        private float _stickNavCooldown;

        public bool IsOpen { get; private set; }
        public MenuPage CurrentPage { get; private set; } = MenuPage.Main;
        public int SelectedIndex { get; private set; }
        public CursorLockMode SavedCursorLock => _savedCursorLock;

        public PauseMenuOverlay(UserSettings settings, string configPath,
                                Action<int, int, bool, bool> applyDisplay, Action requestQuit)
        {
            _settings = settings;
            _configPath = configPath;
            _applyDisplay = applyDisplay;
            _requestQuit = requestQuit;
        }

        public void Open(SdlInputBackend input)
        {
            if (IsOpen) return;
            IsOpen = true;
            CurrentPage = MenuPage.Main;
            SelectedIndex = 0;
            _stickNavCooldown = 0.25f;
            _savedCursorLock = Cursor.lockState;
            Cursor.lockState = CursorLockMode.None;
            input.MenuPaused = true;
            input.ClearAllKeys();
            Console.WriteLine("[Menu] Opened pause menu");
        }

        public void Close(SdlInputBackend input)
        {
            if (!IsOpen) return;
            IsOpen = false;
            CurrentPage = MenuPage.Main;
            input.MenuPaused = false;
            input.ClearAllKeys();
            Cursor.lockState = _savedCursorLock;
            Console.WriteLine("[Menu] Resumed game");
        }

        private int ItemCount => CurrentPage == MenuPage.Main ? 4 : (Presets.Length + 2);

        public void MoveSelection(int delta)
        {
            int n = ItemCount;
            SelectedIndex = ((SelectedIndex + delta) % n + n) % n;
        }

        public void HandleKey(SDL_Scancode sc, SdlInputBackend input)
        {
            if (!IsOpen) return;
            switch (sc)
            {
                case SDL_Scancode.SDL_SCANCODE_UP:
                case SDL_Scancode.SDL_SCANCODE_W:
                    MoveSelection(-1);
                    break;
                case SDL_Scancode.SDL_SCANCODE_DOWN:
                case SDL_Scancode.SDL_SCANCODE_S:
                    MoveSelection(1);
                    break;
                case SDL_Scancode.SDL_SCANCODE_RETURN:
                case SDL_Scancode.SDL_SCANCODE_KP_ENTER:
                case SDL_Scancode.SDL_SCANCODE_SPACE:
                    ActivateSelected(input);
                    break;
                case SDL_Scancode.SDL_SCANCODE_ESCAPE:
                    if (CurrentPage == MenuPage.Resolution)
                    {
                        CurrentPage = MenuPage.Main;
                        SelectedIndex = 1;
                    }
                    else
                    {
                        Close(input);
                    }
                    break;
            }
        }

        public void HandleGamepadButton(SDL_GamepadButton btn, SdlInputBackend input)
        {
            if (!IsOpen) return;
            switch (btn)
            {
                case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP:
                    MoveSelection(-1);
                    break;
                case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN:
                    MoveSelection(1);
                    break;
                case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH:
                    ActivateSelected(input);
                    break;
                case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST:
                    if (CurrentPage == MenuPage.Resolution)
                    {
                        CurrentPage = MenuPage.Main;
                        SelectedIndex = 1;
                    }
                    else
                    {
                        Close(input);
                    }
                    break;
            }
        }

        public void TickStickNavigation(SdlInputBackend input, float dt)
        {
            if (!IsOpen) return;
            if (_stickNavCooldown > 0f) _stickNavCooldown = MathF.Max(0f, _stickNavCooldown - dt);
            var (_, ly) = input.GetLeftStickWithDeadzone();
            if (MathF.Abs(ly) > 0.55f && _stickNavCooldown <= 0f)
            {
                MoveSelection(ly > 0f ? -1 : 1);
                _stickNavCooldown = 0.22f;
            }
        }

        public void HandleMouseMotion(float pixelX, float pixelY)
        {
            if (!IsOpen) return;
            for (int i = 0; i < _rowHitBoxes.Count; i++)
            {
                var r = _rowHitBoxes[i];
                if (pixelX >= r.x && pixelX <= r.x + r.w && pixelY >= r.y && pixelY <= r.y + r.h)
                {
                    SelectedIndex = r.idx;
                    return;
                }
            }
        }

        public void HandleMouseClick(float pixelX, float pixelY, SdlInputBackend input)
        {
            if (!IsOpen) return;
            for (int i = 0; i < _rowHitBoxes.Count; i++)
            {
                var r = _rowHitBoxes[i];
                if (pixelX >= r.x && pixelX <= r.x + r.w && pixelY >= r.y && pixelY <= r.y + r.h)
                {
                    SelectedIndex = r.idx;
                    ActivateSelected(input);
                    return;
                }
            }
        }

        private void ActivateSelected(SdlInputBackend input)
        {
            if (CurrentPage == MenuPage.Main)
            {
                switch (SelectedIndex)
                {
                    case 0:
                        Close(input);
                        break;
                    case 1:
                        CurrentPage = MenuPage.Resolution;
                        SelectedIndex = FindCurrentPresetIndex();
                        if (SelectedIndex < 0) SelectedIndex = 0;
                        Console.WriteLine("[Menu] Entered Resolution submenu");
                        break;
                    case 2:
                        _settings.ControllerEnabled = !_settings.ControllerEnabled;
                        input.ControllerEnabled = _settings.ControllerEnabled;
                        _settings.Save(_configPath);
                        Console.WriteLine($"[Menu] Controller toggled: {(_settings.ControllerEnabled ? "ON" : "OFF")}");
                        break;
                    case 3:
                        Console.WriteLine("[Menu] Quit requested");
                        _requestQuit();
                        break;
                }
            }
            else
            {
                if (SelectedIndex >= 0 && SelectedIndex < Presets.Length)
                {
                    var (w, h) = Presets[SelectedIndex];
                    _settings.Width = w;
                    _settings.Height = h;
                    _applyDisplay(w, h, _settings.Fullscreen, false);
                    _settings.Save(_configPath);
                }
                else if (SelectedIndex == Presets.Length)
                {
                    _settings.Fullscreen = !_settings.Fullscreen;
                    _applyDisplay(_settings.Width, _settings.Height, _settings.Fullscreen, false);
                    _settings.Save(_configPath);
                }
                else
                {
                    CurrentPage = MenuPage.Main;
                    SelectedIndex = 1;
                }
            }
        }

        private int FindCurrentPresetIndex()
        {
            for (int i = 0; i < Presets.Length; i++)
            {
                if (Presets[i].W == _settings.Width && Presets[i].H == _settings.Height) return i;
            }
            return -1;
        }

        public void Render(OverlayDrawContext ctx)
        {
            if (!IsOpen) return;
            _rowHitBoxes.Clear();

            float W = ctx.Width;
            float H = ctx.Height;
            float s = MathF.Max(0.65f, ctx.UiScale);

            ctx.DrawRect(0f, 0f, W, H, new Color(0.02f, 0.02f, 0.04f, 0.86f));

            float panelW = 580f * s;
            float panelH = (CurrentPage == MenuPage.Main ? 390f : 495f) * s;
            float px = (W - panelW) * 0.5f;
            float py = (H - panelH) * 0.5f;

            ctx.DrawRect(px, py, panelW, panelH, new Color(0.08f, 0.10f, 0.14f, 0.96f));
            ctx.DrawRectOutline(px, py, panelW, panelH, 2f * s, new Color(0.45f, 0.52f, 0.64f, 0.90f));

            float headerH = 54f * s;
            ctx.DrawRect(px + 2f * s, py + 2f * s, panelW - 4f * s, headerH, new Color(0.13f, 0.16f, 0.23f, 0.98f));
            string title = CurrentPage == MenuPage.Main ? "PAUSED" : "DISPLAY & RESOLUTION";
            ctx.DrawText(title, px, py + 2f * s, panelW, headerH, 26, new Color(0.95f, 0.97f, 1.0f, 1f), centered: true);

            var inp = UnityShim.Backend.EngineHost.Input as SdlInputBackend;
            string padStatus = inp != null && inp.ConnectedGamepadCount > 0
                ? $"Pad: {inp.ActiveGamepadName}"
                : "Pad: None detected";
            string statusLine = $"Display: {_settings.Width}x{_settings.Height} {(_settings.Fullscreen ? "Fullscreen" : "Windowed")}   |   {padStatus}";
            ctx.DrawText(statusLine, px, py + headerH + 6f * s, panelW, 24f * s, 13, new Color(0.70f, 0.76f, 0.84f, 0.95f), centered: true);

            var labels = BuildLabels();
            float rowX = px + 30f * s;
            float rowW = panelW - 60f * s;
            float rowH = 42f * s;
            float rowGap = 9f * s;
            float curY = py + headerH + 38f * s;

            for (int i = 0; i < labels.Length; i++)
            {
                bool sel = i == SelectedIndex;
                Color bg = sel ? new Color(0.22f, 0.40f, 0.58f, 0.96f) : new Color(0.14f, 0.17f, 0.22f, 0.92f);
                Color fg = sel ? new Color(1.0f, 1.0f, 1.0f, 1f) : new Color(0.84f, 0.88f, 0.92f, 1f);
                ctx.DrawRect(rowX, curY, rowW, rowH, bg);
                if (sel)
                {
                    ctx.DrawRectOutline(rowX, curY, rowW, rowH, 2f * s, new Color(0.68f, 0.85f, 1.0f, 0.98f));
                }
                string prefix = sel ? "> " : "  ";
                ctx.DrawText(prefix + labels[i], rowX, curY, rowW, rowH, 18, fg, centered: false);
                _rowHitBoxes.Add((rowX, curY, rowW, rowH, i));
                curY += rowH + rowGap;
            }

            string footer = CurrentPage == MenuPage.Main
                ? "Arrows/W/S/D-Pad: Move   |   Enter/A/Click: Select   |   Esc/Start: Resume"
                : "Arrows/D-Pad: Choose   |   Enter/A/Click: Apply   |   Esc/B: Back";
            ctx.DrawText(footer, px, py + panelH - 34f * s, panelW, 26f * s, 12, new Color(0.62f, 0.68f, 0.76f, 0.90f), centered: true);
        }

        private string[] BuildLabels()
        {
            if (CurrentPage == MenuPage.Main)
            {
                return new[]
                {
                    "Resume",
                    $"Resolution: {_settings.Width}x{_settings.Height} {(_settings.Fullscreen ? "Fullscreen" : "Windowed")}  >",
                    $"Controller: {(_settings.ControllerEnabled ? "ON" : "OFF")}",
                    "Quit"
                };
            }

            var list = new string[Presets.Length + 2];
            for (int i = 0; i < Presets.Length; i++)
            {
                var (w, h) = Presets[i];
                bool active = w == _settings.Width && h == _settings.Height;
                list[i] = active ? $"{w} x {h}  [ACTIVE]" : $"{w} x {h}";
            }
            list[Presets.Length] = $"Fullscreen: {(_settings.Fullscreen ? "ON" : "OFF")}";
            list[Presets.Length + 1] = "< Back";
            return list;
        }
    }
}
