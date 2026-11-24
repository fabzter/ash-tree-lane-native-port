namespace UnityEngine
{
    public class Sprite : Object
    {
        public Texture2D texture { get; set; }
        public Rect rect { get; set; }
        public float pixelsPerUnit { get; set; } = 100f;
    }

    public class Font : Object
    {
        public string FontPath { get; set; } = string.Empty;
    }

    public class Canvas : Behaviour
    {
        public int renderMode { get; set; }
        public int sortingOrder { get; set; }
    }
}

namespace UnityEngine.UI
{
    public enum TextAnchor
    {
        UpperLeft = 0, UpperCenter = 1, UpperRight = 2,
        MiddleLeft = 3, MiddleCenter = 4, MiddleRight = 5,
        LowerLeft = 6, LowerCenter = 7, LowerRight = 8
    }

    public class Graphic : MonoBehaviour
    {
        public Color color { get; set; } = Color.white;
    }

    public class Image : Graphic
    {
        public Sprite sprite { get; set; }
        public int imageType { get; set; }
        public bool preserveAspect { get; set; }
    }

    public class Text : Graphic
    {
        public string text { get; set; } = string.Empty;
        public Font font { get; set; }
        public int fontSize { get; set; } = 14;
        public int alignment { get; set; } = 4;
    }

    public class CanvasScaler : MonoBehaviour
    {
        public int uiScaleMode { get; set; }
        public float referencePixelsPerUnit { get; set; } = 100f;
        public float scaleFactor { get; set; } = 1f;
        public Vector2 referenceResolution { get; set; } = new Vector2(800f, 600f);
        public int screenMatchMode { get; set; }
        public float matchWidthOrHeight { get; set; }
    }
}
