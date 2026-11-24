#include "postfx_common.metal"

fragment float4 fs_blit(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    return t0.sample(s0, in.uv);
}

// --- Bloom (4 passes: prefilter, downsample, upsample, combine) ---
fragment float4 fs_bloom_prefilter(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    float threshold = u.p0.x;
    float softT = u.p0.y;
    float knee = threshold * softT + 1e-5;
    float br = max(c.r, max(c.g, c.b));
    float soft = clamp(br - threshold + knee, 0.0, 2.0 * knee);
    soft = (soft * soft) / (4.0 * knee);
    float contrib = max(soft, br - threshold) / max(br, 1e-5);
    return float4(c * max(0.0, contrib), 1.0);
}

fragment float4 fs_bloom_down(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 o = u.texelSize.xy * max(0.5, u.p0.z);
    float3 s = t0.sample(s0, in.uv + float2(-o.x, -o.y)).rgb
             + t0.sample(s0, in.uv + float2( o.x, -o.y)).rgb
             + t0.sample(s0, in.uv + float2(-o.x,  o.y)).rgb
             + t0.sample(s0, in.uv + float2( o.x,  o.y)).rgb;
    return float4(s * 0.25, 1.0);
}

fragment float4 fs_bloom_up(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 o = u.texelSize.xy * max(0.25, u.p0.w);
    float3 s = t0.sample(s0, in.uv + float2(-o.x, -o.y)).rgb * 0.125
             + t0.sample(s0, in.uv + float2( o.x, -o.y)).rgb * 0.125
             + t0.sample(s0, in.uv + float2(-o.x,  o.y)).rgb * 0.125
             + t0.sample(s0, in.uv + float2( o.x,  o.y)).rgb * 0.125
             + t0.sample(s0, in.uv).rgb * 0.5;
    return float4(s, 1.0);
}

fragment float4 fs_bloom_combine(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]], // _OriginalTex
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 bloom = t0.sample(s0, in.uv).rgb * u.p1.x; // _Intensity
    float3 orig = t1.sample(s1, in.uv).rgb;
    return float4(orig + bloom, 1.0);
}

// --- Kuwahara & Generalized / Anisotropic Kuwahara ---
fragment float4 fs_kuwahara(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    int radius = clamp(int(u.p0.x + 0.5), 1, 8);
    if (u.p0.z > 0.5)
    {
        float minR = max(1.0, u.p0.y);
        radius = clamp(int(mix(minR, float(radius), 0.5 + 0.5 * sin(u.timeVec.x * u.p0.w))), 1, 8);
    }

    float3 mean[4] = {0, 0, 0, 0};
    float3 varSum[4] = {0, 0, 0, 0};
    float cnt = float((radius + 1) * (radius + 1));

    for (int q = 0; q < 4; q++)
    {
        int sx = (q == 0 || q == 2) ? -1 : 0;
        int ex = (q == 0 || q == 2) ?  0 : 1;
        int sy = (q < 2) ? -1 : 0;
        int ey = (q < 2) ?  0 : 1;
        for (int j = sy * radius; j <= ey * radius; j++)
        for (int i = sx * radius; i <= ex * radius; i++)
        {
            float3 c = t0.sample(s0, in.uv + float2(i, j) * u.texelSize.xy).rgb;
            mean[q] += c;
            varSum[q] += c * c;
        }
        mean[q] /= cnt;
        varSum[q] = abs(varSum[q] / cnt - mean[q] * mean[q]);
    }

    float minVar = 1e9;
    float3 best = mean[0];
    for (int q = 0; q < 4; q++)
    {
        float v = varSum[q].r + varSum[q].g + varSum[q].b;
        if (v < minVar) { minVar = v; best = mean[q]; }
    }
    return float4(best, 1.0);
}

// --- PixelArtFilter ---
fragment float4 fs_pixelart(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 grid = max(float2(64.0, 36.0), u.texelSize.zw * 0.25);
    float2 snapped = (floor(in.uv * grid) + 0.5) / grid;
    float3 c = t0.sample(s0, snapped).rgb;
    c = floor(c * 24.0 + 0.5) / 24.0;
    return float4(c, 1.0);
}

// --- Blur (gaussian separable / box) ---
fragment float4 fs_blur(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    int r = clamp(int(u.p0.x + 0.5), 1, 8);
    int passIdx = int(u.timeVec.z + 0.5);
    float2 dir = (passIdx % 2 == 1) ? float2(0.0, u.texelSize.y) : float2(u.texelSize.x, 0.0);
    float sigma = max(0.5, u.p0.y);
    float3 sum = float3(0.0);
    float wSum = 0.0;
    for (int i = -r; i <= r; i++)
    {
        float w = exp(-(float(i * i)) / (2.0 * sigma * sigma));
        sum += t0.sample(s0, in.uv + dir * float(i)).rgb * w;
        wSum += w;
    }
    return float4(sum / max(wSum, 1e-5), 1.0);
}

// --- Difference of Gaussians / Extended DoG ---
fragment float4 fs_dog(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    int passIdx = int(u.timeVec.z + 0.5);
    if (passIdx == 0 || passIdx == 1)
    {
        // Horizontal or Vertical Gaussian on luminance-packed RG
        float2 axis = (passIdx == 0) ? float2(u.texelSize.x, 0.0) : float2(0.0, u.texelSize.y);
        float s1v = max(0.5, u.p0.x);
        float s2v = max(1.0, s1v * max(1.1, u.p0.y));
        float g1 = 0.0, g2 = 0.0, w1 = 0.0, w2 = 0.0;
        for (int i = -4; i <= 4; i++)
        {
            float2 d = axis * float(i);
            float3 sampleC = t0.sample(s0, in.uv + d).rgb;
            float l1 = (passIdx == 0) ? fx_luma(sampleC) : sampleC.r;
            float l2 = (passIdx == 0) ? l1 : sampleC.g;
            float k1 = exp(-float(i * i) / (2.0 * s1v * s1v));
            float k2 = exp(-float(i * i) / (2.0 * s2v * s2v));
            g1 += l1 * k1; w1 += k1;
            g2 += l2 * k2; w2 += k2;
        }
        return float4(g1 / w1, g2 / w2, 0.0, 1.0);
    }
    else
    {
        float2 g = t1.sample(s1, in.uv).rg; // _GaussianTex
        float tau = u.p0.z;
        float phi = max(0.5, u.p0.w);
        float thresh = u.p1.x;
        float diff = g.r - tau * g.g;
        float edge = diff >= thresh ? 1.0 : 1.0 + tanh(phi * (diff - thresh));
        edge = saturate(edge);
        if (u.p1.y > 0.5) edge = 1.0 - edge;
        float3 orig = t0.sample(s0, in.uv).rgb;
        return float4(orig * edge, 1.0);
    }
}

// --- Vignette ---
fragment float4 fs_vignette(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    float2 offset = u.p0.zw;
    float2 d = abs(in.uv - 0.5 - offset) * max(0.25, u.p0.x);
    float dist = pow(pow(d.x, max(1.0, u.p1.x)) + pow(d.y, max(1.0, u.p1.x)), 1.0 / max(1.0, u.p1.x));
    float vig = smoothstep(0.35, 0.35 + max(0.05, u.p0.y), dist);
    float intensity = clamp(u.p1.y, 0.0, 2.0);
    return float4(mix(c, u.c0.rgb, saturate(vig * intensity)), 1.0);
}

// --- Chromatic Aberration ---
fragment float4 fs_chromatic(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 dir = in.uv - 0.5;
    float dist = length(dir);
    float mask = smoothstep(u.p0.y, u.p0.y + max(0.05, u.p0.z), dist);
    float str = u.p0.x * 0.015 * mask;
    float r = t0.sample(s0, in.uv + dir * (u.c0.r != 0.0 ? u.c0.r * str : str)).r;
    float g = t0.sample(s0, in.uv + dir * (u.c0.g * str)).g;
    float b = t0.sample(s0, in.uv - dir * (u.c0.b != 0.0 ? u.c0.b * str : str)).b;
    return float4(r, g, b, 1.0);
}

// --- Color Correction ---
fragment float4 fs_color_correction(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb * pow(2.0, u.p0.x); // _Exposure
    c += u.p0.y; // _Brightness
    float mid = u.p0.z > 0.0 ? u.p0.z : 0.5;
    c = (c - mid) * max(0.0, u.p0.w) + mid; // _Contrast
    float l = fx_luma(c);
    c = mix(float3(l), c, max(0.0, u.p1.x)); // _Saturation
    c *= u.c0.rgb; // _ColorFilter
    return float4(saturate(c), 1.0);
}

// --- Ditherer ---
fragment float4 fs_dither(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    uint2 p = uint2(in.uv * u.texelSize.zw) & 3u;
    const float bayer4[16] = {
         0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
        12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
         3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
        15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
    };
    float b = bayer4[p.y * 4u + p.x] - 0.5;
    c += u.p0.x * b;
    float3 levels = max(float3(2.0), u.p0.yzw);
    c = floor(saturate(c) * (levels - 1.0) + 0.5) / (levels - 1.0);
    return float4(c, 1.0);
}

// --- EdgeDetector ---
fragment float4 fs_edge(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 o = u.texelSize.xy;
    float lx = fx_luma(t0.sample(s0, in.uv + float2(o.x, 0)).rgb) - fx_luma(t0.sample(s0, in.uv - float2(o.x, 0)).rgb);
    float ly = fx_luma(t0.sample(s0, in.uv + float2(0, o.y)).rgb) - fx_luma(t0.sample(s0, in.uv - float2(0, o.y)).rgb);
    float edge = saturate(sqrt(lx * lx + ly * ly) * 4.0);
    return float4(mix(t0.sample(s0, in.uv).rgb, u.c0.rgb, edge), 1.0);
}

// --- Fog, Gamma, Hue, Sharpness, Tonemapper, Zoom, BlendModes, ColorBlindness, PaletteSwapper ---
fragment float4 fs_gamma(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    return float4(pow(max(c, 0.0), float3(max(0.1, u.p0.x))), 1.0);
}

fragment float4 fs_hue_shift(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    float angle = u.p0.x * 6.2831853;
    float3 k = float3(0.57735);
    float cosA = cos(angle);
    float3 res = c * cosA + cross(k, c) * sin(angle) + k * dot(k, c) * (1.0 - cosA);
    return float4(saturate(res), 1.0);
}

fragment float4 fs_sharpness(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float2 o = u.texelSize.xy;
    float3 c = t0.sample(s0, in.uv).rgb;
    float3 blur = (t0.sample(s0, in.uv + float2(o.x, 0)).rgb
                 + t0.sample(s0, in.uv - float2(o.x, 0)).rgb
                 + t0.sample(s0, in.uv + float2(0, o.y)).rgb
                 + t0.sample(s0, in.uv - float2(0, o.y)).rgb) * 0.25;
    return float4(saturate(c + (c - blur) * u.p0.x), 1.0);
}

fragment float4 fs_tonemapper(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 x = max(t0.sample(s0, in.uv).rgb, 0.0);
    // ACES filmic approximation
    float3 mapped = (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
    return float4(saturate(mapped), 1.0);
}

fragment float4 fs_zoom(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float z = max(0.1, 1.0 + u.p0.x);
    float2 uv = (in.uv - 0.5 - u.p0.zw) / z + 0.5;
    return t0.sample(s0, saturate(uv));
}

fragment float4 fs_fog(
    PostFxVertOut in [[stage_in]],
    texture2d<float> t0 [[texture(0)]], sampler s0 [[sampler(0)]],
    texture2d<float> t1 [[texture(1)]], sampler s1 [[sampler(1)]],
    texture2d<float> t2 [[texture(2)]], sampler s2 [[sampler(2)]],
    texture2d<float> t3 [[texture(3)]], sampler s3 [[sampler(3)]],
    constant PostFxUniforms& u [[buffer(0)]])
{
    float3 c = t0.sample(s0, in.uv).rgb;
    float d = saturate(u.p0.x * 0.25);
    return float4(mix(c, u.c0.rgb, d), 1.0);
}
