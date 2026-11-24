using System;
using System.Collections.Generic;
using UnityShim.Backend;

namespace UnityEngine
{
    public struct Keyframe
    {
        public float time;
        public float value;
        public float inTangent;
        public float outTangent;
    }

    public class AnimationCurve
    {
        public readonly List<Keyframe> Keys = new List<Keyframe>();

        public float Evaluate(float t)
        {
            if (Keys.Count == 0) return 0f;
            if (t <= Keys[0].time) return Keys[0].value;
            if (t >= Keys[^1].time) return Keys[^1].value;
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                Keyframe k0 = Keys[i], k1 = Keys[i + 1];
                if (t >= k0.time && t <= k1.time)
                {
                    if (MathF.Abs(k0.outTangent) > 1e6f || MathF.Abs(k1.inTangent) > 1e6f)
                    {
                        return t >= k1.time ? k1.value : k0.value;
                    }
                    float dt = k1.time - k0.time;
                    if (dt < 1e-6f) return k1.value;
                    float u = (t - k0.time) / dt;
                    float u2 = u * u, u3 = u2 * u;
                    float h00 = 2f * u3 - 3f * u2 + 1f;
                    float h10 = u3 - 2f * u2 + u;
                    float h01 = -2f * u3 + 3f * u2;
                    float h11 = u3 - u2;
                    return h00 * k0.value + h10 * dt * k0.outTangent + h01 * k1.value + h11 * dt * k1.inTangent;
                }
            }
            return Keys[^1].value;
        }
    }

    public sealed class AnimationFloatCurve
    {
        public string Attribute = string.Empty;
        public string Path = string.Empty;
        public AnimationCurve Curve = new AnimationCurve();
    }

    public class AnimationClip : Object
    {
        public float length { get; set; }
        public int wrapMode { get; set; }
        public readonly List<AnimationFloatCurve> FloatCurves = new List<AnimationFloatCurve>();
    }

    public class Animation : Behaviour
    {
        private readonly Dictionary<string, AnimationClip> _clips = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        private AnimationClip _activeClip;
        private float _time;

        public AnimationClip clip { get; set; }
        public bool playAutomatically { get; set; }
        public bool isPlaying { get; private set; }

        public void AddClip(AnimationClip c, string clipName)
        {
            if (c == null) return;
            _clips[clipName ?? c.name] = c;
            if (clip == null) clip = c;
        }

        public bool Play()
        {
            if (clip == null) return false;
            _activeClip = clip;
            _time = 0f;
            isPlaying = true;
            EvaluateAt(0f);
            return true;
        }

        public bool Play(string animation)
        {
            if (!string.IsNullOrEmpty(animation) && _clips.TryGetValue(animation, out var found))
            {
                _activeClip = found;
                _time = 0f;
                isPlaying = true;
                EvaluateAt(0f);
                return true;
            }
            return Play();
        }

        public void Stop() => isPlaying = false;

        internal void Step(float dt)
        {
            if (!isPlaying || _activeClip == null || !isActiveAndEnabled) return;
            _time += dt;
            if (_time >= _activeClip.length)
            {
                EvaluateAt(_activeClip.length);
                if (_activeClip.wrapMode == 2) // Loop
                {
                    _time %= MathF.Max(0.001f, _activeClip.length);
                }
                else
                {
                    isPlaying = false;
                }
            }
            else
            {
                EvaluateAt(_time);
            }
        }

        private void EvaluateAt(float t)
        {
            var g = gameObject?.GetComponent<UnityEngine.UI.Graphic>();
            foreach (var fc in _activeClip.FloatCurves)
            {
                float val = fc.Curve.Evaluate(t);
                if (g != null && fc.Attribute == "m_Color.a")
                {
                    var c = g.color;
                    g.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(val));
                }
                else if (fc.Attribute == "m_Enabled")
                {
                    if (g != null) g.enabled = val >= 0.5f;
                }
            }
        }

        public static void StepAll(float dt)
        {
            foreach (var anim in SceneRegistry.GetAllComponents<Animation>())
            {
                anim.Step(dt);
            }
        }
    }
}
