using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using UnityEngine.UI;
using UnityShim.Backend;

namespace UnityShim.Scene
{
    public sealed class SceneLoader : ISceneController
    {
        private readonly AssetBank _assets;
        private readonly Dictionary<string, Type> _scriptTypes = new(StringComparer.Ordinal);
        private int _activeSceneIndex = -1;
        private string _activeSceneName = "level0";
        private int? _pendingSceneIndex;

        public AssetBank Assets => _assets;
        public int ActiveSceneIndex => _activeSceneIndex;
        public string ActiveSceneName => _activeSceneName;
        public string ActivePostFxOverride { get; set; }

        public SceneLoader(AssetBank assets)
        {
            _assets = assets;
            DiscoverScriptTypes();
        }

        private void DiscoverScriptTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string aname = asm.GetName().Name ?? "";
                if (!aname.Contains("GameScripts") && !aname.Contains("UnityShim")) continue;
                foreach (var t in asm.GetTypes())
                {
                    if (typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract)
                    {
                        _scriptTypes[t.Name] = t;
                    }
                }
            }
        }

        public void RequestLoadScene(int buildIndex)
        {
            if (buildIndex >= 0 && buildIndex <= 2 && _pendingSceneIndex == null)
            {
                _pendingSceneIndex = buildIndex;
            }
        }

        public bool ConsumePendingSceneLoad()
        {
            if (!_pendingSceneIndex.HasValue) return false;
            int idx = _pendingSceneIndex.Value;
            _pendingSceneIndex = null;
            LoadSceneNow(idx);
            return true;
        }

        public void LoadSceneNow(int index)
        {
            _assets.EnsureMetadataLoaded();

            EngineHost.Audio?.StopAllVoices();
            if (EngineHost.Input is AudioInput.SdlInputBackend sinp) sinp.ClearAllKeys();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SceneRegistry.Clear();
            Camera.main = null;
            RenderSettings.Reset();
            LightmapRegistry.Lightmaps.Clear();
            LightmapRegistry.DirLightmaps.Clear();

            string scenePath = Path.Combine(_assets.AssetRoot, "scenes", $"scene_{index}.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(scenePath));
            var root = doc.RootElement;

            _activeSceneIndex = root.GetProperty("sceneIndex").GetInt32();
            _activeSceneName = root.GetProperty("sceneName").GetString() ?? $"level{index}";

            if (root.TryGetProperty("renderSettings", out var rsEl) && rsEl.ValueKind == JsonValueKind.Object)
            {
                var amb = rsEl.GetProperty("ambientLight");
                RenderSettings.ambientLight = new Color(amb[0].GetSingle(), amb[1].GetSingle(), amb[2].GetSingle(), amb[3].GetSingle());
                RenderSettings.ambientIntensity = rsEl.GetProperty("ambientIntensity").GetSingle();
                RenderSettings.reflectionIntensity = rsEl.TryGetProperty("reflectionIntensity", out var riEl) ? riEl.GetSingle() : 1f;
                if (rsEl.TryGetProperty("ambientProbe", out var apEl) && apEl.ValueKind == JsonValueKind.Array && apEl.GetArrayLength() == 27)
                {
                    float[] sh = new float[27];
                    for (int si = 0; si < 27; si++) sh[si] = apEl[si].GetSingle();
                    RenderSettings.AmbientProbeSH = sh;
                }
                RenderSettings.fog = rsEl.GetProperty("fog").GetBoolean();
                var fg = rsEl.GetProperty("fogColor");
                RenderSettings.fogColor = new Color(fg[0].GetSingle(), fg[1].GetSingle(), fg[2].GetSingle(), fg[3].GetSingle());
                RenderSettings.fogDensity = rsEl.GetProperty("fogDensity").GetSingle();
            }

            if (root.TryGetProperty("lightmaps", out var lmArr))
            {
                foreach (var lmEl in lmArr.EnumerateArray())
                {
                    if (lmEl.ValueKind == JsonValueKind.String)
                    {
                        LightmapRegistry.Lightmaps.Add(_assets.LoadTexture(lmEl.GetString(), clamp: true));
                    }
                    else
                    {
                        LightmapRegistry.Lightmaps.Add(null);
                    }
                }
            }
            if (root.TryGetProperty("dirLightmaps", out var dmArr))
            {
                foreach (var dmEl in dmArr.EnumerateArray())
                {
                    if (dmEl.ValueKind == JsonValueKind.String)
                    {
                        LightmapRegistry.DirLightmaps.Add(_assets.LoadTexture(dmEl.GetString(), clamp: true));
                    }
                    else
                    {
                        LightmapRegistry.DirLightmaps.Add(null);
                    }
                }
            }

            var goByPid = new Dictionary<long, GameObject>();
            var initialActive = new Dictionary<long, bool>();
            var compByPid = new Dictionary<long, Component>();
            var deferredMonoFields = new List<(MonoBehaviour mb, JsonElement fields)>();
            var transformsToWire = new List<(GameObject go, JsonElement trEl)>();

            // Pass 1: Create all GameObjects, Transforms, and Components cleanly BEFORE activation
            foreach (var goEl in root.GetProperty("gameObjects").EnumerateArray())
            {
                long goPid = goEl.GetProperty("pid").GetInt64();
                string goName = goEl.GetProperty("name").GetString() ?? "GameObject";
                bool activeSelf = goEl.GetProperty("activeSelf").GetBoolean();
                var trEl = goEl.GetProperty("transform");
                bool isRect = trEl.ValueKind != JsonValueKind.Null && trEl.GetProperty("isRectTransform").GetBoolean();

                var go = new GameObject(goName, isRect)
                {
                    tag = goEl.GetProperty("tag").GetString() ?? "Untagged",
                    layer = goEl.GetProperty("layer").GetInt32()
                };
                go.SetInitialActiveSelf(false); // keep inactive until fields are populated
                goByPid[goPid] = go;
                initialActive[goPid] = activeSelf;

                if (trEl.ValueKind != JsonValueKind.Null)
                {
                    long trPid = trEl.GetProperty("pid").GetInt64();
                    compByPid[trPid] = go.transform;
                    transformsToWire.Add((go, trEl));
                }

                foreach (var cEl in goEl.GetProperty("components").EnumerateArray())
                {
                    string ctype = cEl.GetProperty("type").GetString() ?? "";
                    long cpid = cEl.GetProperty("pid").GetInt64();
                    bool cEnabled = !cEl.TryGetProperty("enabled", out var enEl) || enEl.GetBoolean();

                    Component comp = CreateComponent(go, ctype, cEnabled, cEl, deferredMonoFields);
                    if (comp != null)
                    {
                        compByPid[cpid] = comp;
                    }
                }
            }

            // Wire Transform parent/sibling/local TRS
            foreach (var (go, trEl) in transformsToWire)
            {
                Transform tr = go.transform;
                tr.siblingOrder = trEl.GetProperty("siblingOrder").GetInt32();
                var lp = trEl.GetProperty("localPosition");
                var lr = trEl.GetProperty("localRotation");
                var ls = trEl.GetProperty("localScale");
                tr.localPosition = new Vector3(lp[0].GetSingle(), lp[1].GetSingle(), lp[2].GetSingle());
                tr.localRotation = new Quaternion(lr[0].GetSingle(), lr[1].GetSingle(), lr[2].GetSingle(), lr[3].GetSingle());
                tr.localScale = new Vector3(ls[0].GetSingle(), ls[1].GetSingle(), ls[2].GetSingle());

                if (tr is RectTransform rt)
                {
                    var amin = trEl.GetProperty("anchorMin");
                    var amax = trEl.GetProperty("anchorMax");
                    var apos = trEl.GetProperty("anchoredPosition");
                    var sd = trEl.GetProperty("sizeDelta");
                    var piv = trEl.GetProperty("pivot");
                    rt.anchorMin = new Vector2(amin[0].GetSingle(), amin[1].GetSingle());
                    rt.anchorMax = new Vector2(amax[0].GetSingle(), amax[1].GetSingle());
                    rt.anchoredPosition = new Vector2(apos[0].GetSingle(), apos[1].GetSingle());
                    rt.sizeDelta = new Vector2(sd[0].GetSingle(), sd[1].GetSingle());
                    rt.pivot = new Vector2(piv[0].GetSingle(), piv[1].GetSingle());
                }
            }

            foreach (var (go, trEl) in transformsToWire)
            {
                if (trEl.TryGetProperty("parentGoPid", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
                {
                    long pPid = pEl.GetInt64();
                    if (goByPid.TryGetValue(pPid, out var parentGo))
                    {
                        go.transform.SetParent(parentGo.transform, worldPositionStays: false);
                    }
                }
            }

            // Pass 2: Populate MonoBehaviour fields BEFORE triggering Awake/OnEnable
            foreach (var (mb, fields) in deferredMonoFields)
            {
                PopulateMonoFields(mb, fields, goByPid, compByPid);
            }

            // Optional CLI/hotkey Post-FX script attachment on Camera.main
            if (!string.IsNullOrEmpty(ActivePostFxOverride) && ActivePostFxOverride != "none" && Camera.main != null)
            {
                AttachPostFxByName(Camera.main.gameObject, ActivePostFxOverride);
            }

            // Pass 3: Apply initial activeSelf & trigger Awake/OnEnable/Start + auto Animations/Audio
            foreach (var kv in initialActive)
            {
                if (goByPid.TryGetValue(kv.Key, out var go))
                {
                    go.SetInitialActiveSelf(kv.Value);
                }
            }

            SceneRegistry.ActivateSceneAndDispatchLifecycle();

            foreach (var anim in SceneRegistry.GetAllComponents<Animation>())
            {
                if (anim.playAutomatically && anim.isActiveAndEnabled && anim.clip != null) anim.Play();
            }
            foreach (var src in SceneRegistry.GetAllComponents<AudioSource>())
            {
                if (src.playOnAwake && src.isActiveAndEnabled && src.clip != null) src.Play();
            }

            Console.WriteLine($"[SceneLoader] Loaded scene {index} ({_activeSceneName}): {goByPid.Count} objects, {LightmapRegistry.Lightmaps.Count} lightmaps.");
        }

        private Component CreateComponent(GameObject go, string ctype, bool enabled, JsonElement el,
                                          List<(MonoBehaviour mb, JsonElement fields)> deferredMono)
        {
            switch (ctype)
            {
                case "Camera":
                {
                    var c = new Camera();
                    c.SetEnabledDirect(enabled);
                    c.clearFlags = el.GetProperty("clearFlags").GetInt32();
                    var bg = el.GetProperty("backgroundColor");
                    c.backgroundColor = new Color(bg[0].GetSingle(), bg[1].GetSingle(), bg[2].GetSingle(), bg[3].GetSingle());
                    c.fieldOfView = el.GetProperty("fov").GetSingle();
                    c.nearClipPlane = el.GetProperty("near").GetSingle();
                    c.farClipPlane = el.GetProperty("far").GetSingle();
                    var cmEl = el.GetProperty("cullingMask");
                    c.cullingMask = cmEl.TryGetInt32(out int cmSigned) ? cmSigned : unchecked((int)cmEl.GetUInt32());
                    go.AttachComponent(c, triggerLifecycleNow: false);
                    if (go.CompareTag("MainCamera") || Camera.main == null) Camera.main = c;
                    return c;
                }
                case "Light":
                {
                    var l = new Light();
                    l.SetEnabledDirect(enabled);
                    l.type = (LightType)el.GetProperty("lightType").GetInt32();
                    var col = el.GetProperty("color");
                    l.color = new Color(col[0].GetSingle(), col[1].GetSingle(), col[2].GetSingle(), col[3].GetSingle());
                    l.intensity = el.GetProperty("intensity").GetSingle();
                    l.range = el.GetProperty("range").GetSingle();
                    l.spotAngle = el.GetProperty("spotAngle").GetSingle();
                    go.AttachComponent(l, triggerLifecycleNow: false);
                    return l;
                }
                case "MeshFilter":
                {
                    var mf = new MeshFilter();
                    if (el.TryGetProperty("mesh", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                    {
                        mf.sharedMesh = _assets.LoadMesh(mEl.GetString());
                    }
                    go.AttachComponent(mf, triggerLifecycleNow: false);
                    return mf;
                }
                case "MeshRenderer":
                case "SkinnedMeshRenderer":
                {
                    MeshRenderer mr = ctype == "SkinnedMeshRenderer" ? new SkinnedMeshRenderer() : new MeshRenderer();
                    mr.SetEnabledDirect(enabled);
                    var matArr = el.GetProperty("materials");
                    var mats = new Material[matArr.GetArrayLength()];
                    for (int i = 0; i < mats.Length; i++)
                    {
                        string mk = matArr[i].ValueKind == JsonValueKind.String ? matArr[i].GetString() : null;
                        mats[i] = _assets.GetMaterial(mk);
                    }
                    mr.sharedMaterials = mats;
                    mr.lightmapIndex = el.GetProperty("lightmapIndex").GetInt32();
                    var st = el.GetProperty("lightmapScaleOffset");
                    mr.lightmapScaleOffset = new Vector4(st[0].GetSingle(), st[1].GetSingle(), st[2].GetSingle(), st[3].GetSingle());
                    mr.isPartOfStaticBatch = el.GetProperty("isPartOfStaticBatch").GetBoolean();
                    mr.staticBatchFirstSubMesh = el.GetProperty("staticBatchFirstSubMesh").GetInt32();
                    mr.staticBatchSubMeshCount = el.GetProperty("staticBatchSubMeshCount").GetInt32();
                    if (mr is SkinnedMeshRenderer smr && el.TryGetProperty("mesh", out var smEl) && smEl.ValueKind == JsonValueKind.String)
                    {
                        smr.sharedMesh = _assets.LoadMesh(smEl.GetString());
                    }
                    go.AttachComponent(mr, triggerLifecycleNow: false);
                    return mr;
                }
                case "BoxCollider":
                {
                    var bc = new BoxCollider();
                    bc.SetEnabledDirect(enabled);
                    bc.isTrigger = el.GetProperty("isTrigger").GetBoolean();
                    var cen = el.GetProperty("center"); var sz = el.GetProperty("size");
                    bc.center = new Vector3(cen[0].GetSingle(), cen[1].GetSingle(), cen[2].GetSingle());
                    bc.size = new Vector3(sz[0].GetSingle(), sz[1].GetSingle(), sz[2].GetSingle());
                    go.AttachComponent(bc, triggerLifecycleNow: false);
                    return bc;
                }
                case "CapsuleCollider":
                {
                    var cc = new CapsuleCollider();
                    cc.SetEnabledDirect(enabled);
                    cc.isTrigger = el.GetProperty("isTrigger").GetBoolean();
                    var cen = el.GetProperty("center");
                    cc.center = new Vector3(cen[0].GetSingle(), cen[1].GetSingle(), cen[2].GetSingle());
                    cc.radius = el.GetProperty("radius").GetSingle();
                    cc.height = el.GetProperty("height").GetSingle();
                    cc.direction = el.GetProperty("direction").GetInt32();
                    go.AttachComponent(cc, triggerLifecycleNow: false);
                    return cc;
                }
                case "MeshCollider":
                {
                    var mc = new MeshCollider();
                    mc.SetEnabledDirect(enabled);
                    mc.isTrigger = el.GetProperty("isTrigger").GetBoolean();
                    mc.convex = el.GetProperty("convex").GetBoolean();
                    if (el.TryGetProperty("mesh", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                    {
                        mc.sharedMesh = _assets.LoadMesh(mEl.GetString());
                    }
                    go.AttachComponent(mc, triggerLifecycleNow: false);
                    return mc;
                }
                case "Rigidbody":
                {
                    var rb = new Rigidbody
                    {
                        mass = el.GetProperty("mass").GetSingle(),
                        drag = el.GetProperty("drag").GetSingle(),
                        useGravity = el.GetProperty("useGravity").GetBoolean(),
                        isKinematic = el.GetProperty("isKinematic").GetBoolean(),
                        constraints = (RigidbodyConstraints)el.GetProperty("constraints").GetInt32()
                    };
                    go.AttachComponent(rb, triggerLifecycleNow: false);
                    return rb;
                }
                case "AudioSource":
                {
                    var src = new AudioSource();
                    src.SetEnabledDirect(enabled);
                    if (el.TryGetProperty("clip", out var clEl) && clEl.ValueKind == JsonValueKind.String)
                    {
                        src.clip = _assets.GetAudioClip(clEl.GetString());
                    }
                    src.playOnAwake = el.GetProperty("playOnAwake").GetBoolean();
                    src.loop = el.GetProperty("loop").GetBoolean();
                    src.volume = el.GetProperty("volume").GetSingle();
                    src.pitch = el.GetProperty("pitch").GetSingle();
                    src.spatialBlend = el.GetProperty("spatialBlend").GetSingle();
                    src.minDistance = el.GetProperty("minDistance").GetSingle();
                    src.maxDistance = el.GetProperty("maxDistance").GetSingle();
                    go.AttachComponent(src, triggerLifecycleNow: false);
                    return src;
                }
                case "Animation":
                {
                    var a = new Animation();
                    a.SetEnabledDirect(enabled);
                    a.playAutomatically = el.GetProperty("playAutomatically").GetBoolean();
                    if (el.TryGetProperty("clip", out var clEl) && clEl.ValueKind == JsonValueKind.String)
                    {
                        a.clip = _assets.GetAnimationClip(clEl.GetString());
                        if (a.clip != null) a.AddClip(a.clip, a.clip.name);
                    }
                    if (el.TryGetProperty("clips", out var arr))
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var ac = _assets.GetAnimationClip(item.GetString());
                            if (ac != null) a.AddClip(ac, ac.name);
                        }
                    }
                    go.AttachComponent(a, triggerLifecycleNow: false);
                    return a;
                }
                case "Canvas":
                {
                    var cv = new Canvas();
                    cv.SetEnabledDirect(enabled);
                    cv.renderMode = el.GetProperty("renderMode").GetInt32();
                    cv.sortingOrder = el.GetProperty("sortingOrder").GetInt32();
                    go.AttachComponent(cv, triggerLifecycleNow: false);
                    return cv;
                }
                case "EventSystem":
                case "GraphicRaycaster":
                case "StandaloneInputModule":
                    return null;
                default:
                {
                    if (_scriptTypes.TryGetValue(ctype, out var t))
                    {
                        var mb = (MonoBehaviour)Activator.CreateInstance(t)!;
                        mb.SetEnabledDirect(enabled);
                        go.AttachComponent(mb, triggerLifecycleNow: false);
                        if (el.TryGetProperty("fields", out var flEl))
                        {
                            deferredMono.Add((mb, flEl));
                        }
                        return mb;
                    }
                    Console.WriteLine($"[SceneLoader] Warning: Unknown component {ctype}");
                    return null;
                }
            }
        }

        private void PopulateMonoFields(MonoBehaviour mb, JsonElement fields,
                                        Dictionary<long, GameObject> goByPid,
                                        Dictionary<long, Component> compByPid)
        {
            if (mb is Image img)
            {
                if (fields.TryGetProperty("color", out var c)) img.color = new Color(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle(), c[3].GetSingle());
                if (fields.TryGetProperty("sprite", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("assetKey", out var sk)) img.sprite = _assets.GetSprite(sk.GetString());
                if (fields.TryGetProperty("preserveAspect", out var pa)) img.preserveAspect = pa.GetBoolean();
                return;
            }
            if (mb is Text txt)
            {
                if (fields.TryGetProperty("color", out var c)) txt.color = new Color(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle(), c[3].GetSingle());
                if (fields.TryGetProperty("fontSize", out var fs)) txt.fontSize = fs.GetInt32();
                if (fields.TryGetProperty("alignment", out var al)) txt.alignment = al.GetInt32();
                if (fields.TryGetProperty("text", out var tx)) txt.text = tx.GetString() ?? "";
                txt.font = _assets.DefaultFont;
                return;
            }

            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = mb.GetType();
            foreach (var prop in fields.EnumerateObject())
            {
                FieldInfo fi = FindField(t, prop.Name, F);
                if (fi == null) continue;
                object val = ResolveFieldValue(fi.FieldType, prop.Value, goByPid, compByPid);
                if (val != null || !fi.FieldType.IsValueType)
                {
                    fi.SetValue(mb, val);
                }
            }
        }

        private static FieldInfo FindField(Type t, string name, BindingFlags flags)
        {
            for (Type cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
            {
                var fi = cur.GetField(name, flags);
                if (fi != null) return fi;
            }
            return null;
        }

        private object ResolveFieldValue(Type targetType, JsonElement val,
                                         Dictionary<long, GameObject> goByPid,
                                         Dictionary<long, Component> compByPid)
        {
            if (val.ValueKind == JsonValueKind.Null) return null;
            if (targetType == typeof(float)) return val.GetSingle();
            if (targetType == typeof(int)) return val.GetInt32();
            if (targetType == typeof(bool)) return val.GetBoolean();
            if (targetType == typeof(string)) return val.GetString();
            if (targetType.IsEnum) return Enum.ToObject(targetType, val.GetInt32());

            if (targetType == typeof(GameObject) && val.ValueKind == JsonValueKind.Object && val.TryGetProperty("goPid", out var gpid))
            {
                return goByPid.TryGetValue(gpid.GetInt64(), out var g) ? g : null;
            }
            if (typeof(Component).IsAssignableFrom(targetType) && val.ValueKind == JsonValueKind.Object && val.TryGetProperty("compPid", out var cpid))
            {
                return compByPid.TryGetValue(cpid.GetInt64(), out var comp) && targetType.IsInstanceOfType(comp) ? comp : null;
            }
            if (targetType == typeof(AudioClip) && val.ValueKind == JsonValueKind.Object && val.TryGetProperty("assetKey", out var ak))
            {
                return _assets.GetAudioClip(ak.GetString());
            }
            if (targetType.IsArray && val.ValueKind == JsonValueKind.Array)
            {
                Type elemType = targetType.GetElementType()!;
                var arr = Array.CreateInstance(elemType, val.GetArrayLength());
                int i = 0;
                foreach (var item in val.EnumerateArray())
                {
                    arr.SetValue(ResolveFieldValue(elemType, item, goByPid, compByPid), i++);
                }
                return arr;
            }
            return null;
        }

        public void AttachPostFxByName(GameObject targetGo, string name)
        {
            string norm = name.Trim().ToLowerInvariant();
            string className = norm switch
            {
                "bloom" => "Bloom",
                "kuwahara" => "Kuwahara",
                "anisotropickuwahara" or "aniso" => "AnisotropicKuwahara",
                "generalizedkuwahara" or "genkuwahara" => "GeneralizedKuwahara",
                "pixelart" or "pixelartfilter" => "PixelArtFilter",
                "dog" or "differenceofgaussians" => "DifferenceOfGaussians",
                "extendeddog" or "extdog" => "ExtendedDoG",
                "vignette" => "Vignette",
                "chromatic" or "chromaticaberration" => "ChromaticAberration",
                "colorcorrection" => "ColorCorrection",
                "dither" or "ditherer" => "Ditherer",
                "edge" or "edgedetector" => "EdgeDetector",
                "blur" => "Blur",
                "fog" => "Fog",
                "gamma" or "gammacorrector" => "GammaCorrector",
                "hue" or "hueshifter" => "HueShifter",
                "sharpness" or "sharpnessdispatcher" => "SharpnessDispatcher",
                "tonemapper" => "Tonemapper",
                "zoom" => "Zoom",
                _ => name
            };

            if (!_scriptTypes.TryGetValue(className, out Type fxType)) return;
            var mb = (MonoBehaviour)Activator.CreateInstance(fxType)!;
            foreach (var fi in fxType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (fi.FieldType == typeof(Shader))
                {
                    fi.SetValue(mb, Shader.Find(className));
                }
            }
            // Tune default post-fx properties for clear live preview while keeping scene logic verbatim
            SetFloatIfPresent(fxType, mb, "bloomIntensity", 2.5f);
            SetFloatIfPresent(fxType, mb, "threshold", 0.08f);
            SetFloatIfPresent(fxType, mb, "softThreshold", 0.5f);
            SetIntIfPresent(fxType, mb, "downSamples", className == "PixelArtFilter" ? 2 : 5);
            SetIntIfPresent(fxType, mb, "kernelSize", 6);
            SetIntIfPresent(fxType, mb, "passes", 2);
            SetFloatIfPresent(fxType, mb, "intensity", 2.2f);
            SetFloatIfPresent(fxType, mb, "smoothness", 0.45f);
            SetFloatIfPresent(fxType, mb, "phi", 12f);
            SetFloatIfPresent(fxType, mb, "threshold", className == "DifferenceOfGaussians" ? 0.002f : 0.08f);
            var vc = fxType.GetField("vignetteColor", BindingFlags.Public | BindingFlags.Instance);
            if (vc != null && vc.FieldType == typeof(Color)) vc.SetValue(mb, Color.black);

            targetGo.AttachComponent(mb, triggerLifecycleNow: true);
            SceneRegistry.DispatchPendingStarts();
            Console.WriteLine($"[PostFX] Attached {className} to Camera.main");
        }

        private static void SetFloatIfPresent(Type t, object o, string field, float v)
        {
            var fi = t.GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (fi != null && fi.FieldType == typeof(float)) fi.SetValue(o, v);
        }

        private static void SetIntIfPresent(Type t, object o, string field, int v)
        {
            var fi = t.GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (fi != null && fi.FieldType == typeof(int)) fi.SetValue(o, v);
        }
    }
}
