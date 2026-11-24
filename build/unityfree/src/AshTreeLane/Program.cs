using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            int maxFrames = -1;
            int winW = 1280, winH = 720;
            string assetsArg = null;
            bool traceRender = false;
            bool gpuDebug = false;
            bool noPostFx = false;
            bool nullRender = false;
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
                    case "--frames" when i + 1 < args.Length:
                        int.TryParse(args[++i], out maxFrames);
                        break;
                    case "--width" when i + 1 < args.Length:
                        int.TryParse(args[++i], out winW);
                        break;
                    case "--height" when i + 1 < args.Length:
                        int.TryParse(args[++i], out winH);
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

            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO))
            {
                Console.Error.WriteLine($"SDL_Init failed: {SdlNative.GetErrorString()}");
                return 1;
            }

            byte[] titleUtf8 = Encoding.UTF8.GetBytes("The House On Ash Tree Lane\0");
            SDL_Window* window;
            fixed (byte* pTitle = titleUtf8)
            {
                window = SDL3.SDL_CreateWindow(pTitle, winW, winH, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            }
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SdlNative.GetErrorString()}");
                SDL3.SDL_Quit();
                return 1;
            }
            EngineHost.WindowHandle = window;

            var input = new SdlInputBackend();
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

            sceneLoader.LoadSceneNow(startScene);
            if (MathF.Abs(initialYaw) > 0.01f && Camera.main != null)
            {
                // Apply synthetic mouse look or initial orientation to inspect alternative viewing angles
                Camera.main.transform.localRotation = Quaternion.Euler(0f, initialYaw, 0f);
                if (Camera.main.transform.parent != null)
                {
                    Camera.main.transform.parent.localRotation *= Quaternion.Euler(0f, initialYaw, 0f);
                    Camera.main.transform.localRotation = Quaternion.identity;
                }
            }

            bool running = true;
            var sw = Stopwatch.StartNew();
            double lastTime = sw.Elapsed.TotalSeconds;
            double fixedAccum = 0.0;
            const float fixedDt = 1f / 60f;
            int frame = 0;

            while (running)
            {
                double now = sw.Elapsed.TotalSeconds;
                float rawDt = (float)(now - lastTime);
                lastTime = now;
                float dt = maxFrames > 0 ? fixedDt : Math.Clamp(rawDt, 0.001f, 0.05f);
                Time.deltaTime = dt;
                Time.fixedDeltaTime = fixedDt;
                Time.time += dt;
                Time.unscaledTime += dt;
                Time.frameCount = frame;

                input.BeginFrame();
                SDL_Event ev;
                while (SDL3.SDL_PollEvent(&ev))
                {
                    if (ev.Type == SDL_EventType.SDL_EVENT_QUIT || ev.Type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        running = false;
                        break;
                    }
                    if (ev.Type == SDL_EventType.SDL_EVENT_KEY_DOWN && !ev.key.repeat)
                    {
                        HandleHostHotkeys(ev.key.scancode, sceneLoader, renderer, ref running);
                    }
                    input.HandleEvent(in ev);
                }

                // Inject synthetic test inputs cleanly at scheduled frames
                for (int k = 0; k < scheduledKeys.Count; k++)
                {
                    var sk = scheduledKeys[k];
                    if (sk.KeyCode == 0) continue;
                    if (frame == sk.StartFrame) input.InjectSyntheticKey(sk.KeyCode, true);
                    else if (frame == sk.EndFrame) input.InjectSyntheticKey(sk.KeyCode, false);
                }

                input.UpdateSmoothedAxes(dt);

                // Process deferred SceneManager.LoadScene from previous frame
                sceneLoader.ConsumePendingSceneLoad();

                // Fixed update + physics
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

            if (traceRender) Console.WriteLine("[Shutdown] renderer.Dispose()");
            renderer.Dispose();
            if (traceRender) Console.WriteLine("[Shutdown] audio.Dispose()");
            audio.Dispose();
            if (traceRender) Console.WriteLine("[Shutdown] SDL_DestroyWindow");
            SDL3.SDL_DestroyWindow(window);
            if (traceRender) Console.WriteLine("[Shutdown] SDL_Quit");
            SDL3.SDL_Quit();
            if (traceRender) Console.WriteLine("[Shutdown] Done");
            return 0;
        }

        private static void HandleHostHotkeys(SDL_Scancode sc, SceneLoader sceneLoader, MetalRenderer renderer, ref bool running)
        {
            if (sc == SDL_Scancode.SDL_SCANCODE_ESCAPE)
            {
                if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = CursorLockMode.None;
                else running = false;
                return;
            }

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
