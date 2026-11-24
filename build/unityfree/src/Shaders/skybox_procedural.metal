#include <metal_stdlib>
using namespace metal;

struct SkyboxVertOut
{
    float4 clipPos [[position]];
    float2 ndc;
};

struct SkyboxUniforms
{
    float4x4 invViewProj;
    float4 skyTop;
    float4 skyHorizon;
    float4 groundColor;
    float4 sunDir;
};

vertex SkyboxVertOut vs_skybox(uint vid [[vertex_id]])
{
    float2 pos = float2((vid == 1) ? 3.0 : -1.0, (vid == 2) ? 3.0 : -1.0);
    SkyboxVertOut out;
    out.clipPos = float4(pos, 0.9999, 1.0);
    out.ndc = pos;
    return out;
}

fragment float4 fs_skybox(
    SkyboxVertOut in [[stage_in]],
    constant SkyboxUniforms& u [[buffer(0)]])
{
    float4 farClip = u.invViewProj * float4(in.ndc, 1.0, 1.0);
    float3 dir = normalize(farClip.xyz / max(abs(farClip.w), 1e-5));
    float y = dir.y;
    float3 col;
    if (y >= 0.0)
    {
        col = mix(u.skyHorizon.rgb, u.skyTop.rgb, pow(saturate(y), 0.55));
    }
    else
    {
        col = mix(u.skyHorizon.rgb, u.groundColor.rgb, saturate(-y * 3.0));
    }
    float sun = pow(max(0.0, dot(dir, normalize(-u.sunDir.xyz))), 64.0);
    col += float3(1.0, 0.92, 0.78) * sun * 0.4;
    return float4(col, 1.0);
}
