using System;
using UnityShim.Backend;

namespace UnityEngine
{
    [Flags]
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SpaceAttribute : Attribute { public SpaceAttribute(float height = 8f) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HideInInspector : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RequireComponent : Attribute
    {
        public RequireComponent(Type requiredComponent) { }
        public RequireComponent(Type requiredComponent, Type requiredComponent2) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ExecuteInEditMode : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ImageEffectOpaque : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ColorUsageAttribute : Attribute
    {
        public ColorUsageAttribute(bool showAlpha) { }
        public ColorUsageAttribute(bool showAlpha, bool hdr) { }
    }

    public static class Time
    {
        public static float deltaTime { get; set; } = 1f / 60f;
        public static float fixedDeltaTime { get; set; } = 1f / 60f;
        public static float time { get; set; }
        public static float unscaledTime { get; set; }
        public static float timeScale { get; set; } = 1f;
        public static int frameCount { get; set; }
    }

    public static class Debug
    {
        public static void Log(object message) => Console.WriteLine($"[Log] {message}");
        public static void LogWarning(object message) => Console.WriteLine($"[Warn] {message}");
        public static void LogError(object message) => Console.Error.WriteLine($"[Error] {message}");
        public static void DrawLine(Vector3 start, Vector3 end, Color color) { }
        public static void DrawLine(Vector3 start, Vector3 end) { }
    }

    public static class Screen
    {
        public static int width { get; set; } = 1280;
        public static int height { get; set; } = 720;
    }

    public enum CursorLockMode { None = 0, Locked = 1, Confined = 2 }

    public static class Cursor
    {
        private static CursorLockMode _lockState = CursorLockMode.None;
        public static CursorLockMode lockState
        {
            get => _lockState;
            set
            {
                _lockState = value;
                EngineHost.Input?.SetCursorLocked(value == CursorLockMode.Locked);
            }
        }
        public static bool visible { get; set; } = true;
    }
}
