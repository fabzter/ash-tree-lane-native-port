using System;
using System.Collections.Generic;

namespace UnityShim.Backend
{
    public interface IInputBackend
    {
        bool AnyKey { get; }
        bool AnyKeyDown { get; }
        bool GetKey(int keyCode);
        bool GetKeyDown(int keyCode);
        bool GetKeyUp(int keyCode);
        bool GetKeyDownName(string name);
        float GetAxis(string axisName);
        float GetAxisRaw(string axisName);
        bool GetButtonDown(string buttonName);
        void SetCursorLocked(bool locked);
        void BeginFrame();
        void InjectSyntheticKey(int keyCode, bool down);
    }

    public interface IAudioBackend
    {
        int RegisterClip(string name, float[] interleavedStereoPcm, int sampleRate);
        int PlayVoice(int clipId, float volume, float pitch, bool loop, ulong delaySamples,
                      float spatialBlend, float minDist, float maxDist, float x, float y, float z);
        void UpdateVoice(int voiceId, float volume, float pitch, bool loop,
                         float spatialBlend, float minDist, float maxDist, float x, float y, float z);
        void PauseVoice(int voiceId);
        void StopVoice(int voiceId);
        bool IsVoicePlaying(int voiceId);
        void SetListenerPose(float x, float y, float z, float rx, float ry, float rz);
        void StopAllVoices();
        void Tick(float dt);
    }

    public interface IPhysicsBackend
    {
        void Step(float dt);
        bool Raycast(float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance);
    }

    public interface IRenderBackend
    {
        int UploadTextureRgba(string name, int width, int height, byte[] rgba, bool linear, bool clamp, bool pointFilter, bool generateMipmaps = false);
        void ReleaseTexture(int texId);
        int CreateRenderTarget(int width, int height, int format, bool linear);
        void ReleaseRenderTarget(int rtId);
        int UploadMesh(string name, float[] interleaved56, int vertexCount, uint[] indices, uint[] submeshOffsets, uint[] submeshCounts);
        void ReleaseMesh(int meshId);
        void Blit(
            int srcTexId, int srcW, int srcH,
            int dstRtId, int dstW, int dstH,
            string shaderName, int pass,
            IReadOnlyDictionary<string, float> floats,
            IReadOnlyDictionary<string, int> ints,
            IReadOnlyDictionary<string, float[]> vectors,
            IReadOnlyDictionary<string, int> textures);
        void RenderFrame();
        void RequestScreenshot(string outputPath);
    }

    public interface ISceneController
    {
        int ActiveSceneIndex { get; }
        string ActiveSceneName { get; }
        void RequestLoadScene(int buildIndex);
    }

    public static class EngineHost
    {
        public static string AssetRoot { get; set; } = string.Empty;
        public static string ShaderRoot { get; set; } = string.Empty;

        public static IInputBackend Input { get; set; }
        public static IAudioBackend Audio { get; set; }
        public static IPhysicsBackend Physics { get; set; }
        public static IRenderBackend Renderer { get; set; }
        public static ISceneController SceneController { get; set; }

        public static unsafe SDL.SDL_Window* WindowHandle { get; set; }
    }
}
