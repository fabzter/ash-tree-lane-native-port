using System;
using System.IO;
using StbTrueTypeSharp;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.Rendering
{
    public readonly struct GlyphInfo
    {
        public readonly float U0, V0, U1, V1;
        public readonly int W, H;
        public readonly int XOff, YOff;
        public readonly float Advance;

        public GlyphInfo(float u0, float v0, float u1, float v1, int w, int h, int xoff, int yoff, float adv)
        {
            U0 = u0; V0 = v0; U1 = u1; V1 = v1; W = w; H = h; XOff = xoff; YOff = yoff; Advance = adv;
        }
    }

    public unsafe sealed class FontAtlas
    {
        public static FontAtlas Default { get; private set; }
        public Texture2D AtlasTexture { get; }
        public float BasePixelHeight { get; } = 40f;
        public float AscentPx { get; }
        public float LineHeightPx { get; }
        private readonly GlyphInfo[] _glyphs = new GlyphInfo[128];

        public FontAtlas(byte[] ttfBytes)
        {
            const int atlasW = 512, atlasH = 512;
            byte[] rgba = new byte[atlasW * atlasH * 4];
            var fontInfo = new StbTrueType.stbtt_fontinfo();
            fixed (byte* pData = ttfBytes)
            {
                if (StbTrueType.stbtt_InitFont(fontInfo, pData, 0) == 0)
                {
                    AtlasTexture = Texture2D.WhiteTexture;
                    return;
                }
                float scale = StbTrueType.stbtt_ScaleForPixelHeight(fontInfo, BasePixelHeight);
                int asc, desc, gap;
                StbTrueType.stbtt_GetFontVMetrics(fontInfo, &asc, &desc, &gap);
                AscentPx = asc * scale;
                LineHeightPx = (asc - desc + gap) * scale;

                int penX = 2, penY = 2, rowH = 0;
                for (int c = 32; c <= 126; c++)
                {
                    int adv, lsb;
                    StbTrueType.stbtt_GetCodepointHMetrics(fontInfo, c, &adv, &lsb);
                    int gw, gh, xoff, yoff;
                    byte* bmp = StbTrueType.stbtt_GetCodepointBitmap(fontInfo, scale, scale, c, &gw, &gh, &xoff, &yoff);
                    if (penX + gw + 2 >= atlasW)
                    {
                        penX = 2;
                        penY += rowH + 2;
                        rowH = 0;
                    }
                    if (penY + gh + 2 < atlasH && bmp != null)
                    {
                        for (int y = 0; y < gh; y++)
                        for (int x = 0; x < gw; x++)
                        {
                            byte a = bmp[y * gw + x];
                            int dstIdx = ((penY + y) * atlasW + (penX + x)) * 4;
                            rgba[dstIdx + 0] = 255;
                            rgba[dstIdx + 1] = 255;
                            rgba[dstIdx + 2] = 255;
                            rgba[dstIdx + 3] = a;
                        }
                    }
                    if (bmp != null) StbTrueType.stbtt_FreeBitmap(bmp, null);

                    _glyphs[c] = new GlyphInfo(
                        penX / (float)atlasW, penY / (float)atlasH,
                        (penX + gw) / (float)atlasW, (penY + gh) / (float)atlasH,
                        gw, gh, xoff, yoff, adv * scale);

                    penX += gw + 2;
                    if (gh > rowH) rowH = gh;
                }
            }

            AtlasTexture = new Texture2D(atlasW, atlasH) { name = "FontAtlas" };
            AtlasTexture.UploadRgba(rgba, linear: false, clamp: true, pointFilter: false);
        }

        public static void InitializeDefault(string ttfPath)
        {
            if (Default != null) return;
            if (File.Exists(ttfPath))
            {
                Default = new FontAtlas(File.ReadAllBytes(ttfPath));
            }
        }

        public bool TryGetGlyph(char ch, out GlyphInfo info)
        {
            int idx = ch;
            if (idx >= 32 && idx < 127) { info = _glyphs[idx]; return true; }
            info = _glyphs[' '];
            return false;
        }

        public float MeasureLineWidth(string line, float scale)
        {
            float w = 0f;
            for (int i = 0; i < line.Length; i++)
            {
                TryGetGlyph(line[i], out var g);
                w += g.Advance * scale;
            }
            return w;
        }
    }
}
