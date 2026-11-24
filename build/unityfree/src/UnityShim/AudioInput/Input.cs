using UnityShim.Backend;

namespace UnityEngine
{
    public enum KeyCode
    {
        None = 0,
        Backspace = 8,
        Tab = 9,
        Clear = 12,
        Return = 13,
        Pause = 19,
        Escape = 27,
        Space = 32,
        Exclaim = 33,
        DoubleQuote = 34,
        Hash = 35,
        Dollar = 36,
        Percent = 37,
        Ampersand = 38,
        Quote = 39,
        LeftParen = 40,
        RightParen = 41,
        Asterisk = 42,
        Plus = 43,
        Comma = 44,
        Minus = 45,
        Period = 46,
        Slash = 47,
        Alpha0 = 48, Alpha1 = 49, Alpha2 = 50, Alpha3 = 51, Alpha4 = 52,
        Alpha5 = 53, Alpha6 = 54, Alpha7 = 55, Alpha8 = 56, Alpha9 = 57,
        Colon = 58, Semicolon = 59, Less = 60, Equals = 61, Greater = 62, Question = 63, At = 64,
        LeftBracket = 91, Backslash = 92, RightBracket = 93, Caret = 94, Underscore = 95, BackQuote = 96,
        A = 97, B = 98, C = 99, D = 100, E = 101, F = 102, G = 103, H = 104, I = 105,
        J = 106, K = 107, L = 108, M = 109, N = 110, O = 111, P = 112, Q = 113, R = 114,
        S = 115, T = 116, U = 117, V = 118, W = 119, X = 120, Y = 121, Z = 122,
        Delete = 127,
        UpArrow = 273, DownArrow = 274, RightArrow = 275, LeftArrow = 276,
        F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287,
        F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293,
        RightShift = 303, LeftShift = 304, RightControl = 305, LeftControl = 306, RightAlt = 307, LeftAlt = 308,
        Mouse0 = 323, Mouse1 = 324, Mouse2 = 325
    }

    public static class Input
    {
        public static bool anyKey => EngineHost.Input?.AnyKey ?? false;
        public static bool anyKeyDown => EngineHost.Input?.AnyKeyDown ?? false;
        public static bool GetKey(KeyCode key) => EngineHost.Input?.GetKey((int)key) ?? false;
        public static bool GetKeyDown(KeyCode key) => EngineHost.Input?.GetKeyDown((int)key) ?? false;
        public static bool GetKeyUp(KeyCode key) => EngineHost.Input?.GetKeyUp((int)key) ?? false;
        public static bool GetKeyDown(string name) => EngineHost.Input?.GetKeyDownName(name) ?? false;
        public static float GetAxis(string axisName) => EngineHost.Input?.GetAxis(axisName) ?? 0f;
        public static float GetAxisRaw(string axisName) => EngineHost.Input?.GetAxisRaw(axisName) ?? 0f;
        public static bool GetButtonDown(string buttonName) => EngineHost.Input?.GetButtonDown(buttonName) ?? false;
    }
}
