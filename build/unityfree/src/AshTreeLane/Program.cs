using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using UnityEngine;
using UnityShim.AudioInput;
using UnityShim.Backend;
using UnityShim.Physics;
using UnityShim.Rendering;
using UnityShim.Scene;

namespace AshTreeLane
{
    internal static unsafe class Program
    {
        private struct ScheduledKey
        {
            public int KeyCode;
            public int StartFrame;
            public int EndFrame;
        }

        public static int Main(string[] args)
        {
            SdlNative.EnsureInitialized();
            RuntimeHelpers.RunClassConstructor(typeof(global::Menu).TypeHandle);

            int startScene = 0;
            string postFx = null;
            string screenshotPath = null;
            string configArg = null;
            int maxFrames = -1;
            int? cliWidth = null, cliHeight = null;
            bool? cliFullscreen = null;
            bool? cliController = null;
            string assetsArg = null;
            bool traceRender = false;
            bool gpuDebug = false;
            bool noPostFx = false;
            bool nullRender = false;
            bool testGamepad = false;
            bool logGamepad = false;
            float initialYaw = 0f;
            var scheduledKeys = new List<ScheduledKey>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--scene" when i + 1 < args.Length:
                        int.TryParse(args[++i], out startScene);
                        break;
                    case "--postfx" when i + 1 < args.Length:
                        postFx = args[++i];
                        break;
                    case "--screenshot" when i + 1 < args.Length:
                        screenshotPath = args[++i];
                        break;
                    case "--config" when i + 1 < args.Length:
                        configArg = args[++i];
                        break;
                    case "--frames" when i + 1 < args.Length:
                        int.TryParse(args[++i], out maxFrames);
                        break;
                    case "--width" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int cw)) cliWidth = cw;
                        break;
                    case "--height" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out int ch)) cliHeight = ch;
                        break;
                    case "--fullscreen":
                        cliFullscreen = true;
                        break;
                    case "--windowed":
                        cliFullscreen = false;
                        break;
                    case "--no-controller":
                        cliController = false;
                        break;
                    case "--assets" when i + 1 < args.Length:
                        assetsArg = args[++i];
                        break;
                    case "--trace-render":
                        traceRender = true;
                        break;
                    case "--gpu-debug":
                        gpuDebug = true;
                        break;
                    case "--no-postfx":
                        noPostFx = true;
                        break;
                    case "--null-render":
                        nullRender = true;
                        break;
                    case "--test-gamepad":
                        testGamepad = true;
                        break;
                    case "--log-gamepad":
                        logGamepad = true;
                        break;
                    case "--yaw" when i + 1 < args.Length:
                        float.TryParse(args[++i], out initialYaw);
                        break;
                    case "--send-key" when i + 1 < args.Length:
                        {
                            var parts = args[++i].Split(':');
                            if (parts.Length >= 2 && int.TryParse(parts[1], out int f))
                            {
                                int endF = parts.Length >= 3 && int.TryParse(parts[2], out int ef) ? ef : f + 2;
                                scheduledKeys.Add(new ScheduledKey
                                {
                                    KeyCode = SdlInputBackend.NameToKeyCode(parts[0]),
                                    StartFrame = f,
                                    EndFrame = endF
                                });
                            }
                            break;
                        }
                }
            }

            EngineHost.AssetRoot = ResolveDirectory(assetsArg, "assets", "build/unityfree/assets");
            EngineHost.ShaderRoot = ResolveDirectory(null, "Shaders", "src/Shaders", "build/unityfree/src/Shaders");

            string configPath = UserSettings.ResolveDefaultPath(configArg);
            var settings = UserSettings.Load(configPath);
            if (cliWidth.HasValue) settings.Width = Math.Clamp(cliWidth.Value, 640, 7680);
            if (cliHeight.HasValue) settings.Height = Math.Clamp(cliHeight.Value, 360, 4320);
            if (cliFullscreen.HasValue) settings.Fullscreen = cliFullscreen.Value;
            if (cliController.HasValue) settings.ControllerEnabled = cliController.Value;

            // Disable HIDAPI Switch calibration hijack (Bluetooth DS4 presents 0x057e:0x2009 and fails SPI
            // calibration under HIDAPI_SWITCH=1) while enabling MFi/GCController + PS4/PS5 HIDAPI + background events.
            (string Key, string Val)[] sdlHints =
            {
                ("SDL_JOYSTICK_HIDAPI", "1"),
                ("SDL_JOYSTICK_HIDAPI_PS4", "1"),
                ("SDL_JOYSTICK_HIDAPI_PS5", "1"),
                ("SDL_JOYSTICK_HIDAPI_SWITCH", "0"),
                ("SDL_JOYSTICK_MFI", "1"),
                ("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1")
            };
            foreach (var (hk, hv) in sdlHints)
            {
                byte[] hn = Encoding.ASCII.GetBytes(hk + "\0");
                byte[] hvb = Encoding.ASCII.GetBytes(hv + "\0");
                fixed (byte* pHn = hn)
                fixed (byte* pHv = hvb)
                {
                    SDL3.SDL_SetHint(pHn, pHv);
                }
            }

            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_JOYSTICK | SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                Console.Error.WriteLine($"SDL_Init failed: {SdlNative.GetErrorString()}");
                return 1;
            }
            int sdlVer = SDL3.SDL_GetVersion();
            Console.WriteLine($"[SDL] Loaded {SdlNative.LoadedLibraryPath} (version {sdlVer / 1000000}.{(sdlVer / 1000) % 1000}.{sdlVer % 1000})");

            SDL_WindowFlags winFlags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            if (settings.Fullscreen) winFlags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;

            byte[] titleUtf8 = Encoding.UTF8.GetBytes("The House On Ash Tree Lane\0");
            SDL_Window* window;
            fixed (byte* pTitle = titleUtf8)
            {
                window = SDL3.SDL_CreateWindow(pTitle, settings.Width, settings.Height, winFlags);
            }
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SdlNative.GetErrorString()}");
                SDL3.SDL_Quit();
                return 1;
            }
            EngineHost.WindowHandle = window;

            var input = new SdlInputBackend { ControllerEnabled = settings.ControllerEnabled };
            input.InitializeGamepads();
            var audio = new SdlAudioBackend();
            var physics = new PhysicsWorld();
            var renderer = new MetalRenderer(window, EngineHost.ShaderRoot, gpuDebug)
            {
                TraceRender = traceRender,
                DisablePostFx = noPostFx,
                NullRender = nullRender
            };
            var assets = new AssetBank();
            var sceneLoader = new SceneLoader(assets) { ActivePostFxOverride = postFx };

            EngineHost.Input = input;
            EngineHost.Audio = audio;
            EngineHost.Physics = physics;
            EngineHost.Renderer = renderer;
            EngineHost.SceneController = sceneLoader;

            bool running = true;
            var pauseMenu = new PauseMenuOverlay(
                settings,
                configPath,
                (w, h, fs, lockCursor) => renderer.RecreateWindowAndSwapchain(w, h, fs, lockCursor),
                () => running = false);
            renderer.OverlayRenderer = pauseMenu.Render;

            sceneLoader.LoadSceneNow(startScene);
            if (MathF.Abs(initialYaw) > 0.01f && Camera.main != null)
            {
                Camera.main.transform.localRotation = Quaternion.Euler(0f, initialYaw, 0f);
                if (Camera.main.transform.parent != null)
                {
                    Camera.main.transform.parent.localRotation *= Quaternion.Euler(0f, initialYaw, 0f);
                    Camera.main.transform.localRotation = Quaternion.identity;
                }
            }

            int exitCode = 0;
            if (testGamepad)
            {
                exitCode = RunVirtualGamepadSelfTest(input, audio, physics, renderer, sceneLoader, pauseMenu, screenshotPath);
            }
            else
            {
                var sw = Stopwatch.StartNew();
                double lastTime = sw.Elapsed.TotalSeconds;
                double fixedAccum = 0.0;
                double nextPadRescanTime = 0.25;
                const float fixedDt = 1f / 60f;
                int frame = 0;

                while (running)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    float rawDt = (float)(now - lastTime);
                    lastTime = now;
                    float dt = maxFrames > 0 ? fixedDt : Math.Clamp(rawDt, 0.001f, 0.05f);
                    Time.fixedDeltaTime = fixedDt;
                    Time.frameCount = frame;

                    input.BeginFrame();
                    if (now >= nextPadRescanTime)
                    {
                        input.RescanControllers();
                        if (now < 5.0 || input.ConnectedGamepadCount == 0)
                        {
                            nextPadRescanTime = now + (now < 5.0 ? 0.5 : 2.5);
                        }
                        else
                        {
                            nextPadRescanTime = double.MaxValue;
                        }
                    }

                    SDL_Event ev;
                    while (SDL3.SDL_PollEvent(&ev))
                    {
                        var evType = ev.Type;
                        if (evType == SDL_EventType.SDL_EVENT_QUIT || evType == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                        {
                            running = false;
                            break;
                        }

                        if (evType == SDL_EventType.SDL_EVENT_GAMEPAD_ADDED || evType == SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED ||
                            evType == SDL_EventType.SDL_EVENT_JOYSTICK_ADDED || evType == SDL_EventType.SDL_EVENT_JOYSTICK_REMOVED)
                        {
                            input.HandleEvent(in ev);
                            continue;
                        }

                        if (evType == SDL_EventType.SDL_EVENT_KEY_DOWN && !ev.key.repeat && ev.key.scancode == SDL_Scancode.SDL_SCANCODE_ESCAPE)
                        {
                            if (!pauseMenu.IsOpen) pauseMenu.Open(input);
                            else pauseMenu.HandleKey(SDL_Scancode.SDL_SCANCODE_ESCAPE, input);
                            continue;
                        }

                        if (pauseMenu.IsOpen)
                        {
                            if (evType == SDL_EventType.SDL_EVENT_KEY_DOWN && !ev.key.repeat)
                            {
                                pauseMenu.HandleKey(ev.key.scancode, input);
                            }
                            else if (evType == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                            {
                                var (px, py) = WindowToPixel(renderer.Window, ev.motion.x, ev.motion.y);
                                pauseMenu.HandleMouseMotion(px, py);
                            }
                            else if (evType == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN && ev.button.button == 1)
                            {
                                var (px, py) = WindowToPixel(renderer.Window, ev.button.x, ev.button.y);
                                pauseMenu.HandleMouseClick(px, py, input);
                            }
                            else if (evType == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN ||
                                     evType == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP ||
                                     evType == SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION ||
                                     evType == SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_DOWN ||
                                     evType == SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_UP ||
                                     evType == SDL_EventType.SDL_EVENT_JOYSTICK_AXIS_MOTION ||
                                     evType == SDL_EventType.SDL_EVENT_JOYSTICK_HAT_MOTION)
                            {
                                input.HandleEvent(in ev);
                            }
                            continue;
                        }

                        if (evType == SDL_EventType.SDL_EVENT_KEY_DOWN && !ev.key.repeat)
                        {
                            HandleHostHotkeys(ev.key.scancode, sceneLoader, renderer, ref running);
                        }
                        input.HandleEvent(in ev);
                    }

                    if (input.ConsumePauseToggleRequest() && input.ControllerEnabled)
                    {
                        if (pauseMenu.IsOpen) pauseMenu.Close(input);
                        else pauseMenu.Open(input);
                    }
                    else if (pauseMenu.IsOpen && input.ControllerEnabled)
                    {
                        var btns = input.DrainMenuGamepadButtons();
                        for (int b = 0; b < btns.Count; b++)
                        {
                            pauseMenu.HandleGamepadButton(btns[b], input);
                        }
                    }

                    input.PollConnectedControllers();

                    if (logGamepad && (frame == 1 || frame % 15 == 0))
                    {
                        Console.WriteLine($"[PadLog f={frame}] pads={input.ConnectedGamepadCount} ({input.ActiveGamepadName}) H={Input.GetAxis("Horizontal"):F3} V={Input.GetAxis("Vertical"):F3} MX={Input.GetAxisRaw("Mouse X"):F3} MY={Input.GetAxisRaw("Mouse Y"):F3} {input.GetRawAxisDebugString()}");
                    }

                    // Route scheduled synthetic keys cleanly to Pause Menu or gameplay Input
                    for (int k = 0; k < scheduledKeys.Count; k++)
                    {
                        var sk = scheduledKeys[k];
                        if (sk.KeyCode == 0) continue;
                        if (frame == sk.StartFrame)
                        {
                            if (sk.KeyCode == (int)KeyCode.Escape)
                            {
                                if (!pauseMenu.IsOpen) pauseMenu.Open(input);
                                else pauseMenu.HandleKey(SDL_Scancode.SDL_SCANCODE_ESCAPE, input);
                            }
                            else if (pauseMenu.IsOpen)
                            {
                                var sc = SdlInputBackend.KeyCodeToScancode(sk.KeyCode);
                                if (sc != SDL_Scancode.SDL_SCANCODE_UNKNOWN) pauseMenu.HandleKey(sc, input);
                            }
                            else
                            {
                                input.InjectSyntheticKey(sk.KeyCode, true);
                            }
                        }
                        else if (frame == sk.EndFrame && !pauseMenu.IsOpen)
                        {
                            input.InjectSyntheticKey(sk.KeyCode, false);
                        }
                    }

                    if (pauseMenu.IsOpen)
                    {
                        Time.timeScale = 0f;
                        Time.deltaTime = 0f;
                        Time.unscaledTime += dt;
                        pauseMenu.TickStickNavigation(input, dt);
                    }
                    else
                    {
                        Time.timeScale = 1f;
                        Time.deltaTime = dt;
                        Time.time += dt;
                        Time.unscaledTime += dt;

                        input.UpdateSmoothedAxes(dt);
                        sceneLoader.ConsumePendingSceneLoad();

                        fixedAccum += dt;
                        int subSteps = 0;
                        while (fixedAccum >= fixedDt && subSteps < 4)
                        {
                            SceneRegistry.DispatchFixedUpdate();
                            physics.Step(fixedDt);
                            fixedAccum -= fixedDt;
                            subSteps++;
                        }

                        SceneRegistry.DispatchUpdate();
                        UnityEngine.Animation.StepAll(dt);
                        SceneRegistry.DispatchLateUpdate();

                        if (Camera.main != null)
                        {
                            var ct = Camera.main.transform;
                            audio.SetListenerPose(ct.position.x, ct.position.y, ct.position.z, ct.right.x, ct.right.y, ct.right.z);
                        }
                        audio.Tick(dt);
                    }

                    if (maxFrames > 0 && frame == maxFrames - 1 && !string.IsNullOrEmpty(screenshotPath))
                    {
                        renderer.RequestScreenshot(screenshotPath);
                    }
                    renderer.RenderFrame();

                    frame++;
                    if (maxFrames > 0 && frame >= maxFrames)
                    {
                        running = false;
                    }
                }
            }

            if (traceRender) Console.WriteLine("[Shutdown] input.Dispose()");
            input.Dispose();
            if (traceRender) Console.WriteLine("[Shutdown] renderer.Dispose()");
            renderer.Dispose();
            if (traceRender) Console.WriteLine("[Shutdown] audio.Dispose()");
            audio.Dispose();
            if (traceRender) Console.WriteLine("[Shutdown] SDL_DestroyWindow");
            if (renderer.Window != null) SDL3.SDL_DestroyWindow(renderer.Window);
            if (traceRender) Console.WriteLine("[Shutdown] SDL_Quit");
            SDL3.SDL_Quit();
            if (traceRender) Console.WriteLine("[Shutdown] Done");
            return exitCode;
        }

        private static (float px, float py) WindowToPixel(SDL_Window* win, float lx, float ly)
        {
            if (win == null) return (lx, ly);
            int ww = 1, wh = 1, pw = 1, ph = 1;
            SDL3.SDL_GetWindowSize(win, &ww, &wh);
            SDL3.SDL_GetWindowSizeInPixels(win, &pw, &ph);
            return (lx * (float)pw / Math.Max(1, ww), ly * (float)ph / Math.Max(1, wh));
        }

        private static int RunVirtualGamepadSelfTest(
            SdlInputBackend input, SdlAudioBackend audio, PhysicsWorld physics,
            MetalRenderer renderer, SceneLoader sceneLoader, PauseMenuOverlay pauseMenu,
            string screenshotPath)
        {
            bool passed = true;
            void Check(bool cond, string msg)
            {
                if (!cond) { Console.Error.WriteLine($"[GamepadTest] FAIL: {msg}"); passed = false; }
                else Console.WriteLine($"[GamepadTest] PASS: {msg}");
            }

            void PumpFrames(int n, float dt = 1f / 60f, bool render = true, int syntheticEFrame = -1)
            {
                for (int f = 0; f < n; f++)
                {
                    Time.fixedDeltaTime = dt;
                    input.BeginFrame();
                    SDL3.SDL_UpdateJoysticks();
                    SDL3.SDL_UpdateGamepads();
                    SDL_Event ev;
                    while (SDL3.SDL_PollEvent(&ev))
                    {
                        input.HandleEvent(in ev);
                    }
                    if (input.ConsumePauseToggleRequest() && input.ControllerEnabled)
                    {
                        if (pauseMenu.IsOpen) pauseMenu.Close(input);
                        else pauseMenu.Open(input);
                    }
                    input.PollConnectedControllers();
                    if (f == syntheticEFrame) input.InjectSyntheticKey((int)KeyCode.E, true);
                    if (f == syntheticEFrame + 2) input.InjectSyntheticKey((int)KeyCode.E, false);

                    if (pauseMenu.IsOpen)
                    {
                        Time.timeScale = 0f;
                        Time.deltaTime = 0f;
                        Time.unscaledTime += dt;
                    }
                    else
                    {
                        Time.timeScale = 1f;
                        Time.deltaTime = dt;
                        Time.time += dt;
                        Time.unscaledTime += dt;
                        input.UpdateSmoothedAxes(dt);
                        sceneLoader.ConsumePendingSceneLoad();
                        SceneRegistry.DispatchFixedUpdate();
                        physics.Step(dt);
                        SceneRegistry.DispatchUpdate();
                        UnityEngine.Animation.StepAll(dt);
                        SceneRegistry.DispatchLateUpdate();
                        audio.Tick(dt);
                    }
                    if (render) renderer.RenderFrame();
                }
            }

            // Settle initial instruction fade + allow async GCController/MFi physical pads to arrive first
            PumpFrames(65, render: false, syntheticEFrame: 2);
            input.RescanControllers();
            int basePadCount = input.ConnectedGamepadCount;
            if (basePadCount > 0)
            {
                Console.WriteLine($"[GamepadTest] PASS: Physical pad detected at baseline (count={basePadCount}, name={input.ActiveGamepadName})");
            }

            Console.WriteLine("[GamepadTest] Attaching SDL3 virtual gamepad...");
            byte[] vname = Encoding.UTF8.GetBytes("Virtual Xbox Pad\0");
            SDL_JoystickID virtId;
            fixed (byte* pName = vname)
            {
                var desc = new SDL_VirtualJoystickDesc
                {
                    version = (uint)sizeof(SDL_VirtualJoystickDesc),
                    type = (ushort)SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD,
                    vendor_id = 0x045E,
                    product_id = 0x028E,
                    naxes = 6,
                    nbuttons = 15,
                    button_mask = 0x7FFFu,
                    axis_mask = 0x3Fu,
                    name = pName
                };
                virtId = SDL3.SDL_AttachVirtualJoystick(&desc);
            }
            if ((uint)virtId == 0)
            {
                Console.Error.WriteLine($"[GamepadTest] FAIL: SDL_AttachVirtualJoystick failed: {SdlNative.GetErrorString()}");
                return 1;
            }
            SDL_Joystick* vjoy = SDL3.SDL_OpenJoystick(virtId);
            if (vjoy == null)
            {
                Console.Error.WriteLine($"[GamepadTest] FAIL: SDL_OpenJoystick failed: {SdlNative.GetErrorString()}");
                SDL3.SDL_DetachVirtualJoystick(virtId);
                return 1;
            }

            PumpFrames(3, render: false);
            input.RescanControllers();
            Check(SDL3.SDL_IsGamepad(virtId), "SDL_IsGamepad recognises virtual joystick as SDL_Gamepad");
            Check(input.ConnectedGamepadCount == basePadCount + 1, $"SDL_EVENT_GAMEPAD_ADDED opened gamepad (count={input.ConnectedGamepadCount})");

            // 1. Deadzone test (3000/32767 = 0.091 < 0.18 deadzone)
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 3000);
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, 3000);
            PumpFrames(2, render: false);
            Check(MathF.Abs(Input.GetAxisRaw("Horizontal")) < 1e-5f && MathF.Abs(Input.GetAxisRaw("Vertical")) < 1e-5f,
                  "Left stick inside 0.18 deadzone yields 0");

            // 2. Left stick full deflection (SDL Y=-32000 is forward/up -> Unity Vertical > 0)
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 32000);
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, -32000);
            PumpFrames(10, render: false);
            float hVal = Input.GetAxis("Horizontal");
            float vVal = Input.GetAxis("Vertical");
            Check(hVal > 0.65f && vVal > 0.65f, $"Left stick feeds Input.GetAxis Horizontal={hVal:F2}, Vertical={vVal:F2}");
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 0);
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, 0);
            PumpFrames(2, render: false);

            // 3. Right stick camera look drives Mouse X/Y and FirstPersonLook yaw
            float yawBefore = Camera.main != null && Camera.main.transform.parent != null
                ? Camera.main.transform.parent.eulerAngles.y : 0f;
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX, 26000);
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY, -16000);
            PumpFrames(18, render: true);
            float mx = Input.GetAxis("Mouse X");
            float my = Input.GetAxis("Mouse Y");
            float yawAfter = Camera.main != null && Camera.main.transform.parent != null
                ? Camera.main.transform.parent.eulerAngles.y : 0f;
            Check(mx > 0.5f && my > 0.25f, $"Right stick feeds Mouse X={mx:F2}, Mouse Y={my:F2}");
            Check(MathF.Abs(yawAfter - yawBefore) > 5f, $"Right stick rotated camera yaw ({yawBefore:F1} -> {yawAfter:F1} deg)");

            if (!string.IsNullOrEmpty(screenshotPath))
            {
                renderer.RequestScreenshot(screenshotPath);
                renderer.RenderFrame();
            }
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX, 0);
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY, 0);
            PumpFrames(2, render: false);

            // 4. South (A) -> Jump + Space; West (X) -> E; D-Pad Up -> W + Vertical
            void CheckSinglePress(SDL_GamepadButton btn, Func<bool> check, string label)
            {
                SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)btn, true);
                input.BeginFrame();
                SDL3.SDL_UpdateJoysticks();
                SDL3.SDL_UpdateGamepads();
                SDL_Event ev;
                while (SDL3.SDL_PollEvent(&ev)) input.HandleEvent(in ev);
                Check(check(), label);
                SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)btn, false);
                PumpFrames(2, render: false);
            }

            CheckSinglePress(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, () => Input.GetButtonDown("Jump") && Input.GetKeyDown("space"), "South (A) -> Jump & space");
            CheckSinglePress(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, () => Input.GetKeyDown(KeyCode.E), "West (X) -> KeyCode.E");
            CheckSinglePress(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, () => Input.GetKeyDown(KeyCode.W) && Input.GetAxisRaw("Vertical") > 0.5f, "D-Pad Up -> KeyCode.W & Vertical");

            // 5. Start -> pauses Time.time, toggles back off
            SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, true);
            PumpFrames(1, render: false);
            Check(pauseMenu.IsOpen && Time.timeScale == 0f, "Start button opens Pause Menu & freezes Time.timeScale=0");
            SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, false);
            float tPaused0 = Time.time;
            PumpFrames(5, render: false);
            Check(MathF.Abs(Time.time - tPaused0) < 1e-6f, $"Time.time frozen while paused ({tPaused0:F3} == {Time.time:F3})");

            SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, true);
            PumpFrames(1, render: false);
            SDL3.SDL_SetJoystickVirtualButton(vjoy, (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, false);
            Check(!pauseMenu.IsOpen && Time.timeScale > 0.9f, "Second Start press resumes gameplay");

            // 6. ControllerEnabled toggle gates axis
            input.ControllerEnabled = false;
            SDL3.SDL_SetJoystickVirtualAxis(vjoy, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 32000);
            PumpFrames(2, render: false);
            Check(MathF.Abs(Input.GetAxisRaw("Horizontal")) < 1e-5f, "ControllerEnabled=false zeros gamepad input");
            input.ControllerEnabled = true;

            // 7. Hotplug removal
            SDL3.SDL_CloseJoystick(vjoy);
            SDL3.SDL_DetachVirtualJoystick(virtId);
            PumpFrames(2, render: false);
            Check(input.ConnectedGamepadCount == basePadCount, $"SDL_EVENT_GAMEPAD_REMOVED detached pad (count={input.ConnectedGamepadCount})");

            // 8. Unmapped Raw Joystick fallback (SDL_JOYSTICK_TYPE_UNKNOWN, SDL_IsGamepad == false)
            byte[] rname = Encoding.UTF8.GetBytes("Raw Generic Stick\0");
            SDL_JoystickID rawId;
            fixed (byte* pRName = rname)
            {
                var rdesc = new SDL_VirtualJoystickDesc
                {
                    version = (uint)sizeof(SDL_VirtualJoystickDesc),
                    type = (ushort)SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN,
                    vendor_id = 0x1209,
                    product_id = 0x0001,
                    naxes = 4,
                    nbuttons = 10,
                    nhats = 1,
                    button_mask = 0x3FFu,
                    axis_mask = 0x0Fu,
                    name = pRName
                };
                rawId = SDL3.SDL_AttachVirtualJoystick(&rdesc);
            }
            SDL_Joystick* rjoy = SDL3.SDL_OpenJoystick(rawId);
            PumpFrames(3, render: false);
            Check(!SDL3.SDL_IsGamepad(rawId) && input.ConnectedGamepadCount >= basePadCount + 1,
                  $"Unmapped joystick opened via raw fallback (count={input.ConnectedGamepadCount})");

            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 0, -32000);
            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 1, -32000);
            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 2, 26000);
            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 3, -16000);
            PumpFrames(8, render: false);
            Check(Input.GetAxis("Horizontal") < -0.65f && Input.GetAxis("Vertical") > 0.65f &&
                  Input.GetAxisRaw("Mouse X") > 0.5f && Input.GetAxisRaw("Mouse Y") > 0.25f,
                  $"Raw joystick axes drive H={Input.GetAxis("Horizontal"):F2} V={Input.GetAxis("Vertical"):F2} MX={Input.GetAxisRaw("Mouse X"):F2} MY={Input.GetAxisRaw("Mouse Y"):F2}");

            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 0, 0); SDL3.SDL_SetJoystickVirtualAxis(rjoy, 1, 0);
            SDL3.SDL_SetJoystickVirtualAxis(rjoy, 2, 0); SDL3.SDL_SetJoystickVirtualAxis(rjoy, 3, 0);
            PumpFrames(2, render: false);

            SDL3.SDL_SetJoystickVirtualButton(rjoy, 0, true);
            input.BeginFrame(); SDL3.SDL_UpdateJoysticks();
            { SDL_Event ev; while (SDL3.SDL_PollEvent(&ev)) input.HandleEvent(in ev); }
            Check(Input.GetButtonDown("Jump") && Input.GetKeyDown("space"), "Raw fallback button 0 -> Jump & space");
            SDL3.SDL_SetJoystickVirtualButton(rjoy, 0, false); PumpFrames(2, render: false);

            SDL3.SDL_SetJoystickVirtualButton(rjoy, 2, true);
            input.BeginFrame(); SDL3.SDL_UpdateJoysticks();
            { SDL_Event ev; while (SDL3.SDL_PollEvent(&ev)) input.HandleEvent(in ev); }
            Check(Input.GetKeyDown(KeyCode.E), "Raw fallback button 2 -> KeyCode.E");
            SDL3.SDL_SetJoystickVirtualButton(rjoy, 2, false); PumpFrames(2, render: false);

            SDL3.SDL_SetJoystickVirtualHat(rjoy, 0, 1); // Up
            input.BeginFrame(); SDL3.SDL_UpdateJoysticks();
            { SDL_Event ev; while (SDL3.SDL_PollEvent(&ev)) input.HandleEvent(in ev); }
            Check(Input.GetKeyDown(KeyCode.W) && Input.GetAxisRaw("Vertical") > 0.5f, "Raw fallback hat UP -> KeyCode.W & Vertical");
            SDL3.SDL_SetJoystickVirtualHat(rjoy, 0, 0); PumpFrames(2, render: false);

            SDL3.SDL_SetJoystickVirtualButton(rjoy, 9, true);
            PumpFrames(1, render: false);
            Check(pauseMenu.IsOpen && Time.timeScale == 0f, "Raw fallback button 9 -> Pause Menu opens");
            SDL3.SDL_SetJoystickVirtualButton(rjoy, 9, false);
            SDL3.SDL_SetJoystickVirtualButton(rjoy, 9, true);
            PumpFrames(1, render: false);
            SDL3.SDL_SetJoystickVirtualButton(rjoy, 9, false);

            SDL3.SDL_CloseJoystick(rjoy);
            SDL3.SDL_DetachVirtualJoystick(rawId);
            PumpFrames(2, render: false);
            Check(input.ConnectedGamepadCount == basePadCount, $"Raw joystick detached cleanly (count={input.ConnectedGamepadCount})");

            Console.WriteLine(passed ? "[GamepadTest] ALL CHECKS PASSED" : "[GamepadTest] SOME CHECKS FAILED");
            return passed ? 0 : 1;
        }

        private static void HandleHostHotkeys(SDL_Scancode sc, SceneLoader sceneLoader, MetalRenderer renderer, ref bool running)
        {
            string fx = sc switch
            {
                SDL_Scancode.SDL_SCANCODE_F1 => "none",
                SDL_Scancode.SDL_SCANCODE_F2 => "bloom",
                SDL_Scancode.SDL_SCANCODE_F3 => "kuwahara",
                SDL_Scancode.SDL_SCANCODE_F4 => "pixelart",
                SDL_Scancode.SDL_SCANCODE_F5 => "dog",
                SDL_Scancode.SDL_SCANCODE_F6 => "vignette",
                _ => null
            };

            if (fx != null && Camera.main != null)
            {
                sceneLoader.ActivePostFxOverride = fx;
                foreach (var mb in Camera.main.GetComponents<MonoBehaviour>())
                {
                    if (mb.InternalHasOnRenderImage) UnityEngine.Object.Destroy(mb);
                }
                if (fx != "none") sceneLoader.AttachPostFxByName(Camera.main.gameObject, fx);
            }
            else if (sc == SDL_Scancode.SDL_SCANCODE_F12)
            {
                renderer.RequestScreenshot($"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            }
        }

        private static string ResolveDirectory(string explicitPath, params string[] candidates)
        {
            if (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
            string baseDir = AppContext.BaseDirectory;
            string cwd = Directory.GetCurrentDirectory();
            foreach (string rel in candidates)
            {
                string p1 = Path.GetFullPath(Path.Combine(baseDir, rel));
                if (Directory.Exists(p1)) return p1;
                string p2 = Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", rel));
                if (Directory.Exists(p2)) return p2;
                string p3 = Path.GetFullPath(Path.Combine(cwd, rel));
                if (Directory.Exists(p3)) return p3;
            }
            return Path.GetFullPath(Path.Combine(baseDir, candidates[0]));
        }
    }
}
