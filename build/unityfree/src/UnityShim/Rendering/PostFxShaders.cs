using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.Rendering
{
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    public unsafe struct PostFxUniforms
    {
        public fixed float texelSize[4];
        public fixed float p0[4];
        public fixed float p1[4];
        public fixed float p2[4];
        public fixed float p3[4];
        public fixed float p4[4];
        public fixed float p5[4];
        public fixed float p6[4];
        public fixed float p7[4];
        public fixed float c0[4];
        public fixed float c1[4];
        public fixed float c2[4];
        public fixed float c3[4];
        public fixed float c4[4];
        public fixed float c5[4];
        public fixed float timeVec[4];
    }

    public unsafe sealed class PostFxShaders : IDisposable
    {
        private readonly SDL_GPUDevice* _device;
        private readonly string _combinedMsl;
        private SDL_GPUShader* _vsFullscreen;
        private readonly Dictionary<string, nint> _pipelines = new(StringComparer.OrdinalIgnoreCase);

        public PostFxShaders(SDL_GPUDevice* device, string shaderDir)
        {
            _device = device;
            string common = File.ReadAllText(Path.Combine(shaderDir, "postfx_common.metal")).Replace("#pragma once", "");
            string effects = File.ReadAllText(Path.Combine(shaderDir, "postfx_effects.metal")).Replace("#include \"postfx_common.metal\"", "");
            _combinedMsl = common + "\n" + effects;
            _vsFullscreen = CompileShader("vs_fullscreen", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 0);
        }

        private SDL_GPUShader* CompileShader(string entry, SDL_GPUShaderStage stage, uint samplers, uint ubos)
        {
            byte[] srcRaw = Encoding.UTF8.GetBytes(_combinedMsl + "\0");
            byte[] ent = Encoding.UTF8.GetBytes(entry + "\0");
            fixed (byte* pSrc = srcRaw)
            fixed (byte* pEnt = ent)
            {
                var info = new SDL_GPUShaderCreateInfo
                {
                    code = pSrc,
                    code_size = (nuint)(srcRaw.Length - 1),
                    entrypoint = pEnt,
                    format = (SDL_GPUShaderFormat)SDL3.SDL_GPU_SHADERFORMAT_MSL,
                    stage = stage,
                    num_samplers = samplers,
                    num_uniform_buffers = ubos
                };
                SDL_GPUShader* sh = SDL3.SDL_CreateGPUShader(_device, &info);
                if (sh == null)
                {
                    throw new InvalidOperationException($"SDL_CreateGPUShader failed for {entry}: {SdlNative.GetErrorString()}");
                }
                return sh;
            }
        }

        public SDL_GPUGraphicsPipeline* GetPipeline(string shaderName, int pass, SDL_GPUTextureFormat targetFormat)
        {
            string entry = ResolveFragmentEntry(shaderName, pass);
            string key = $"{entry}:{(int)targetFormat}";
            if (_pipelines.TryGetValue(key, out nint existing)) return (SDL_GPUGraphicsPipeline*)existing;

            SDL_GPUShader* fs = CompileShader(entry, SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 4, 1);
            var colorDesc = new SDL_GPUColorTargetDescription { format = targetFormat };
            var pinfo = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = _vsFullscreen,
                fragment_shader = fs,
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState
                {
                    fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL,
                    cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE,
                    front_face = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE
                },
                multisample_state = new SDL_GPUMultisampleState { sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1 },
                depth_stencil_state = new SDL_GPUDepthStencilState { compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_ALWAYS, enable_depth_test = false, enable_depth_write = false },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo
                {
                    color_target_descriptions = &colorDesc,
                    num_color_targets = 1,
                    has_depth_stencil_target = false
                }
            };
            SDL_GPUGraphicsPipeline* pipe = SDL3.SDL_CreateGPUGraphicsPipeline(_device, &pinfo);
            SDL3.SDL_ReleaseGPUShader(_device, fs);
            if (pipe == null)
            {
                throw new InvalidOperationException($"PostFx pipeline {entry} failed: {SdlNative.GetErrorString()}");
            }
            _pipelines[key] = (nint)pipe;
            return pipe;
        }

        private static string ResolveFragmentEntry(string shaderName, int pass)
        {
            string k = (shaderName ?? "").ToLowerInvariant();
            if (k.Contains("bloom"))
            {
                return pass switch { 0 => "fs_bloom_prefilter", 1 => "fs_bloom_down", 2 => "fs_bloom_up", 3 => "fs_bloom_combine", _ => "fs_blit" };
            }
            if (k.Contains("kuwahara")) return "fs_kuwahara";
            if (k.Contains("pixelart")) return "fs_pixelart";
            if (k.Contains("dog") || k.Contains("gaussian")) return "fs_dog";
            if (k.Contains("blur")) return "fs_blur";
            if (k.Contains("vignette")) return "fs_vignette";
            if (k.Contains("chromatic")) return "fs_chromatic";
            if (k.Contains("colorcorrection")) return "fs_color_correction";
            if (k.Contains("dither")) return "fs_dither";
            if (k.Contains("edge")) return "fs_edge";
            if (k.Contains("fog")) return "fs_fog";
            if (k.Contains("gamma")) return "fs_gamma";
            if (k.Contains("hue")) return "fs_hue_shift";
            if (k.Contains("sharp")) return "fs_sharpness";
            if (k.Contains("tonemap")) return "fs_tonemapper";
            if (k.Contains("zoom")) return "fs_zoom";
            return "fs_blit";
        }

        public static PostFxUniforms PackUniforms(
            string shaderName, int pass, int srcW, int srcH,
            IReadOnlyDictionary<string, float> flts,
            IReadOnlyDictionary<string, int> ints,
            IReadOnlyDictionary<string, float[]> vecs)
        {
            PostFxUniforms u = default;
            u.texelSize[0] = 1f / Math.Max(1, srcW);
            u.texelSize[1] = 1f / Math.Max(1, srcH);
            u.texelSize[2] = srcW;
            u.texelSize[3] = srcH;
            u.timeVec[0] = Time.time;
            u.timeVec[1] = Time.deltaTime;
            u.timeVec[2] = Math.Max(0, pass);
            u.timeVec[3] = Time.frameCount;

            float F(string n, float d = 0f) => (flts != null && flts.TryGetValue(n, out var v)) ? v : (ints != null && ints.TryGetValue(n, out var iv) ? iv : d);
            void CopyVec(float* dst, string n, float d0 = 1f, float d1 = 1f, float d2 = 1f, float d3 = 1f)
            {
                if (vecs != null && vecs.TryGetValue(n, out var a) && a.Length >= 4)
                {
                    dst[0] = a[0]; dst[1] = a[1]; dst[2] = a[2]; dst[3] = a[3];
                }
                else
                {
                    dst[0] = d0; dst[1] = d1; dst[2] = d2; dst[3] = d3;
                }
            }

            string k = (shaderName ?? "").ToLowerInvariant();
            if (k.Contains("bloom"))
            {
                u.p0[0] = F("_Threshold", 1f);
                u.p0[1] = F("_SoftThreshold", 0.5f);
                u.p0[2] = F("_DownDelta", 1f);
                u.p0[3] = F("_UpDelta", 0.5f);
                u.p1[0] = F("_Intensity", 1f);
            }
            else if (k.Contains("kuwahara"))
            {
                u.p0[0] = F("_KernelSize", 3f);
                u.p0[1] = F("_MinKernelSize", 1f);
                u.p0[2] = F("_AnimateSize", 0f);
                u.p0[3] = F("_SizeAnimationSpeed", 1f);
            }
            else if (k.Contains("dog") || k.Contains("gaussian"))
            {
                u.p0[0] = F("_Sigma", F("_SigmaC", 2f));
                u.p0[1] = F("_K", 1.6f);
                u.p0[2] = F("_Tau", 1f);
                u.p0[3] = F("_Phi", 5f);
                u.p1[0] = F("_Threshold", 0.005f);
                u.p1[1] = F("_Invert", 0f);
            }
            else if (k.Contains("blur"))
            {
                u.p0[0] = F("_KernelSize", 3f);
                u.p0[1] = F("_Sigma", 2f);
            }
            else if (k.Contains("vignette"))
            {
                if (vecs != null && vecs.TryGetValue("_VignetteSize", out var vsz) && vsz.Length >= 2)
                {
                    u.p0[0] = vsz[0]; u.p0[1] = vsz[1];
                }
                else
                {
                    u.p0[0] = 1f; u.p0[1] = 1f;
                }
                if (vecs != null && vecs.TryGetValue("_VignetteOffset", out var vof) && vof.Length >= 2)
                {
                    u.p0[2] = vof[0]; u.p0[3] = vof[1];
                }
                CopyVec(u.c0, "_VignetteColor", 0, 0, 0, 1);
                u.p0[1] = F("_Smoothness", 0.5f);
                u.p1[0] = F("_Roundness", 2f);
                u.p1[1] = F("_Intensity", 1f);
            }
            else if (k.Contains("chromatic"))
            {
                u.p0[0] = F("_Intensity", 1f);
                u.p0[1] = F("_Radius", 0.3f);
                u.p0[2] = F("_Hardness", 0.5f);
                CopyVec(u.c0, "_ColorOffsets", 1, 0, -1, 0);
            }
            else if (k.Contains("colorcorrection"))
            {
                u.p0[0] = F("_Exposure", 0f);
                u.p0[1] = F("_Brightness", 0f);
                u.p0[2] = F("_MidPoint", 0.5f);
                u.p0[3] = F("_Contrast", 1f);
                u.p1[0] = F("_Saturation", 1f);
                CopyVec(u.c0, "_ColorFilter", 1, 1, 1, 1);
            }
            else if (k.Contains("dither"))
            {
                u.p0[0] = F("_Spread", 0.5f);
                u.p0[1] = F("_RedColorCount", 8f);
                u.p0[2] = F("_GreenColorCount", 8f);
                u.p0[3] = F("_BlueColorCount", 8f);
            }
            else if (k.Contains("edge"))
            {
                CopyVec(u.c0, "_BorderColor", 0, 0, 0, 1);
            }
            else if (k.Contains("gamma"))
            {
                u.p0[0] = F("_Gamma", 1f);
            }
            else if (k.Contains("hue"))
            {
                u.p0[0] = F("_HueShift", 0f);
            }
            else if (k.Contains("sharp"))
            {
                u.p0[0] = F("_Amount", 1f);
            }
            else if (k.Contains("zoom"))
            {
                u.p0[0] = F("_Zoom", 0.2f);
            }
            else if (k.Contains("fog"))
            {
                u.p0[0] = F("_FogDensity", 0.25f);
                CopyVec(u.c0, "_FogColor", 0.5f, 0.5f, 0.5f, 1f);
            }
            return u;
        }

        public void Dispose()
        {
            foreach (nint p in _pipelines.Values) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, (SDL_GPUGraphicsPipeline*)p);
            _pipelines.Clear();
            if (_vsFullscreen != null) SDL3.SDL_ReleaseGPUShader(_device, _vsFullscreen);
        }
    }
}
