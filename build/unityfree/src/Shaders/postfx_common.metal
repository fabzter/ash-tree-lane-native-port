#pragma once
#include <metal_stdlib>
using namespace metal;

struct PostFxVertOut
{
    float4 clipPos [[position]];
    float2 uv;
};

struct PostFxUniforms
{
    float4 texelSize; // 1/w, 1/h, w, h
    float4 p0;
    float4 p1;
    float4 p2;
    float4 p3;
    float4 p4;
    float4 p5;
    float4 p6;
    float4 p7;
    float4 c0;
    float4 c1;
    float4 c2;
    float4 c3;
    float4 c4;
    float4 c5;
    float4 timeVec;   // x = Time.time, y = deltaTime, z = passIndex, w = frameCount
};

inline float fx_luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

vertex PostFxVertOut vs_fullscreen(uint vid [[vertex_id]])
{
    float2 pos = float2((vid == 1) ? 3.0 : -1.0, (vid == 2) ? 3.0 : -1.0);
    PostFxVertOut out;
    out.clipPos = float4(pos, 0.0, 1.0);
    out.uv = float2((pos.x + 1.0) * 0.5, 1.0 - (pos.y + 1.0) * 0.5);
    return out;
}
