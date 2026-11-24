using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using StbImageSharp;
using UnityEngine;
using UnityShim.Backend;
using UnityShim.Rendering;

namespace UnityShim.Scene
{
    public sealed class AssetBank
    {
        private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AnimationClip> _animationsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AnimationClip> _animationsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AudioClip> _audioByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Mesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private Font _defaultFont;
        private bool _metadataLoaded;

        public string AssetRoot => EngineHost.AssetRoot;

        public void EnsureMetadataLoaded()
        {
            if (_metadataLoaded) return;
            _metadataLoaded = true;

            LoadFont();
            LoadSprites();
            LoadMaterials();
            LoadAnimations();
            LoadAudio();
        }

        private void LoadFont()
        {
            string defaultTtf = Path.Combine(AssetRoot, "fonts", "consola.ttf");
            if (!File.Exists(defaultTtf))
            {
                string fjson = Path.Combine(AssetRoot, "fonts.json");
                if (File.Exists(fjson))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(fjson));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        defaultTtf = Path.Combine(AssetRoot, prop.Value.GetString() ?? "");
                        break;
                    }
                }
            }
            if (File.Exists(defaultTtf))
            {
                FontAtlas.InitializeDefault(defaultTtf);
                _defaultFont = new Font { name = "consola", FontPath = defaultTtf };
            }
        }

        public Font DefaultFont => _defaultFont;

        public Texture2D LoadTexture(string relPath, bool clamp = false, bool pointFilter = false, bool generateMipmaps = false)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            string cacheKey = $"{relPath}|{(clamp ? 1 : 0)}|{(pointFilter ? 1 : 0)}|{(generateMipmaps ? 1 : 0)}";
            if (_textures.TryGetValue(cacheKey, out var cached)) return cached;

            string full = Path.Combine(AssetRoot, relPath);
            if (!File.Exists(full)) return null;

            using var fs = File.OpenRead(full);
            var img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
            var tex = new Texture2D(img.Width, img.Height) { name = Path.GetFileNameWithoutExtension(relPath) };
            tex.UploadRgba(img.Data, linear: false, clamp: clamp, pointFilter: pointFilter, generateMipmaps: generateMipmaps);
            _textures[cacheKey] = tex;
            return tex;
        }

        private void LoadSprites()
        {
            string p = Path.Combine(AssetRoot, "sprites.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var el = prop.Value;
                string texRel = el.TryGetProperty("texture", out var tEl) ? tEl.GetString() : null;
                Texture2D tex = LoadTexture(texRel, clamp: true);
                var rArr = el.GetProperty("rect");
                var sp = new Sprite
                {
                    name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : prop.Name,
                    texture = tex,
                    rect = new Rect(rArr[0].GetSingle(), rArr[1].GetSingle(), rArr[2].GetSingle(), rArr[3].GetSingle()),
                    pixelsPerUnit = el.TryGetProperty("pixelsPerUnit", out var ppu) ? ppu.GetSingle() : 100f
                };
                _sprites[prop.Name] = sp;
            }
        }

        private void LoadMaterials()
        {
            string p = Path.Combine(AssetRoot, "materials.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var el = prop.Value;
                string shName = el.GetProperty("shader").GetString() ?? "Standard";
                var mat = new Material(Shader.Find(shName))
                {
                    name = el.GetProperty("name").GetString() ?? prop.Name,
                    Hidden = el.GetProperty("hidden").GetBoolean(),
                    DoubleSided = el.GetProperty("doubleSided").GetBoolean(),
                    AlphaCutout = el.GetProperty("alphaCutout").GetBoolean(),
                    AlphaBlend = el.GetProperty("alphaBlend").GetBoolean(),
                    IsStandard = !el.TryGetProperty("isStandard", out var stdEl) || stdEl.GetBoolean(),
                    Metallic = el.TryGetProperty("metallic", out var metEl) ? metEl.GetSingle() : 0f,
                    Smoothness = el.TryGetProperty("smoothness", out var smEl) ? smEl.GetSingle() : 0.5f,
                    MainTexClamp = el.TryGetProperty("mainTexClamp", out var clEl) && clEl.GetBoolean(),
                    MainTexPoint = el.TryGetProperty("mainTexPoint", out var ptEl) && ptEl.GetBoolean(),
                    FlatShaded = el.TryGetProperty("flatShaded", out var flEl) && flEl.GetBoolean(),
                    Cutoff = el.GetProperty("cutoff").GetSingle()
                };
                var col = el.GetProperty("color");
                mat.color = new Color(col[0].GetSingle(), col[1].GetSingle(), col[2].GetSingle(), col[3].GetSingle());
                var em = el.GetProperty("emission");
                mat.EmissionColor = new Color(em[0].GetSingle(), em[1].GetSingle(), em[2].GetSingle(), em[3].GetSingle());

                if (el.TryGetProperty("mainTex", out var mtEl) && mtEl.ValueKind == JsonValueKind.String)
                {
                    string texRel = mtEl.GetString();
                    if (!mat.Hidden && !string.IsNullOrEmpty(texRel))
                    {
                        mat.mainTexture = LoadTexture(texRel, clamp: mat.MainTexClamp, pointFilter: mat.MainTexPoint, generateMipmaps: !(mat.MainTexClamp || mat.MainTexPoint));
                    }
                }
                var sc = el.GetProperty("mainTexScale");
                var of = el.GetProperty("mainTexOffset");
                mat.mainTextureScale = new Vector2(sc[0].GetSingle(), sc[1].GetSingle());
                mat.mainTextureOffset = new Vector2(of[0].GetSingle(), of[1].GetSingle());

                _materials[prop.Name] = mat;
            }
        }

        private void LoadAnimations()
        {
            string p = Path.Combine(AssetRoot, "animations.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var el = prop.Value;
                var clip = new AnimationClip
                {
                    name = el.GetProperty("name").GetString() ?? prop.Name,
                    length = el.GetProperty("length").GetSingle(),
                    wrapMode = el.GetProperty("wrapMode").GetInt32()
                };
                foreach (var cEl in el.GetProperty("curves").EnumerateArray())
                {
                    var fc = new AnimationFloatCurve
                    {
                        Attribute = cEl.GetProperty("attribute").GetString() ?? "",
                        Path = cEl.GetProperty("path").GetString() ?? ""
                    };
                    foreach (var kEl in cEl.GetProperty("keyframes").EnumerateArray())
                    {
                        fc.Curve.Keys.Add(new Keyframe
                        {
                            time = kEl.GetProperty("time").GetSingle(),
                            value = kEl.GetProperty("value").GetSingle(),
                            inTangent = kEl.GetProperty("inSlope").GetSingle(),
                            outTangent = kEl.GetProperty("outSlope").GetSingle()
                        });
                    }
                    clip.FloatCurves.Add(fc);
                }
                _animationsByKey[prop.Name] = clip;
                _animationsByName[clip.name] = clip;
            }
        }

        private void LoadAudio()
        {
            string p = Path.Combine(AssetRoot, "audio_map.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string cname = prop.Value.GetProperty("name").GetString() ?? prop.Name;
                string rel = prop.Value.GetProperty("path").GetString() ?? "";
                string full = Path.Combine(AssetRoot, rel);
                if (File.Exists(full))
                {
                    _audioByKey[prop.Name] = AudioClip.FromOggFile(cname, full);
                }
            }
        }

        public Material GetMaterial(string key) => !string.IsNullOrEmpty(key) && _materials.TryGetValue(key, out var m) ? m : null;
        public Sprite GetSprite(string key) => !string.IsNullOrEmpty(key) && _sprites.TryGetValue(key, out var s) ? s : null;
        public AnimationClip GetAnimationClip(string key) => !string.IsNullOrEmpty(key) && _animationsByKey.TryGetValue(key, out var c) ? c : null;
        public AnimationClip GetAnimationByName(string name) => !string.IsNullOrEmpty(name) && _animationsByName.TryGetValue(name, out var c) ? c : null;
        public AudioClip GetAudioClip(string key) => !string.IsNullOrEmpty(key) && _audioByKey.TryGetValue(key, out var a) ? a : null;

        public Mesh LoadMesh(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            if (_meshes.TryGetValue(relPath, out var cached)) return cached;

            string full = Path.Combine(AssetRoot, relPath);
            if (!File.Exists(full)) return null;

            using var fs = File.OpenRead(full);
            using var br = new BinaryReader(fs);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || magic[0] != (byte)'M' || magic[1] != (byte)'E' || magic[2] != (byte)'S' || magic[3] != (byte)'H')
            {
                throw new InvalidDataException($"Bad mesh magic in {relPath}");
            }
            int uncompSize = br.ReadInt32();
            byte[] raw = new byte[uncompSize];
            using (var ds = new DeflateStream(fs, CompressionMode.Decompress))
            {
                ds.ReadExactly(raw);
            }

            using var ms = new MemoryStream(raw);
            using var r = new BinaryReader(ms);
            int vCount = r.ReadInt32();
            int iCount = r.ReadInt32();
            int subCount = r.ReadInt32();
            Vector3 bc = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 be = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            uint[] subOff = new uint[subCount];
            uint[] subCnt = new uint[subCount];
            for (int i = 0; i < subCount; i++)
            {
                subOff[i] = r.ReadUInt32();
                subCnt[i] = r.ReadUInt32();
            }

            float[] verts = new float[vCount * 14];
            byte[] vBytes = r.ReadBytes(verts.Length * sizeof(float));
            Buffer.BlockCopy(vBytes, 0, verts, 0, vBytes.Length);

            uint[] indices = new uint[iCount];
            byte[] iBytes = r.ReadBytes(indices.Length * sizeof(uint));
            Buffer.BlockCopy(iBytes, 0, indices, 0, iBytes.Length);

            var mesh = new Mesh
            {
                name = Path.GetFileNameWithoutExtension(relPath),
                VertexCount = vCount,
                InterleavedVertices = verts,
                Indices = indices,
                SubmeshOffsets = subOff,
                SubmeshCounts = subCnt,
                bounds = new Bounds(bc, be * 2f)
            };
            _meshes[relPath] = mesh;
            return mesh;
        }
    }
}
