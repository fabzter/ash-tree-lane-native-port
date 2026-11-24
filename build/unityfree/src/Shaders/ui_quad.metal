#include <metal_stdlib>
using namespace metal;

struct UiVertIn
{
    float2 posPx [[attribute(0)]];
    float2 uv    [[attribute(1)]];
    float4 color [[attribute(2)]];
};

struct UiVertOut
{
    float4 clipPos [[position]];
    float2 uv;
    float4 color;
};

struct UiUniforms
{
    float4 screenSize; // x=width, y=height
};

vertex UiVertOut vs_ui(
    UiVertIn in [[stage_in]],
    constant UiUniforms& u [[buffer(0)]])
{
    UiVertOut out;
    float2 ndc = float2(
        (in.posPx.x / max(1.0, u.screenSize.x)) * 2.0 - 1.0,
        1.0 - (in.posPx.y / max(1.0, u.screenSize.y)) * 2.0);
    out.clipPos = float4(ndc, 0.0, 1.0);
    out.uv = in.uv;
    out.color = in.color;
    return out;
}

fragment float4 fs_ui(
    UiVertOut in [[stage_in]],
    texture2d<float> uiTex [[texture(0)]],
    sampler uiSamp [[sampler(0)]])
{
    return uiTex.sample(uiSamp, in.uv) * in.color;
}
