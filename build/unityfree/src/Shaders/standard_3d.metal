#include <metal_stdlib>
using namespace metal;

struct VertexIn
{
    float3 pos [[attribute(0)]];
    float3 normal [[attribute(1)]];
    float2 uv0 [[attribute(2)]];
    float2 uv1 [[attribute(3)]];
    float4 color [[attribute(4)]];
};

struct VertexOut
{
    float4 clipPos [[position]];
    float3 worldPos;
    float3 worldNormal;
    float3 flatNormal [[flat]];
    float2 uv0;
    float2 lmUv;
    float4 color;
};

struct VertexUniforms
{
    float4x4 mvp;
    float4x4 model;
    float4 mainTexST;
    float4 lightmapST;
};

struct LightData
{
    float4 posType;        // xyz = world pos, w = type (0=Spot, 1=Dir, 2=Point)
    float4 dirRange;       // xyz = forward light dir, w = range
    float4 colorIntensity; // rgb = linear/srgb color, a = intensity
    float4 spotParams;     // x = cosOuter, y = cosInner, z,w unused
};

struct FragUniforms
{
    float4 baseColor;
    float4 emissionColor;
    float4 ambientColor;
    float4 cameraPosCutoff; // xyz = camPos, w = cutoff
    float4 flags;           // x = hasAlbedo, y = hasLightmap, z = alphaCutout, w = lightCount
    float4 fogParams;       // rgb = fogColor, w = fogDensity (<0 disabled)
    float4 pbrParams;       // x = metallic, y = smoothness, z = isStandard, w = hasDirLightmap
    float4 shAr;
    float4 shAg;
    float4 shAb;
    float4 shBr;
    float4 shBg;
    float4 shBb;
    float4 shC;
    LightData lights[4];
};

inline float3 srgbToLinear(float3 c)
{
    float3 lo = c / 12.92;
    float3 hi = pow(max((c + 0.055) / 1.055, 0.0), float3(2.4));
    return select(hi, lo, c <= 0.04045);
}

inline float3 linearToSrgb(float3 c)
{
    c = clamp(c, 0.0, 1.0);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, float3(1.0 / 2.4)) - 0.055;
    return select(hi, lo, c <= 0.0031308);
}

inline float3 shadeSH9(float3 n, constant FragUniforms& u)
{
    float4 norm = float4(n, 1.0);
    float3 x1;
    x1.r = dot(u.shAr, norm);
    x1.g = dot(u.shAg, norm);
    x1.b = dot(u.shAb, norm);

    float4 vB = norm.xyzz * norm.yzzx;
    float3 x2;
    x2.r = dot(u.shBr, vB);
    x2.g = dot(u.shBg, vB);
    x2.b = dot(u.shBb, vB);

    float vC = norm.x * norm.x - norm.y * norm.y;
    float3 x3 = u.shC.rgb * vC;
    return max(x1 + x2 + x3, 0.0);
}

vertex VertexOut vs_standard(
    VertexIn in [[stage_in]],
    constant VertexUniforms& u [[buffer(0)]])
{
    VertexOut out;
    float4 world = u.model * float4(in.pos, 1.0);
    out.clipPos = u.mvp * float4(in.pos, 1.0);
    out.worldPos = world.xyz;
    float3 wn = normalize((u.model * float4(in.normal, 0.0)).xyz + float3(0.0, 1e-5, 0.0));
    out.worldNormal = wn;
    out.flatNormal = wn;
    float2 u0 = in.uv0 * u.mainTexST.xy + u.mainTexST.zw;
    out.uv0 = float2(u0.x, 1.0 - u0.y);
    float2 lm = in.uv1 * u.lightmapST.xy + u.lightmapST.zw;
    out.lmUv = float2(lm.x, 1.0 - lm.y);
    out.color = in.color;
    return out;
}

fragment float4 fs_standard(
    VertexOut in [[stage_in]],
    texture2d<float> albedoTex [[texture(0)]],
    sampler albedoSamp [[sampler(0)]],
    texture2d<float> lmTex [[texture(1)]],
    sampler lmSamp [[sampler(1)]],
    texture2d<float> dirLmTex [[texture(2)]],
    sampler dirLmSamp [[sampler(2)]],
    constant FragUniforms& u [[buffer(0)]])
{
    float4 tex = u.flags.x > 0.5 ? albedoTex.sample(albedoSamp, in.uv0) : float4(1.0);
    // Unity Linear workflow converts sRGB albedo texture & _Color cleanly to Linear space
    float3 texLin = srgbToLinear(tex.rgb);
    float3 baseLin = srgbToLinear(u.baseColor.rgb);
    float3 vcolLin = srgbToLinear(in.color.rgb);
    float alpha = tex.a * u.baseColor.a * in.color.a;

    if (u.flags.z > 0.5 && alpha < u.cameraPosCutoff.w)
    {
        discard_fragment();
    }

    float3 albedoLin = texLin * baseLin * vcolLin;
    float3 N = normalize(u.ambientColor.w > 0.5 ? in.flatNormal : in.worldNormal);
    float3 V = normalize(u.cameraPosCutoff.xyz - in.worldPos);

    float metallic = clamp(u.pbrParams.x, 0.0, 1.0);
    float smoothness = clamp(u.pbrParams.y, 0.0, 1.0);
    bool isStd = u.pbrParams.z > 0.5;
    float oneMinusDielectric = 1.0 - metallic * 0.96;
    float3 diffAlbedo = isStd ? (albedoLin * oneMinusDielectric) : albedoLin;

    float3 lit = float3(0.0);

    if (u.flags.y > 0.5)
    {
        float3 lmSrgb = lmTex.sample(lmSamp, in.lmUv).rgb;
        float3 lmLin = srgbToLinear(lmSrgb);
        if (u.pbrParams.w > 0.5)
        {
            float4 dirTex = dirLmTex.sample(dirLmSamp, in.lmUv);
            float halfLambert = max(0.0, dot(N, dirTex.xyz - 0.5) + 0.5);
            lmLin = lmLin * (halfLambert / max(0.05, dirTex.w));
        }
        lit += diffAlbedo * lmLin;
    }
    else
    {
        float3 sh = (u.shC.w > 0.5) ? shadeSH9(N, u) : (srgbToLinear(u.ambientColor.rgb) * 0.65);
        lit += diffAlbedo * sh;
    }

    // Unity 2021.1 BRDF1_Unity_PBS indirect specular (runs for all Standard materials including smoothness=0)
    if (isStd)
    {
        float ndv = saturate(dot(N, V));
        float perceptualRoughness = 1.0 - smoothness;
        float roughness = max(perceptualRoughness * perceptualRoughness, 0.002);
        float surfaceReduction = 1.0 / (roughness * roughness + 1.0);
        float3 f0 = mix(float3(0.04), albedoLin, metallic);
        float grazingTerm = saturate(smoothness + (1.0 - oneMinusDielectric));
        float fresnel = pow(1.0 - ndv, 5.0);
        float3 R = reflect(-V, N);
        float3 envDirCol = (u.shC.w > 0.5) ? shadeSH9(R, u) : float3(0.11, 0.12, 0.11);
        lit += envDirCol * surfaceReduction * mix(f0, float3(grazingTerm), fresnel);
    }

    int count = clamp(int(u.flags.w + 0.5), 0, 4);
    for (int i = 0; i < count; i++)
    {
        LightData L = u.lights[i];
        int ltype = int(L.posType.w + 0.5);
        float3 lightCol = srgbToLinear(L.colorIntensity.rgb) * L.colorIntensity.a;

        if (ltype == 1) // Directional
        {
            float3 toLight = normalize(-L.dirRange.xyz);
            float ndl = max(0.0, dot(N, toLight));
            lit += diffAlbedo * lightCol * ndl;
        }
        else
        {
            float3 delta = L.posType.xyz - in.worldPos;
            float dist = length(delta);
            float range = max(0.01, L.dirRange.w);
            if (dist < range)
            {
                float3 toLight = delta / max(dist, 1e-4);
                float ndl = max(0.0, dot(N, toLight));
                float ratio = dist / range;
                float atten = 1.0 / (1.0 + 25.0 * ratio * ratio);
                atten *= saturate(1.0 - ratio * ratio);

                if (ltype == 0) // Spot
                {
                    float cosAng = dot(-toLight, normalize(L.dirRange.xyz));
                    float spot = smoothstep(L.spotParams.x, L.spotParams.y, cosAng);
                    atten *= spot;
                }
                lit += diffAlbedo * lightCol * (ndl * atten);
            }
        }
    }

    lit += srgbToLinear(u.emissionColor.rgb);

    if (u.fogParams.w > 0.0)
    {
        float dist = length(in.worldPos - u.cameraPosCutoff.xyz);
        float fogFactor = exp(-pow(dist * u.fogParams.w, 2.0));
        float3 fogLin = srgbToLinear(u.fogParams.rgb);
        lit = mix(fogLin, lit, saturate(fogFactor));
    }

    return float4(linearToSrgb(lit), alpha);
}
