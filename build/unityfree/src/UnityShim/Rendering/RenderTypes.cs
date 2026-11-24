using System;
using System.Collections.Generic;
using UnityShim.Backend;

namespace UnityEngine
{
    public enum DepthTextureMode
    {
        None = 0,
        Depth = 1,
        DepthNormals = 2,
        MotionVectors = 4
    }

    public enum RenderTextureFormat
    {
        ARGB32 = 0,
        Depth = 1,
        ARGBHalf = 2,
        Default = 7,
        RHalf = 15,
        RGInt = 21,
        RG32 = 24
    }

    public enum RenderTextureReadWrite
    {
        Default = 0,
        Linear = 1,
        sRGB = 2
    }

    public class Texture : Object
    {
        public int width { get; protected set; }
        public int height { get; protected set; }
        public int GpuTextureId { get; internal set; }
        public bool IsLinear { get; set; }
        public bool Clamp { get; set; }
        public bool PointFilter { get; set; }
    }

    public class Texture2D : Texture
    {
        private static Texture2D _white;
        private static Texture2D _black;

        public static Texture2D WhiteTexture => _white ??= CreateSolid("White4x4", 255, 255, 255, 255);
        public static Texture2D BlackTexture => _black ??= CreateSolid("Black4x4", 0, 0, 0, 255);

        public Texture2D() { }
        public Texture2D(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        public void UploadRgba(byte[] rgba, bool linear = false, bool clamp = false, bool pointFilter = false, bool generateMipmaps = false)
        {
            IsLinear = linear;
            Clamp = clamp;
            PointFilter = pointFilter;
            if (EngineHost.Renderer != null)
            {
                if (GpuTextureId > 0) EngineHost.Renderer.ReleaseTexture(GpuTextureId);
                GpuTextureId = EngineHost.Renderer.UploadTextureRgba(name, width, height, rgba, linear, clamp, pointFilter, generateMipmaps);
            }
        }

        private static Texture2D CreateSolid(string name, byte r, byte g, byte b, byte a)
        {
            var t = new Texture2D(4, 4) { name = name };
            byte[] data = new byte[4 * 4 * 4];
            for (int i = 0; i < data.Length; i += 4) { data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a; }
            t.UploadRgba(data, linear: false, clamp: true, pointFilter: false);
            return t;
        }
    }

    public class RenderTexture : Texture
    {
        private static readonly List<RenderTexture> _pool = new();
        private bool _inPool;

        public RenderTextureFormat format { get; }
        public RenderTextureReadWrite readWrite { get; }
        public bool useMipMap { get; set; }

        public RenderTexture(int width, int height, int depthBuffer,
                             RenderTextureFormat format = RenderTextureFormat.Default,
                             RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default)
        {
            this.width = Math.Max(1, width);
            this.height = Math.Max(1, height);
            this.format = format;
            this.readWrite = readWrite;
            Create();
        }

        public bool Create()
        {
            if (GpuTextureId <= 0 && EngineHost.Renderer != null)
            {
                GpuTextureId = EngineHost.Renderer.CreateRenderTarget(width, height, (int)format, readWrite == RenderTextureReadWrite.Linear);
            }
            return GpuTextureId > 0;
        }

        public void Release()
        {
            if (GpuTextureId > 0)
            {
                EngineHost.Renderer?.ReleaseRenderTarget(GpuTextureId);
                GpuTextureId = 0;
            }
        }

        public static RenderTexture GetTemporary(int width, int height, int depthBuffer = 0,
                                                 RenderTextureFormat format = RenderTextureFormat.Default)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            for (int i = 0; i < _pool.Count; i++)
            {
                var rt = _pool[i];
                if (rt.width == width && rt.height == height && rt.format == format)
                {
                    _pool.RemoveAt(i);
                    rt._inPool = false;
                    return rt;
                }
            }
            return new RenderTexture(width, height, depthBuffer, format);
        }

        public static void ReleaseTemporary(RenderTexture temp)
        {
            if (temp == null || temp._inPool) return;
            if (_pool.Count >= 32)
            {
                var old = _pool[0];
                _pool.RemoveAt(0);
                if (old.GpuTextureId > 0) EngineHost.Renderer?.ReleaseRenderTarget(old.GpuTextureId);
            }
            temp._inPool = true;
            _pool.Add(temp);
        }

        public static void ClearPool()
        {
            foreach (var rt in _pool)
            {
                if (rt.GpuTextureId > 0) EngineHost.Renderer?.ReleaseRenderTarget(rt.GpuTextureId);
            }
            _pool.Clear();
        }
    }

    public class Shader : Object
    {
        private static readonly Dictionary<string, Shader> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static Shader Find(string name)
        {
            name ??= "Standard";
            if (!_cache.TryGetValue(name, out var s))
            {
                s = new Shader { name = name };
                _cache[name] = s;
            }
            return s;
        }
    }

    public class Material : Object
    {
        private readonly Dictionary<string, float> _floats = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _ints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture> _textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _texIds = new(StringComparer.Ordinal);

        public Shader shader { get; set; }
        public Vector2 mainTextureScale { get; set; } = Vector2.one;
        public Vector2 mainTextureOffset { get; set; } = Vector2.zero;
        public bool DoubleSided { get; set; }
        public bool Hidden { get; set; }
        public bool AlphaCutout { get; set; }
        public bool AlphaBlend { get; set; }
        public bool MainTexClamp { get; set; }
        public bool MainTexPoint { get; set; }
        public bool FlatShaded { get; set; }
        public bool IsStandard { get; set; } = true;
        public float Metallic { get; set; }
        public float Smoothness { get; set; } = 0.5f;
        public float Cutoff { get; set; } = 0.5f;
        public Color EmissionColor { get; set; } = Color.black;

        public Color color
        {
            get => _vectors.TryGetValue("_Color", out var v) ? new Color(v[0], v[1], v[2], v[3]) : Color.white;
            set => SetColor("_Color", value);
        }

        public Texture mainTexture
        {
            get => _textures.TryGetValue("_MainTex", out var t) ? t : null;
            set => SetTexture("_MainTex", value);
        }

        public Material(Shader shader) { this.shader = shader ?? Shader.Find("Standard"); name = this.shader.name; }
        public Material(string shaderName) : this(Shader.Find(shaderName)) { }

        public void SetFloat(string prop, float value) => _floats[prop] = value;
        public void SetInt(string prop, int value) { _ints[prop] = value; _floats[prop] = value; }
        public void SetVector(string prop, Vector4 value) => _vectors[prop] = new[] { value.x, value.y, value.z, value.w };
        public void SetColor(string prop, Color value) => _vectors[prop] = new[] { value.r, value.g, value.b, value.a };
        public void SetTexture(string prop, Texture value) { _textures[prop] = value; }

        public float GetFloat(string prop, float def = 0f) => _floats.TryGetValue(prop, out float v) ? v : def;
        public int GetInt(string prop, int def = 0) => _ints.TryGetValue(prop, out int v) ? v : def;
        public Texture GetTexture(string prop) => _textures.TryGetValue(prop, out var t) ? t : null;

        internal IReadOnlyDictionary<string, float> RawFloats => _floats;
        internal IReadOnlyDictionary<string, int> RawInts => _ints;
        internal IReadOnlyDictionary<string, float[]> RawVectors => _vectors;
        internal IReadOnlyDictionary<string, int> SyncAndGetTexIds()
        {
            _texIds.Clear();
            foreach (var kv in _textures)
            {
                _texIds[kv.Key] = kv.Value != null ? kv.Value.GpuTextureId : 0;
            }
            return _texIds;
        }
    }

    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture dest) => Blit(source, dest, null, -1);
        public static void Blit(Texture source, RenderTexture dest, Material mat) => Blit(source, dest, mat, -1);
        public static void Blit(Texture source, RenderTexture dest, Material mat, int pass)
        {
            if (EngineHost.Renderer == null || source == null || dest == null) return;
            string shName = mat?.shader?.name ?? "Hidden/Blit";
            EngineHost.Renderer.Blit(
                source.GpuTextureId, source.width, source.height,
                dest.GpuTextureId, dest.width, dest.height,
                shName, pass,
                mat?.RawFloats, mat?.RawInts, mat?.RawVectors, mat?.SyncAndGetTexIds());
        }
    }

    public class Mesh : Object
    {
        public float[] InterleavedVertices { get; set; } = Array.Empty<float>();
        public int VertexCount { get; set; }
        public uint[] Indices { get; set; } = Array.Empty<uint>();
        public uint[] SubmeshOffsets { get; set; } = Array.Empty<uint>();
        public uint[] SubmeshCounts { get; set; } = Array.Empty<uint>();
        public Bounds bounds { get; set; }
        public int GpuMeshId { get; internal set; }

        public void EnsureUploaded()
        {
            if (GpuMeshId <= 0 && VertexCount > 0 && Indices.Length > 0 && EngineHost.Renderer != null)
            {
                GpuMeshId = EngineHost.Renderer.UploadMesh(name, InterleavedVertices, VertexCount, Indices, SubmeshOffsets, SubmeshCounts);
            }
        }
    }

    public class MeshFilter : Component
    {
        public Mesh sharedMesh { get; set; }
        public Mesh mesh { get => sharedMesh; set => sharedMesh = value; }
    }

    public class MeshRenderer : Behaviour
    {
        public Material[] sharedMaterials { get; set; } = Array.Empty<Material>();
        public Material sharedMaterial
        {
            get => sharedMaterials.Length > 0 ? sharedMaterials[0] : null;
            set => sharedMaterials = value == null ? Array.Empty<Material>() : new[] { value };
        }
        public int lightmapIndex { get; set; } = -1;
        public Vector4 lightmapScaleOffset { get; set; } = new Vector4(1f, 1f, 0f, 0f);
        public bool isPartOfStaticBatch { get; set; }
        public int staticBatchFirstSubMesh { get; set; }
        public int staticBatchSubMeshCount { get; set; }
    }

    public class SkinnedMeshRenderer : MeshRenderer
    {
        public Mesh sharedMesh { get; set; }
    }

    public enum LightType { Spot = 0, Directional = 1, Point = 2 }

    public class Light : Behaviour
    {
        public LightType type { get; set; } = LightType.Directional;
        public Color color { get; set; } = Color.white;
        public float intensity { get; set; } = 1f;
        public float range { get; set; } = 10f;
        public float spotAngle { get; set; } = 30f;
    }

    public class Camera : Behaviour
    {
        private static Camera _main;
        public static Camera main
        {
            get
            {
                if (_main == null || _main._destroyed)
                {
                    foreach (var cam in SceneRegistry.GetAllComponents<Camera>())
                    {
                        if (cam.CompareTag("MainCamera") || _main == null) { _main = cam; if (cam.CompareTag("MainCamera")) break; }
                    }
                }
                return _main;
            }
            internal set => _main = value;
        }

        public DepthTextureMode depthTextureMode { get; set; }
        public int clearFlags { get; set; } = 1; // 1=Skybox, 2=SolidColor
        public Color backgroundColor { get; set; } = new Color(0.192f, 0.302f, 0.475f, 1f);
        public float fieldOfView { get; set; } = 60f;
        public float nearClipPlane { get; set; } = 0.3f;
        public float farClipPlane { get; set; } = 1000f;
        public int cullingMask { get; set; } = -1;
    }

    public static class RenderSettings
    {
        public static Color ambientLight { get; set; } = new Color(0.21f, 0.23f, 0.26f, 1f);
        public static float ambientIntensity { get; set; } = 1f;
        public static float reflectionIntensity { get; set; } = 1f;
        public static float[] AmbientProbeSH { get; set; }
        public static Material skybox { get; set; }
        public static bool fog { get; set; }
        public static Color fogColor { get; set; } = Color.gray;
        public static float fogDensity { get; set; } = 0.01f;

        public static void Reset()
        {
            ambientLight = new Color(0.21f, 0.23f, 0.26f, 1f);
            ambientIntensity = 1f;
            reflectionIntensity = 1f;
            AmbientProbeSH = null;
            skybox = null;
            fog = false;
            fogColor = Color.gray;
            fogDensity = 0.01f;
        }
    }

    public static class LightmapRegistry
    {
        public static readonly List<Texture2D> Lightmaps = new();
        public static readonly List<Texture2D> DirLightmaps = new();
    }
}
