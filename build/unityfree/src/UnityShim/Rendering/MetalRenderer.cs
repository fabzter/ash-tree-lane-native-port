using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using UnityEngine;
using UnityEngine.UI;
using UnityShim.Backend;

namespace UnityShim.Rendering
{
    [StructLayout(LayoutKind.Sequential, Size = 320)]
    internal unsafe struct StdVertUniforms
    {
        public fixed float mvp[16];
        public fixed float model[16];
        public fixed float mainTexST[4];
        public fixed float lightmapST[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct StdLightData
    {
        public fixed float posType[4];
        public fixed float dirRange[4];
        public fixed float colorIntensity[4];
        public fixed float spotParams[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct StdFragUniforms
    {
        public fixed float baseColor[4];
        public fixed float emissionColor[4];
        public fixed float ambientColor[4];
        public fixed float cameraPosCutoff[4];
        public fixed float flags[4];
        public fixed float fogParams[4];
        public fixed float pbrParams[4];
        public fixed float shAr[4];
        public fixed float shAg[4];
        public fixed float shAb[4];
        public fixed float shBr[4];
        public fixed float shBg[4];
        public fixed float shBb[4];
        public fixed float shC[4];
        public StdLightData l0, l1, l2, l3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SkyboxUniforms
    {
        public fixed float invViewProj[16];
        public fixed float skyTop[4];
        public fixed float skyHorizon[4];
        public fixed float groundColor[4];
        public fixed float sunDir[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UiVertex
    {
        public float X, Y, U, V, R, G, B, A;
    }

    internal unsafe struct GpuMeshRecord
    {
        public SDL_GPUBuffer* Vbo;
        public SDL_GPUBuffer* Ibo;
        public int IndexCount;
        public uint[] SubOffsets;
        public uint[] SubCounts;
    }

    internal unsafe struct GpuTexRecord
    {
        public SDL_GPUTexture* Tex;
        public int Width;
        public int Height;
        public bool Clamp;
        public bool Point;
    }

    public sealed class OverlayDrawContext
    {
        private readonly MetalRenderer _renderer;
        private readonly int _whiteTexId;

        public int Width { get; }
        public int Height { get; }
        public float UiScale { get; }

        internal OverlayDrawContext(MetalRenderer renderer, int width, int height, float uiScale, int whiteTexId)
        {
            _renderer = renderer;
            Width = width;
            Height = height;
            UiScale = uiScale;
            _whiteTexId = whiteTexId;
        }

        public void DrawRect(float x, float y, float w, float h, Color color)
            => _renderer.EmitQuad(x, y, x + w, y + h, 0f, 0f, 1f, 1f, color, _whiteTexId);

        public void DrawRectOutline(float x, float y, float w, float h, float t, Color color)
        {
            DrawRect(x, y, w, t, color);
            DrawRect(x, y + h - t, w, t, color);
            DrawRect(x, y + t, t, MathF.Max(0f, h - 2f * t), color);
            DrawRect(x + w - t, y + t, t, MathF.Max(0f, h - 2f * t), color);
        }

        public void DrawText(string text, float x, float y, float w, float h, int fontSize, Color color, bool centered = true)
            => _renderer.EmitTextString(text, fontSize, color, x, y, w, h, UiScale, centered);
    }

    public unsafe sealed class MetalRenderer : IRenderBackend, IDisposable
    {
        private SDL_Window* _window;
        private readonly SDL_GPUDevice* _device;
        private readonly string _shaderDir;

        public SDL_Window* Window => _window;
        public Action<OverlayDrawContext> OverlayRenderer { get; set; }

        private SDL_GPUSampler* _sampLinearWrap;
        private SDL_GPUSampler* _sampLinearClamp;
        private SDL_GPUSampler* _sampPointClamp;
        private SDL_GPUSampler* _sampPointWrap;

        private SDL_GPUGraphicsPipeline* _pipeOpaqueBack;
        private SDL_GPUGraphicsPipeline* _pipeOpaqueDouble;
        private SDL_GPUGraphicsPipeline* _pipeAlphaBlend;
        private SDL_GPUGraphicsPipeline* _pipeSkybox;
        private SDL_GPUGraphicsPipeline* _pipeUi;
        private PostFxShaders _postFx;

        private int _nextId = 1;
        private readonly Dictionary<int, GpuTexRecord> _textures = new();
        private readonly Dictionary<int, GpuMeshRecord> _meshes = new();

        private RenderTexture _sceneRt;
        private RenderTexture _finalRt;
        private SDL_GPUTexture* _depthTex;
        private int _rtW, _rtH;

        private SDL_GPUBuffer* _uiVbo;
        private int _uiVboCapacityBytes;
        private readonly List<UiVertex> _uiVerts = new(1024);
        private readonly List<(int startVert, int vertCount, int texId)> _uiBatches = new(64);

        private SDL_GPUCommandBuffer* _activeCmd;
        private string _pendingScreenshot;
        public bool TraceRender { get; set; }
        public bool DisablePostFx { get; set; }
        public bool NullRender { get; set; }

        public MetalRenderer(SDL_Window* window, string shaderDir, bool debugGpu = false)
        {
            _window = window;
            _shaderDir = shaderDir;

            byte[] metalStr = Encoding.ASCII.GetBytes("metal\0");
            fixed (byte* pName = metalStr)
            {
                _device = SDL3.SDL_CreateGPUDevice((SDL_GPUShaderFormat)SDL3.SDL_GPU_SHADERFORMAT_MSL, debugGpu, pName);
                if (_device == null) _device = SDL3.SDL_CreateGPUDevice((SDL_GPUShaderFormat)SDL3.SDL_GPU_SHADERFORMAT_MSL, debugGpu, (byte*)null);
            }
            if (_device == null || !SDL3.SDL_ClaimWindowForGPUDevice(_device, _window))
            {
                throw new InvalidOperationException($"SDL_GPU Metal init failed: {SdlNative.GetErrorString()}");
            }

            CreateSamplers();
            CreateCorePipelines();
            _postFx = new PostFxShaders(_device, shaderDir);
        }

        private void CreateSamplers()
        {
            var lw = new SDL_GPUSamplerCreateInfo
            {
                min_filter = SDL_GPUFilter.SDL_GPU_FILTER_LINEAR,
                mag_filter = SDL_GPUFilter.SDL_GPU_FILTER_LINEAR,
                mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_LINEAR,
                address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
                address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
                address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
                max_lod = 1000f,
                max_anisotropy = 8f,
                enable_anisotropy = true
            };
            _sampLinearWrap = SDL3.SDL_CreateGPUSampler(_device, &lw);

            var lc = lw;
            lc.address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
            lc.address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
            lc.address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
            _sampLinearClamp = SDL3.SDL_CreateGPUSampler(_device, &lc);

            var pc = lc;
            pc.min_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST;
            pc.mag_filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST;
            pc.mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST;
            pc.max_lod = 0f;
            pc.enable_anisotropy = false;
            _sampPointClamp = SDL3.SDL_CreateGPUSampler(_device, &pc);

            var pw = pc;
            pw.address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
            pw.address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
            pw.address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
            _sampPointWrap = SDL3.SDL_CreateGPUSampler(_device, &pw);
        }

        private SDL_GPUShader* CompileMslFile(string fileName, string entry, SDL_GPUShaderStage stage, uint samplers, uint ubos)
        {
            string msl = File.ReadAllText(Path.Combine(_shaderDir, fileName));
            byte[] srcRaw = Encoding.UTF8.GetBytes(msl + "\0");
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
                if (sh == null) throw new InvalidOperationException($"Shader {fileName}:{entry} failed: {SdlNative.GetErrorString()}");
                return sh;
            }
        }

        private SDL_GPUGraphicsPipeline* MustCreatePipeline(string label, SDL_GPUGraphicsPipelineCreateInfo* pinfo)
        {
            SDL_GPUGraphicsPipeline* p = SDL3.SDL_CreateGPUGraphicsPipeline(_device, pinfo);
            if (p == null) throw new InvalidOperationException($"Pipeline {label} failed: {SdlNative.GetErrorString()}");
            return p;
        }

        private void CreateCorePipelines()
        {
            SDL_GPUShader* vsStd = CompileMslFile("standard_3d.metal", "vs_standard", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
            SDL_GPUShader* fsStd = CompileMslFile("standard_3d.metal", "fs_standard", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 3, 1);

            var vbufDesc = new SDL_GPUVertexBufferDescription { slot = 0, pitch = 56, input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX };
            SDL_GPUVertexAttribute* attrs = stackalloc SDL_GPUVertexAttribute[5];
            attrs[0] = new SDL_GPUVertexAttribute { location = 0, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, offset = 0 };
            attrs[1] = new SDL_GPUVertexAttribute { location = 1, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3, offset = 12 };
            attrs[2] = new SDL_GPUVertexAttribute { location = 2, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 24 };
            attrs[3] = new SDL_GPUVertexAttribute { location = 3, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 32 };
            attrs[4] = new SDL_GPUVertexAttribute { location = 4, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, offset = 40 };

            var colOpaque = new SDL_GPUColorTargetDescription
            {
                format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
                blend_state = new SDL_GPUColorTargetBlendState { color_write_mask = (SDL_GPUColorComponentFlags)0xF }
            };
            var pOpaque = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = vsStd,
                fragment_shader = fsStd,
                vertex_input_state = new SDL_GPUVertexInputState
                {
                    vertex_buffer_descriptions = &vbufDesc,
                    num_vertex_buffers = 1,
                    vertex_attributes = attrs,
                    num_vertex_attributes = 5
                },
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState
                {
                    fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL,
                    cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK,
                    front_face = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_CLOCKWISE
                },
                multisample_state = new SDL_GPUMultisampleState { sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1 },
                depth_stencil_state = new SDL_GPUDepthStencilState
                {
                    compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
                    enable_depth_test = true,
                    enable_depth_write = true
                },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo
                {
                    color_target_descriptions = &colOpaque,
                    num_color_targets = 1,
                    depth_stencil_format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
                    has_depth_stencil_target = true
                }
            };
            _pipeOpaqueBack = MustCreatePipeline("opaque_back", &pOpaque);

            pOpaque.rasterizer_state.cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE;
            _pipeOpaqueDouble = MustCreatePipeline("opaque_double", &pOpaque);

            var colBlend = new SDL_GPUColorTargetDescription
            {
                format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
                blend_state = AlphaBlendState()
            };
            pOpaque.target_info.color_target_descriptions = &colBlend;
            pOpaque.depth_stencil_state.enable_depth_write = false;
            _pipeAlphaBlend = MustCreatePipeline("alpha_blend", &pOpaque);

            SDL3.SDL_ReleaseGPUShader(_device, vsStd);
            SDL3.SDL_ReleaseGPUShader(_device, fsStd);

            // Skybox
            SDL_GPUShader* vsSky = CompileMslFile("skybox_procedural.metal", "vs_skybox", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 0);
            SDL_GPUShader* fsSky = CompileMslFile("skybox_procedural.metal", "fs_skybox", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 0, 1);
            var pSky = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = vsSky,
                fragment_shader = fsSky,
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
                multisample_state = new SDL_GPUMultisampleState { sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1 },
                depth_stencil_state = new SDL_GPUDepthStencilState { compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_ALWAYS, enable_depth_test = false, enable_depth_write = false },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo
                {
                    color_target_descriptions = &colOpaque,
                    num_color_targets = 1,
                    depth_stencil_format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
                    has_depth_stencil_target = true
                }
            };
            _pipeSkybox = MustCreatePipeline("skybox", &pSky);
            SDL3.SDL_ReleaseGPUShader(_device, vsSky);
            SDL3.SDL_ReleaseGPUShader(_device, fsSky);

            // UI Quad
            SDL_GPUShader* vsUi = CompileMslFile("ui_quad.metal", "vs_ui", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
            SDL_GPUShader* fsUi = CompileMslFile("ui_quad.metal", "fs_ui", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0);
            var uiVbuf = new SDL_GPUVertexBufferDescription { slot = 0, pitch = (uint)sizeof(UiVertex), input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX };
            SDL_GPUVertexAttribute* uiAttrs = stackalloc SDL_GPUVertexAttribute[3];
            uiAttrs[0] = new SDL_GPUVertexAttribute { location = 0, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 0 };
            uiAttrs[1] = new SDL_GPUVertexAttribute { location = 1, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2, offset = 8 };
            uiAttrs[2] = new SDL_GPUVertexAttribute { location = 2, buffer_slot = 0, format = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4, offset = 16 };
            var pUi = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = vsUi,
                fragment_shader = fsUi,
                vertex_input_state = new SDL_GPUVertexInputState
                {
                    vertex_buffer_descriptions = &uiVbuf,
                    num_vertex_buffers = 1,
                    vertex_attributes = uiAttrs,
                    num_vertex_attributes = 3
                },
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState { fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL, cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
                multisample_state = new SDL_GPUMultisampleState { sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1 },
                depth_stencil_state = new SDL_GPUDepthStencilState { compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_ALWAYS, enable_depth_test = false, enable_depth_write = false },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo { color_target_descriptions = &colBlend, num_color_targets = 1, has_depth_stencil_target = false }
            };
            _pipeUi = MustCreatePipeline("ui_quad", &pUi);
            SDL3.SDL_ReleaseGPUShader(_device, vsUi);
            SDL3.SDL_ReleaseGPUShader(_device, fsUi);
        }

        private static SDL_GPUColorTargetBlendState AlphaBlendState() => new()
        {
            enable_blend = true,
            src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
            dst_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
            src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
            dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
            alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
            color_write_mask = (SDL_GPUColorComponentFlags)0xF
        };

        public int UploadTextureRgba(string name, int width, int height, byte[] rgba, bool linear, bool clamp, bool pointFilter, bool generateMipmaps = false)
        {
            uint levels = (generateMipmaps && width >= 4 && height >= 4)
                ? (uint)(1 + (int)MathF.Floor(MathF.Log2(MathF.Max(width, height))))
                : 1u;
            var usage = (SDL_GPUTextureUsageFlags)SDL3.SDL_GPU_TEXTUREUSAGE_SAMPLER;
            if (levels > 1) usage |= (SDL_GPUTextureUsageFlags)SDL3.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET;

            var tinfo = new SDL_GPUTextureCreateInfo
            {
                type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
                format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
                usage = usage,
                width = (uint)Math.Max(1, width),
                height = (uint)Math.Max(1, height),
                layer_count_or_depth = 1,
                num_levels = levels,
                sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1
            };
            SDL_GPUTexture* tex = SDL3.SDL_CreateGPUTexture(_device, &tinfo);
            var tbInfo = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)rgba.Length };
            SDL_GPUTransferBuffer* tb = SDL3.SDL_CreateGPUTransferBuffer(_device, &tbInfo);
            void* mapped = (void*)SDL3.SDL_MapGPUTransferBuffer(_device, tb, false);
            Marshal.Copy(rgba, 0, (nint)mapped, rgba.Length);
            SDL3.SDL_UnmapGPUTransferBuffer(_device, tb);

            SDL_GPUCommandBuffer* cmd = SDL3.SDL_AcquireGPUCommandBuffer(_device);
            SDL_GPUCopyPass* cp = SDL3.SDL_BeginGPUCopyPass(cmd);
            var src = new SDL_GPUTextureTransferInfo { transfer_buffer = tb, pixels_per_row = (uint)width, rows_per_layer = (uint)height };
            var dst = new SDL_GPUTextureRegion { texture = tex, w = (uint)width, h = (uint)height, d = 1 };
            SDL3.SDL_UploadToGPUTexture(cp, &src, &dst, false);
            SDL3.SDL_EndGPUCopyPass(cp);
            if (levels > 1) SDL3.SDL_GenerateMipmapsForGPUTexture(cmd, tex);
            SDL3.SDL_SubmitGPUCommandBuffer(cmd);
            SDL3.SDL_ReleaseGPUTransferBuffer(_device, tb);

            int id = _nextId++;
            _textures[id] = new GpuTexRecord { Tex = tex, Width = width, Height = height, Clamp = clamp, Point = pointFilter };
            return id;
        }

        public void ReleaseTexture(int texId)
        {
            if (_textures.Remove(texId, out var rec) && rec.Tex != null) SDL3.SDL_ReleaseGPUTexture(_device, rec.Tex);
        }

        public int CreateRenderTarget(int width, int height, int format, bool linear)
        {
            var tinfo = new SDL_GPUTextureCreateInfo
            {
                type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
                format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
                usage = (SDL_GPUTextureUsageFlags)(SDL3.SDL_GPU_TEXTUREUSAGE_SAMPLER | SDL3.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET),
                width = (uint)Math.Max(1, width),
                height = (uint)Math.Max(1, height),
                layer_count_or_depth = 1,
                num_levels = 1,
                sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1
            };
            SDL_GPUTexture* tex = SDL3.SDL_CreateGPUTexture(_device, &tinfo);
            int id = _nextId++;
            _textures[id] = new GpuTexRecord { Tex = tex, Width = width, Height = height, Clamp = true };
            return id;
        }

        public void ReleaseRenderTarget(int rtId) => ReleaseTexture(rtId);

        public int UploadMesh(string name, float[] interleaved56, int vertexCount, uint[] indices, uint[] submeshOffsets, uint[] submeshCounts)
        {
            uint vBytes = (uint)(interleaved56.Length * sizeof(float));
            uint iBytes = (uint)(indices.Length * sizeof(uint));
            var vboInfo = new SDL_GPUBufferCreateInfo { usage = (SDL_GPUBufferUsageFlags)SDL3.SDL_GPU_BUFFERUSAGE_VERTEX, size = Math.Max(4u, vBytes) };
            var iboInfo = new SDL_GPUBufferCreateInfo { usage = (SDL_GPUBufferUsageFlags)SDL3.SDL_GPU_BUFFERUSAGE_INDEX, size = Math.Max(4u, iBytes) };
            SDL_GPUBuffer* vbo = SDL3.SDL_CreateGPUBuffer(_device, &vboInfo);
            SDL_GPUBuffer* ibo = SDL3.SDL_CreateGPUBuffer(_device, &iboInfo);

            var tbInfo = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = vBytes + iBytes };
            SDL_GPUTransferBuffer* tb = SDL3.SDL_CreateGPUTransferBuffer(_device, &tbInfo);
            byte* mapped = (byte*)SDL3.SDL_MapGPUTransferBuffer(_device, tb, false);
            fixed (float* pv = interleaved56) Buffer.MemoryCopy(pv, mapped, vBytes, vBytes);
            fixed (uint* pi = indices) Buffer.MemoryCopy(pi, mapped + vBytes, iBytes, iBytes);
            SDL3.SDL_UnmapGPUTransferBuffer(_device, tb);

            SDL_GPUCommandBuffer* cmd = SDL3.SDL_AcquireGPUCommandBuffer(_device);
            SDL_GPUCopyPass* cp = SDL3.SDL_BeginGPUCopyPass(cmd);
            var s0 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb, offset = 0 };
            var d0 = new SDL_GPUBufferRegion { buffer = vbo, offset = 0, size = vBytes };
            SDL3.SDL_UploadToGPUBuffer(cp, &s0, &d0, false);
            var s1 = new SDL_GPUTransferBufferLocation { transfer_buffer = tb, offset = vBytes };
            var d1 = new SDL_GPUBufferRegion { buffer = ibo, offset = 0, size = iBytes };
            SDL3.SDL_UploadToGPUBuffer(cp, &s1, &d1, false);
            SDL3.SDL_EndGPUCopyPass(cp);
            SDL3.SDL_SubmitGPUCommandBuffer(cmd);
            SDL3.SDL_ReleaseGPUTransferBuffer(_device, tb);

            int id = _nextId++;
            _meshes[id] = new GpuMeshRecord { Vbo = vbo, Ibo = ibo, IndexCount = indices.Length, SubOffsets = submeshOffsets, SubCounts = submeshCounts };
            return id;
        }

        public void ReleaseMesh(int meshId)
        {
            if (_meshes.Remove(meshId, out var rec))
            {
                if (rec.Vbo != null) SDL3.SDL_ReleaseGPUBuffer(_device, rec.Vbo);
                if (rec.Ibo != null) SDL3.SDL_ReleaseGPUBuffer(_device, rec.Ibo);
            }
        }

        private void EnsureScreenTargets(int w, int h)
        {
            if (_rtW == w && _rtH == h && _sceneRt != null && _finalRt != null) return;
            _rtW = w; _rtH = h;
            RenderTexture.ClearPool();
            if (_sceneRt != null) ReleaseRenderTarget(_sceneRt.GpuTextureId);
            if (_finalRt != null) ReleaseRenderTarget(_finalRt.GpuTextureId);
            if (_depthTex != null) SDL3.SDL_ReleaseGPUTexture(_device, _depthTex);

            _sceneRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _finalRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            var dinfo = new SDL_GPUTextureCreateInfo
            {
                type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
                format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
                usage = (SDL_GPUTextureUsageFlags)SDL3.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
                width = (uint)w,
                height = (uint)h,
                layer_count_or_depth = 1,
                num_levels = 1,
                sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1
            };
            _depthTex = SDL3.SDL_CreateGPUTexture(_device, &dinfo);
        }

        public void RequestScreenshot(string outputPath) => _pendingScreenshot = outputPath;

        public SDL_Window* RecreateWindowAndSwapchain(int width, int height, bool fullscreen, bool restoreCursorLocked)
        {
            width = Math.Max(640, width);
            height = Math.Max(360, height);

            SDL3.SDL_WaitForGPUIdle(_device);

            RenderTexture.ClearPool();
            if (_sceneRt != null) { ReleaseRenderTarget(_sceneRt.GpuTextureId); _sceneRt = null; }
            if (_finalRt != null) { ReleaseRenderTarget(_finalRt.GpuTextureId); _finalRt = null; }
            if (_depthTex != null) { SDL3.SDL_ReleaseGPUTexture(_device, _depthTex); _depthTex = null; }
            _rtW = 0;
            _rtH = 0;

            if (_window != null)
            {
                SDL3.SDL_ReleaseWindowFromGPUDevice(_device, _window);
                SDL3.SDL_DestroyWindow(_window);
                _window = null;
            }

            SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            if (fullscreen) flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;

            byte[] titleUtf8 = Encoding.UTF8.GetBytes("The House On Ash Tree Lane\0");
            SDL_Window* newWin;
            fixed (byte* pTitle = titleUtf8)
            {
                newWin = SDL3.SDL_CreateWindow(pTitle, width, height, flags);
            }
            if (newWin == null)
            {
                throw new InvalidOperationException($"SDL_CreateWindow failed during swapchain recreate: {SdlNative.GetErrorString()}");
            }
            if (!SDL3.SDL_ClaimWindowForGPUDevice(_device, newWin))
            {
                throw new InvalidOperationException($"SDL_ClaimWindowForGPUDevice failed during swapchain recreate: {SdlNative.GetErrorString()}");
            }

            _window = newWin;
            EngineHost.WindowHandle = newWin;

            if (!fullscreen)
            {
                SDL3.SDL_SetWindowPosition(_window, (int)SDL3.SDL_WINDOWPOS_CENTERED, (int)SDL3.SDL_WINDOWPOS_CENTERED);
            }
            SDL3.SDL_SetWindowRelativeMouseMode(_window, restoreCursorLocked);

            int pw = width, ph = height;
            SDL3.SDL_GetWindowSizeInPixels(_window, &pw, &ph);
            Screen.width = Math.Max(1, pw);
            Screen.height = Math.Max(1, ph);

            Console.WriteLine($"[Display] Recreated window/swapchain: {Screen.width}x{Screen.height} (requested {width}x{height}, fullscreen={fullscreen})");
            return _window;
        }

        private void Trace(string step) { if (TraceRender) Console.WriteLine($"[RenderTrace] {step}"); }

        public void RenderFrame()
        {
            if (NullRender) return;
            int winW, winH;
            SDL3.SDL_GetWindowSizeInPixels(_window, &winW, &winH);
            if (winW <= 0 || winH <= 0) return;
            Screen.width = winW;
            Screen.height = winH;
            Trace($"EnsureScreenTargets {winW}x{winH}");
            EnsureScreenTargets(winW, winH);

            // Pre-upload static fallback textures & meshes BEFORE acquiring _activeCmd so no nested command buffer occurs inside a render pass
            _ = Texture2D.WhiteTexture.GpuTextureId;
            _ = Texture2D.BlackTexture.GpuTextureId;
            PreuploadSceneMeshes();

            Trace("AcquireGPUCommandBuffer");
            _activeCmd = SDL3.SDL_AcquireGPUCommandBuffer(_device);
            if (_activeCmd == null) return;

            // 1. Render 3D scene -> _sceneRt
            Trace("Render3DScene");
            Render3DScene(_activeCmd, winW, winH);

            // 2. Post-FX chain on Camera.main
            Camera cam = Camera.main;
            List<MonoBehaviour> fxList = new();
            if (!DisablePostFx && cam != null)
            {
                foreach (var mb in cam.GetComponents<MonoBehaviour>())
                {
                    if (mb.isActiveAndEnabled && mb.InternalHasOnRenderImage) fxList.Add(mb);
                }
            }

            if (fxList.Count > 0)
            {
                Trace($"PostFx count={fxList.Count}");
                RenderTexture cur = _sceneRt;
                for (int i = 0; i < fxList.Count; i++)
                {
                    bool last = i == fxList.Count - 1;
                    RenderTexture dst = last ? _finalRt : RenderTexture.GetTemporary(winW, winH, 0, RenderTextureFormat.ARGB32);
                    fxList[i].InternalOnRenderImage(cur, dst);
                    if (cur != _sceneRt) RenderTexture.ReleaseTemporary(cur);
                    cur = dst;
                }
            }
            else
            {
                Trace("Blit sceneRt -> finalRt");
                Blit(_sceneRt.GpuTextureId, winW, winH, _finalRt.GpuTextureId, winW, winH, "Hidden/Blit", 0, null, null, null, null);
            }

            // 3. Render 2D Canvas UI onto _finalRt
            Trace("RenderCanvasUi");
            RenderCanvasUi(_activeCmd, winW, winH);

            // 4. Present _finalRt -> Swapchain
            Trace("WaitAndAcquireGPUSwapchainTexture");
            SDL_GPUTexture* swapTex = null;
            uint sw = 0, sh = 0;
            SDL3.SDL_WaitAndAcquireGPUSwapchainTexture(_activeCmd, _window, &swapTex, &sw, &sh);
            if (swapTex != null && _textures.TryGetValue(_finalRt.GpuTextureId, out var finalRec))
            {
                Trace($"BlitSwapchain {sw}x{sh}");
                var binfo = new SDL_GPUBlitInfo
                {
                    source = new SDL_GPUBlitRegion { texture = finalRec.Tex, w = (uint)winW, h = (uint)winH },
                    destination = new SDL_GPUBlitRegion { texture = swapTex, w = sw, h = sh },
                    load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
                    filter = SDL_GPUFilter.SDL_GPU_FILTER_LINEAR
                };
                SDL3.SDL_BlitGPUTexture(_activeCmd, &binfo);
            }

            // 5. Optional Screenshot Download or Submit
            if (!string.IsNullOrEmpty(_pendingScreenshot) && _textures.TryGetValue(_finalRt.GpuTextureId, out var capRec))
            {
                Trace($"Screenshot {_pendingScreenshot}");
                string outPath = _pendingScreenshot;
                _pendingScreenshot = null;
                uint bytes = (uint)(winW * winH * 4);
                var tbInfo = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, size = bytes };
                SDL_GPUTransferBuffer* dlBuf = SDL3.SDL_CreateGPUTransferBuffer(_device, &tbInfo);
                SDL_GPUCopyPass* cp = SDL3.SDL_BeginGPUCopyPass(_activeCmd);
                var srcReg = new SDL_GPUTextureRegion { texture = capRec.Tex, w = (uint)winW, h = (uint)winH, d = 1 };
                var dstInfo = new SDL_GPUTextureTransferInfo { transfer_buffer = dlBuf, pixels_per_row = (uint)winW, rows_per_layer = (uint)winH };
                SDL3.SDL_DownloadFromGPUTexture(cp, &srcReg, &dstInfo);
                SDL3.SDL_EndGPUCopyPass(cp);

                SDL_GPUFence* fence = SDL3.SDL_SubmitGPUCommandBufferAndAcquireFence(_activeCmd);
                SDL3.SDL_WaitForGPUFences(_device, true, &fence, 1);
                SDL3.SDL_ReleaseGPUFence(_device, fence);

                void* mapped = (void*)SDL3.SDL_MapGPUTransferBuffer(_device, dlBuf, false);
                byte[] pixels = new byte[bytes];
                Marshal.Copy((nint)mapped, pixels, 0, (int)bytes);
                SDL3.SDL_UnmapGPUTransferBuffer(_device, dlBuf);
                SDL3.SDL_ReleaseGPUTransferBuffer(_device, dlBuf);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                WritePng(outPath, winW, winH, pixels);
            }
            else
            {
                Trace("SubmitGPUCommandBuffer");
                SDL3.SDL_SubmitGPUCommandBuffer(_activeCmd);
            }
            _activeCmd = null;
            Trace("FrameDone");
        }

        private void PreuploadSceneMeshes()
        {
            var renderers = SceneRegistry.GetAllComponents<MeshRenderer>();
            for (int i = 0; i < renderers.Count; i++)
            {
                var mr = renderers[i];
                if (!mr.isActiveAndEnabled) continue;
                Mesh mesh = (mr is SkinnedMeshRenderer smr) ? smr.sharedMesh : mr.GetComponent<MeshFilter>()?.sharedMesh;
                mesh?.EnsureUploaded();
            }
        }

        private void Render3DScene(SDL_GPUCommandBuffer* cmd, int w, int h)
        {
            if (!_textures.TryGetValue(_sceneRt.GpuTextureId, out var sceneRec)) return;
            Camera cam = Camera.main;
            Color bg = cam != null ? cam.backgroundColor : new Color(0.03f, 0.013f, 0.057f, 1f);
            bool drawSkybox = cam == null || cam.clearFlags == 1;

            var colorTarget = new SDL_GPUColorTargetInfo
            {
                texture = sceneRec.Tex,
                clear_color = new SDL_FColor { r = bg.r, g = bg.g, b = bg.b, a = 1f },
                load_op = drawSkybox ? SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE : SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE
            };
            var depthTarget = new SDL_GPUDepthStencilTargetInfo
            {
                texture = _depthTex,
                clear_depth = 1.0f,
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE
            };

            SDL_GPURenderPass* rp = SDL3.SDL_BeginGPURenderPass(cmd, &colorTarget, 1, &depthTarget);
            if (cam == null) { SDL3.SDL_EndGPURenderPass(rp); return; }

            Transform ct = cam.transform;
            Matrix4x4 view = Matrix4x4.LookToLH(ct.position, ct.forward, ct.up, ct.right);
            Matrix4x4 proj = Matrix4x4.PerspectiveLHZeroToOne(cam.fieldOfView, (float)w / Math.Max(1, h), cam.nearClipPlane, cam.farClipPlane);
            Matrix4x4 viewProj = proj * view;

            if (drawSkybox)
            {
                DrawSkybox(cmd, rp, view, proj);
            }

            // Collect up to 4 active lights
            var allLights = SceneRegistry.GetAllComponents<Light>();
            StdFragUniforms baseFrag = default;
            baseFrag.ambientColor[0] = RenderSettings.ambientLight.r * RenderSettings.ambientIntensity;
            baseFrag.ambientColor[1] = RenderSettings.ambientLight.g * RenderSettings.ambientIntensity;
            baseFrag.ambientColor[2] = RenderSettings.ambientLight.b * RenderSettings.ambientIntensity;
            baseFrag.ambientColor[3] = 1f;
            baseFrag.cameraPosCutoff[0] = ct.position.x;
            baseFrag.cameraPosCutoff[1] = ct.position.y;
            baseFrag.cameraPosCutoff[2] = ct.position.z;
            baseFrag.fogParams[0] = RenderSettings.fogColor.r;
            baseFrag.fogParams[1] = RenderSettings.fogColor.g;
            baseFrag.fogParams[2] = RenderSettings.fogColor.b;
            baseFrag.fogParams[3] = RenderSettings.fog ? RenderSettings.fogDensity : -1f;

            float[] sh = RenderSettings.AmbientProbeSH;
            if (sh != null && sh.Length == 27)
            {
                baseFrag.shAr[0] = sh[3];  baseFrag.shAr[1] = sh[1];  baseFrag.shAr[2] = sh[2];  baseFrag.shAr[3] = sh[0]  - sh[6];
                baseFrag.shAg[0] = sh[12]; baseFrag.shAg[1] = sh[10]; baseFrag.shAg[2] = sh[11]; baseFrag.shAg[3] = sh[9]  - sh[15];
                baseFrag.shAb[0] = sh[21]; baseFrag.shAb[1] = sh[19]; baseFrag.shAb[2] = sh[20]; baseFrag.shAb[3] = sh[18] - sh[24];

                baseFrag.shBr[0] = sh[4];  baseFrag.shBr[1] = sh[5];  baseFrag.shBr[2] = sh[6]  * 3f; baseFrag.shBr[3] = sh[7];
                baseFrag.shBg[0] = sh[13]; baseFrag.shBg[1] = sh[14]; baseFrag.shBg[2] = sh[15] * 3f; baseFrag.shBg[3] = sh[16];
                baseFrag.shBb[0] = sh[22]; baseFrag.shBb[1] = sh[23]; baseFrag.shBb[2] = sh[24] * 3f; baseFrag.shBb[3] = sh[25];

                baseFrag.shC[0] = sh[8]; baseFrag.shC[1] = sh[17]; baseFrag.shC[2] = sh[26]; baseFrag.shC[3] = 1f;
            }

            int lc = 0;
            for (int i = 0; i < allLights.Count && lc < 4; i++)
            {
                var l = allLights[i];
                if (!l.isActiveAndEnabled) continue;
                StdLightData* ld = lc switch { 0 => &baseFrag.l0, 1 => &baseFrag.l1, 2 => &baseFrag.l2, _ => &baseFrag.l3 };
                Vector3 lp = l.transform.position;
                Vector3 ldir = l.transform.forward;
                ld->posType[0] = lp.x; ld->posType[1] = lp.y; ld->posType[2] = lp.z; ld->posType[3] = (float)(int)l.type;
                ld->dirRange[0] = ldir.x; ld->dirRange[1] = ldir.y; ld->dirRange[2] = ldir.z; ld->dirRange[3] = l.range;
                ld->colorIntensity[0] = l.color.r; ld->colorIntensity[1] = l.color.g; ld->colorIntensity[2] = l.color.b; ld->colorIntensity[3] = l.intensity;
                float halfOuter = (l.spotAngle * 0.5f) * Mathf.Deg2Rad;
                ld->spotParams[0] = MathF.Cos(halfOuter);
                ld->spotParams[1] = MathF.Cos(halfOuter * 0.65f);
                lc++;
            }
            baseFrag.flags[3] = lc;

            var renderers = SceneRegistry.GetAllComponents<MeshRenderer>();
            var blended = new List<(MeshRenderer r, Mesh m, int subIdx, Material mat)>();

            int whiteId = Texture2D.WhiteTexture.GpuTextureId;
            SDL_GPUTexture* whiteTex = _textures[whiteId].Tex;

            // Opaque + Cutout pass
            for (int i = 0; i < renderers.Count; i++)
            {
                var mr = renderers[i];
                if (!mr.isActiveAndEnabled) continue;
                Mesh mesh = (mr is SkinnedMeshRenderer smr) ? smr.sharedMesh : mr.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                mesh.EnsureUploaded();
                if (mesh.GpuMeshId <= 0 || !_meshes.TryGetValue(mesh.GpuMeshId, out var gpuMesh)) continue;

                int firstSub = mr.isPartOfStaticBatch ? mr.staticBatchFirstSubMesh : 0;
                int subCount = mr.isPartOfStaticBatch ? mr.staticBatchSubMeshCount : gpuMesh.SubOffsets.Length;
                Matrix4x4 model = mr.isPartOfStaticBatch ? Matrix4x4.identity : mr.transform.localToWorldMatrix;
                Matrix4x4 mvp = viewProj * model;

                for (int s = 0; s < subCount; s++)
                {
                    int subIdx = firstSub + s;
                    if (subIdx < 0 || subIdx >= gpuMesh.SubOffsets.Length || gpuMesh.SubCounts[subIdx] == 0) continue;
                    Material mat = mr.sharedMaterials.Length == 0 ? null : mr.sharedMaterials[Math.Min(s, mr.sharedMaterials.Length - 1)];
                    if (mat != null && mat.Hidden) continue;
                    if (mat != null && mat.AlphaBlend) { blended.Add((mr, mesh, subIdx, mat)); continue; }

                    DrawSubmesh(cmd, rp, gpuMesh, subIdx, mr, mat, ref model, ref mvp, ref baseFrag, whiteTex);
                }
            }

            // Alpha-blended pass
            for (int i = 0; i < blended.Count; i++)
            {
                var item = blended[i];
                var gpuMesh = _meshes[item.m.GpuMeshId];
                Matrix4x4 model = item.r.isPartOfStaticBatch ? Matrix4x4.identity : item.r.transform.localToWorldMatrix;
                Matrix4x4 mvp = viewProj * model;
                DrawSubmesh(cmd, rp, gpuMesh, item.subIdx, item.r, item.mat, ref model, ref mvp, ref baseFrag, whiteTex);
            }

            SDL3.SDL_EndGPURenderPass(rp);
        }

        private void DrawSkybox(SDL_GPUCommandBuffer* cmd, SDL_GPURenderPass* rp, Matrix4x4 view, Matrix4x4 proj)
        {
            SDL3.SDL_BindGPUGraphicsPipeline(rp, _pipeSkybox);
            // Invert pure rotation+proj for ray reconstruction
            Matrix4x4 vpRot = proj * view;
            vpRot.m03 = 0; vpRot.m13 = 0; vpRot.m23 = 0;
            // Quick analytic inverse of orientation*proj: invert view rotation & proj diagonals
            if (!Invert4x4(view, out var invV) || !Invert4x4(proj, out var invP)) return;
            invV.m03 = 0; invV.m13 = 0; invV.m23 = 0;
            Matrix4x4 invVp = invV * invP;

            SkyboxUniforms su = default;
            invVp.CopyColumnMajor(new Span<float>(su.invViewProj, 16));
            su.skyTop[0] = 0.08f; su.skyTop[1] = 0.14f; su.skyTop[2] = 0.28f; su.skyTop[3] = 1f;
            su.skyHorizon[0] = 0.35f; su.skyHorizon[1] = 0.42f; su.skyHorizon[2] = 0.52f; su.skyHorizon[3] = 1f;
            su.groundColor[0] = 0.12f; su.groundColor[1] = 0.11f; su.groundColor[2] = 0.10f; su.groundColor[3] = 1f;
            su.sunDir[0] = 0.32f; su.sunDir[1] = -0.86f; su.sunDir[2] = -0.39f;
            SDL3.SDL_PushGPUFragmentUniformData(cmd, 0, (nint)(&su), (uint)sizeof(SkyboxUniforms));
            SDL3.SDL_DrawGPUPrimitives(rp, 3, 1, 0, 0);
        }

        private void DrawSubmesh(SDL_GPUCommandBuffer* cmd, SDL_GPURenderPass* rp, in GpuMeshRecord gpuMesh, int subIdx,
                                 MeshRenderer mr, Material mat, ref Matrix4x4 model, ref Matrix4x4 mvp,
                                 ref StdFragUniforms baseFrag, SDL_GPUTexture* whiteTex)
        {
            bool alphaBlend = mat?.AlphaBlend ?? false;
            bool dbl = mat?.DoubleSided ?? false;
            SDL3.SDL_BindGPUGraphicsPipeline(rp, alphaBlend ? _pipeAlphaBlend : (dbl ? _pipeOpaqueDouble : _pipeOpaqueBack));

            var vb = new SDL_GPUBufferBinding { buffer = gpuMesh.Vbo, offset = 0 };
            var ib = new SDL_GPUBufferBinding { buffer = gpuMesh.Ibo, offset = 0 };
            SDL3.SDL_BindGPUVertexBuffers(rp, 0, &vb, 1);
            SDL3.SDL_BindGPUIndexBuffer(rp, &ib, SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_32BIT);

            StdVertUniforms vu = default;
            mvp.CopyColumnMajor(new Span<float>(vu.mvp, 16));
            model.CopyColumnMajor(new Span<float>(vu.model, 16));
            vu.mainTexST[0] = mat?.mainTextureScale.x ?? 1f;
            vu.mainTexST[1] = mat?.mainTextureScale.y ?? 1f;
            vu.mainTexST[2] = mat?.mainTextureOffset.x ?? 0f;
            vu.mainTexST[3] = mat?.mainTextureOffset.y ?? 0f;
            if (mr.isPartOfStaticBatch)
            {
                vu.lightmapST[0] = 1f; vu.lightmapST[1] = 1f; vu.lightmapST[2] = 0f; vu.lightmapST[3] = 0f;
            }
            else
            {
                vu.lightmapST[0] = mr.lightmapScaleOffset.x;
                vu.lightmapST[1] = mr.lightmapScaleOffset.y;
                vu.lightmapST[2] = mr.lightmapScaleOffset.z;
                vu.lightmapST[3] = mr.lightmapScaleOffset.w;
            }
            SDL3.SDL_PushGPUVertexUniformData(cmd, 0, (nint)(&vu), (uint)sizeof(StdVertUniforms));

            StdFragUniforms fu = baseFrag;
            Color bc = mat?.color ?? Color.white;
            Color em = mat?.EmissionColor ?? Color.black;
            fu.baseColor[0] = bc.r; fu.baseColor[1] = bc.g; fu.baseColor[2] = bc.b; fu.baseColor[3] = bc.a;
            fu.emissionColor[0] = em.r; fu.emissionColor[1] = em.g; fu.emissionColor[2] = em.b; fu.emissionColor[3] = em.a;
            fu.ambientColor[3] = (mat?.FlatShaded ?? false) ? 1f : 0f;
            fu.cameraPosCutoff[3] = mat?.Cutoff ?? 0.5f;

            Texture albTexObj = mat?.mainTexture;
            GpuTexRecord albRec = default;
            bool hasAlb = albTexObj != null && albTexObj.GpuTextureId > 0 && _textures.TryGetValue(albTexObj.GpuTextureId, out albRec);
            SDL_GPUTexture* albTex = hasAlb ? albRec.Tex : whiteTex;
            SDL_GPUSampler* albSamp = hasAlb
                ? (albRec.Point ? (albRec.Clamp ? _sampPointClamp : _sampPointWrap)
                                : (albRec.Clamp ? _sampLinearClamp : _sampLinearWrap))
                : _sampLinearWrap;

            GpuTexRecord lmRec = default;
            bool hasLm = mr.lightmapIndex >= 0 && mr.lightmapIndex < LightmapRegistry.Lightmaps.Count &&
                         LightmapRegistry.Lightmaps[mr.lightmapIndex] != null &&
                         _textures.TryGetValue(LightmapRegistry.Lightmaps[mr.lightmapIndex].GpuTextureId, out lmRec);
            SDL_GPUTexture* lmTex = hasLm ? lmRec.Tex : whiteTex;

            GpuTexRecord dlmRec = default;
            bool hasDirLm = hasLm && mr.lightmapIndex < LightmapRegistry.DirLightmaps.Count &&
                            LightmapRegistry.DirLightmaps[mr.lightmapIndex] != null &&
                            _textures.TryGetValue(LightmapRegistry.DirLightmaps[mr.lightmapIndex].GpuTextureId, out dlmRec);
            SDL_GPUTexture* dlmTex = hasDirLm ? dlmRec.Tex : whiteTex;

            fu.flags[0] = hasAlb ? 1f : 0f;
            fu.flags[1] = hasLm ? 1f : 0f;
            fu.flags[2] = (mat?.AlphaCutout ?? false) ? 1f : 0f;
            fu.pbrParams[0] = mat?.Metallic ?? 0f;
            fu.pbrParams[1] = mat?.Smoothness ?? 0.5f;
            fu.pbrParams[2] = (mat == null || mat.IsStandard) ? 1f : 0f;
            fu.pbrParams[3] = hasDirLm ? 1f : 0f;
            SDL3.SDL_PushGPUFragmentUniformData(cmd, 0, (nint)(&fu), (uint)sizeof(StdFragUniforms));

            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[3];
            bindings[0] = new SDL_GPUTextureSamplerBinding { texture = albTex, sampler = albSamp };
            bindings[1] = new SDL_GPUTextureSamplerBinding { texture = lmTex, sampler = _sampLinearClamp };
            bindings[2] = new SDL_GPUTextureSamplerBinding { texture = dlmTex, sampler = _sampLinearClamp };
            SDL3.SDL_BindGPUFragmentSamplers(rp, 0, bindings, 3);

            SDL3.SDL_DrawGPUIndexedPrimitives(rp, gpuMesh.SubCounts[subIdx], 1, gpuMesh.SubOffsets[subIdx], 0, 0);
        }

        public void Blit(int srcTexId, int srcW, int srcH, int dstRtId, int dstW, int dstH,
                         string shaderName, int pass,
                         IReadOnlyDictionary<string, float> floats,
                         IReadOnlyDictionary<string, int> ints,
                         IReadOnlyDictionary<string, float[]> vectors,
                         IReadOnlyDictionary<string, int> textures)
        {
            SDL_GPUCommandBuffer* cmd = _activeCmd != null ? _activeCmd : SDL3.SDL_AcquireGPUCommandBuffer(_device);
            bool ownCmd = _activeCmd == null;

            if (!_textures.TryGetValue(srcTexId, out var srcRec) || !_textures.TryGetValue(dstRtId, out var dstRec))
            {
                if (ownCmd) SDL3.SDL_SubmitGPUCommandBuffer(cmd);
                return;
            }

            SDL_GPUGraphicsPipeline* pipe = _postFx.GetPipeline(shaderName, pass, SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM);
            var ct = new SDL_GPUColorTargetInfo { texture = dstRec.Tex, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE };
            SDL_GPURenderPass* rp = SDL3.SDL_BeginGPURenderPass(cmd, &ct, 1, null);
            SDL3.SDL_BindGPUGraphicsPipeline(rp, pipe);

            SDL_GPUTexture* Aux(string key) => (textures != null && textures.TryGetValue(key, out int aid) && _textures.TryGetValue(aid, out var ar)) ? ar.Tex : srcRec.Tex;
            SDL_GPUTextureSamplerBinding* b = stackalloc SDL_GPUTextureSamplerBinding[4];
            b[0] = new SDL_GPUTextureSamplerBinding { texture = srcRec.Tex, sampler = _sampLinearClamp };
            b[1] = new SDL_GPUTextureSamplerBinding { texture = Aux("_OriginalTex") != srcRec.Tex ? Aux("_OriginalTex") : Aux("_GaussianTex"), sampler = _sampLinearClamp };
            b[2] = new SDL_GPUTextureSamplerBinding { texture = Aux("_TFM") != srcRec.Tex ? Aux("_TFM") : Aux("_DogTex"), sampler = _sampLinearClamp };
            b[3] = new SDL_GPUTextureSamplerBinding { texture = Aux("_BlendTex"), sampler = _sampLinearClamp };
            SDL3.SDL_BindGPUFragmentSamplers(rp, 0, b, 4);

            PostFxUniforms pu = PostFxShaders.PackUniforms(shaderName, pass, srcW, srcH, floats, ints, vectors);
            SDL3.SDL_PushGPUFragmentUniformData(cmd, 0, (nint)(&pu), (uint)sizeof(PostFxUniforms));
            SDL3.SDL_DrawGPUPrimitives(rp, 3, 1, 0, 0);
            SDL3.SDL_EndGPURenderPass(rp);

            if (ownCmd) SDL3.SDL_SubmitGPUCommandBuffer(cmd);
        }

        private void RenderCanvasUi(SDL_GPUCommandBuffer* cmd, int winW, int winH)
        {
            _uiVerts.Clear();
            _uiBatches.Clear();

            var canvases = SceneRegistry.GetAllComponents<Canvas>();
            canvases.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));

            // Reference 1280x720 scale keeps 1049x706 instructions and 776x408 menu well-framed
            float uiScale = MathF.Min(winW / 1280f, winH / 720f);
            var rootRect = new Rect(0f, 0f, winW, winH);
            int whiteTexId = Texture2D.WhiteTexture.GpuTextureId;

            foreach (var c in canvases)
            {
                if (!c.isActiveAndEnabled) continue;
                CollectUiTree(c.transform, rootRect, uiScale, whiteTexId);
            }

            OverlayRenderer?.Invoke(new OverlayDrawContext(this, winW, winH, uiScale, whiteTexId));

            if (_uiVerts.Count == 0 || !_textures.TryGetValue(_finalRt.GpuTextureId, out var finalRec)) return;

            int bytesNeeded = _uiVerts.Count * sizeof(UiVertex);
            if (bytesNeeded > _uiVboCapacityBytes)
            {
                if (_uiVbo != null) SDL3.SDL_ReleaseGPUBuffer(_device, _uiVbo);
                _uiVboCapacityBytes = Math.Max(bytesNeeded, 65536);
                var bi = new SDL_GPUBufferCreateInfo { usage = (SDL_GPUBufferUsageFlags)SDL3.SDL_GPU_BUFFERUSAGE_VERTEX, size = (uint)_uiVboCapacityBytes };
                _uiVbo = SDL3.SDL_CreateGPUBuffer(_device, &bi);
            }

            var tbInfo = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)bytesNeeded };
            SDL_GPUTransferBuffer* tb = SDL3.SDL_CreateGPUTransferBuffer(_device, &tbInfo);
            void* mapped = (void*)SDL3.SDL_MapGPUTransferBuffer(_device, tb, false);
            var span = CollectionsMarshal.AsSpan(_uiVerts);
            fixed (UiVertex* pSrc = span) Buffer.MemoryCopy(pSrc, mapped, bytesNeeded, bytesNeeded);
            SDL3.SDL_UnmapGPUTransferBuffer(_device, tb);

            SDL_GPUCopyPass* cp = SDL3.SDL_BeginGPUCopyPass(cmd);
            var sl = new SDL_GPUTransferBufferLocation { transfer_buffer = tb, offset = 0 };
            var dr = new SDL_GPUBufferRegion { buffer = _uiVbo, offset = 0, size = (uint)bytesNeeded };
            SDL3.SDL_UploadToGPUBuffer(cp, &sl, &dr, false);
            SDL3.SDL_EndGPUCopyPass(cp);
            SDL3.SDL_ReleaseGPUTransferBuffer(_device, tb);

            var ct = new SDL_GPUColorTargetInfo { texture = finalRec.Tex, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE };
            SDL_GPURenderPass* rp = SDL3.SDL_BeginGPURenderPass(cmd, &ct, 1, null);
            SDL3.SDL_BindGPUGraphicsPipeline(rp, _pipeUi);
            var vb = new SDL_GPUBufferBinding { buffer = _uiVbo, offset = 0 };
            SDL3.SDL_BindGPUVertexBuffers(rp, 0, &vb, 1);

            float* scr = stackalloc float[4] { winW, winH, 0f, 0f };
            SDL3.SDL_PushGPUVertexUniformData(cmd, 0, (nint)scr, 16);

            foreach (var batch in _uiBatches)
            {
                SDL_GPUTexture* tex = _textures.TryGetValue(batch.texId, out var trec) ? trec.Tex : _textures[whiteTexId].Tex;
                var b = new SDL_GPUTextureSamplerBinding { texture = tex, sampler = _sampLinearClamp };
                SDL3.SDL_BindGPUFragmentSamplers(rp, 0, &b, 1);
                SDL3.SDL_DrawGPUPrimitives(rp, (uint)batch.vertCount, 1, (uint)batch.startVert, 0);
            }
            SDL3.SDL_EndGPURenderPass(rp);
        }

        private void CollectUiTree(Transform tr, Rect parentRect, float uiScale, int whiteTexId)
        {
            if (!tr.gameObject.activeSelf) return;

            Rect myRect = parentRect;
            if (tr is RectTransform rt && !(tr.gameObject.GetComponent<Canvas>() != null && tr.parent == null))
            {
                myRect = rt.ComputeScreenRect(parentRect, uiScale);
            }

            // Convert bottom-up Unity screen rect -> top-down pixel coords
            float x0 = myRect.x;
            float x1 = myRect.x + myRect.width;
            float yTop = Screen.height - (myRect.y + myRect.height);
            float yBot = Screen.height - myRect.y;

            var img = tr.gameObject.GetComponent<Image>();
            if (img != null && img.enabled && img.color.a > 0.002f)
            {
                int tid = (img.sprite?.texture != null && img.sprite.texture.GpuTextureId > 0) ? img.sprite.texture.GpuTextureId : whiteTexId;
                EmitQuad(x0, yTop, x1, yBot, 0f, 0f, 1f, 1f, img.color, tid);
            }

            var txt = tr.gameObject.GetComponent<Text>();
            if (txt != null && txt.enabled && !string.IsNullOrEmpty(txt.text) && txt.color.a > 0.002f && FontAtlas.Default != null)
            {
                EmitTextQuads(txt, x0, yTop, x1 - x0, yBot - yTop, uiScale);
            }

            for (int i = 0; i < tr.childCount; i++)
            {
                CollectUiTree(tr.GetChild(i), myRect, uiScale, whiteTexId);
            }
        }

        private void EmitTextQuads(Text txt, float rx, float ry, float rw, float rh, float uiScale)
            => EmitTextString(txt.text, txt.fontSize, txt.color, rx, ry, rw, rh, uiScale, centered: true);

        internal void EmitTextString(string s, int fontSize, Color color, float rx, float ry, float rw, float rh, float uiScale, bool centered)
        {
            var atlas = FontAtlas.Default;
            if (atlas == null || string.IsNullOrEmpty(s)) return;
            float pxScale = (Math.Max(8, fontSize) * uiScale) / atlas.BasePixelHeight;
            float lineW = atlas.MeasureLineWidth(s, pxScale);
            float lineH = atlas.LineHeightPx * pxScale;
            float cursorX = centered ? rx + (rw - lineW) * 0.5f : rx + 14f * uiScale;
            float baselineY = ry + (rh - lineH) * 0.5f + atlas.AscentPx * pxScale;
            int tid = atlas.AtlasTexture.GpuTextureId;

            for (int i = 0; i < s.Length; i++)
            {
                if (!atlas.TryGetGlyph(s[i], out var g)) { cursorX += g.Advance * pxScale; continue; }
                if (g.W > 0 && g.H > 0)
                {
                    float gx0 = cursorX + g.XOff * pxScale;
                    float gy0 = baselineY + g.YOff * pxScale;
                    EmitQuad(gx0, gy0, gx0 + g.W * pxScale, gy0 + g.H * pxScale, g.U0, g.V0, g.U1, g.V1, color, tid);
                }
                cursorX += g.Advance * pxScale;
            }
        }

        internal void EmitQuad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, Color c, int texId)
        {
            int start = _uiVerts.Count;
            _uiVerts.Add(new UiVertex { X = x0, Y = y0, U = u0, V = v0, R = c.r, G = c.g, B = c.b, A = c.a });
            _uiVerts.Add(new UiVertex { X = x1, Y = y0, U = u1, V = v0, R = c.r, G = c.g, B = c.b, A = c.a });
            _uiVerts.Add(new UiVertex { X = x1, Y = y1, U = u1, V = v1, R = c.r, G = c.g, B = c.b, A = c.a });
            _uiVerts.Add(new UiVertex { X = x0, Y = y0, U = u0, V = v0, R = c.r, G = c.g, B = c.b, A = c.a });
            _uiVerts.Add(new UiVertex { X = x1, Y = y1, U = u1, V = v1, R = c.r, G = c.g, B = c.b, A = c.a });
            _uiVerts.Add(new UiVertex { X = x0, Y = y1, U = u0, V = v1, R = c.r, G = c.g, B = c.b, A = c.a });

            if (_uiBatches.Count > 0 && _uiBatches[^1].texId == texId)
            {
                var last = _uiBatches[^1];
                _uiBatches[^1] = (last.startVert, last.vertCount + 6, texId);
            }
            else
            {
                _uiBatches.Add((start, 6, texId));
            }
        }

        private static bool Invert4x4(Matrix4x4 m, out Matrix4x4 inv)
        {
            inv = Matrix4x4.identity;
            float[] a = { m.m00, m.m10, m.m20, m.m30, m.m01, m.m11, m.m21, m.m31, m.m02, m.m12, m.m22, m.m32, m.m03, m.m13, m.m23, m.m33 };
            float[] o = new float[16];
            o[0] = a[5]*a[10]*a[15] - a[5]*a[11]*a[14] - a[9]*a[6]*a[15] + a[9]*a[7]*a[14] + a[13]*a[6]*a[11] - a[13]*a[7]*a[10];
            o[4] = -a[4]*a[10]*a[15] + a[4]*a[11]*a[14] + a[8]*a[6]*a[15] - a[8]*a[7]*a[14] - a[12]*a[6]*a[11] + a[12]*a[7]*a[10];
            o[8] = a[4]*a[9]*a[15] - a[4]*a[11]*a[13] - a[8]*a[5]*a[15] + a[8]*a[7]*a[13] + a[12]*a[5]*a[11] - a[12]*a[7]*a[9];
            o[12] = -a[4]*a[9]*a[14] + a[4]*a[10]*a[13] + a[8]*a[5]*a[14] - a[8]*a[6]*a[13] - a[12]*a[5]*a[10] + a[12]*a[6]*a[9];
            o[1] = -a[1]*a[10]*a[15] + a[1]*a[11]*a[14] + a[9]*a[2]*a[15] - a[9]*a[3]*a[14] - a[13]*a[2]*a[11] + a[13]*a[3]*a[10];
            o[5] = a[0]*a[10]*a[15] - a[0]*a[11]*a[14] - a[8]*a[2]*a[15] + a[8]*a[3]*a[14] + a[12]*a[2]*a[11] - a[12]*a[3]*a[10];
            o[9] = -a[0]*a[9]*a[15] + a[0]*a[11]*a[13] + a[8]*a[1]*a[15] - a[8]*a[3]*a[13] - a[12]*a[1]*a[11] + a[12]*a[3]*a[9];
            o[13] = a[0]*a[9]*a[14] - a[0]*a[10]*a[13] - a[8]*a[1]*a[14] + a[8]*a[2]*a[13] + a[12]*a[1]*a[10] - a[12]*a[2]*a[9];
            o[2] = a[1]*a[6]*a[15] - a[1]*a[7]*a[14] - a[5]*a[2]*a[15] + a[5]*a[3]*a[14] + a[13]*a[2]*a[7] - a[13]*a[3]*a[6];
            o[6] = -a[0]*a[6]*a[15] + a[0]*a[7]*a[14] + a[4]*a[2]*a[15] - a[4]*a[3]*a[14] - a[12]*a[2]*a[7] + a[12]*a[3]*a[6];
            o[10] = a[0]*a[5]*a[15] - a[0]*a[7]*a[13] - a[4]*a[1]*a[15] + a[4]*a[3]*a[13] + a[12]*a[1]*a[7] - a[12]*a[3]*a[5];
            o[14] = -a[0]*a[5]*a[14] + a[0]*a[6]*a[13] + a[4]*a[1]*a[14] - a[4]*a[2]*a[13] - a[12]*a[1]*a[6] + a[12]*a[2]*a[5];
            o[3] = -a[1]*a[6]*a[11] + a[1]*a[7]*a[10] + a[5]*a[2]*a[11] - a[5]*a[3]*a[10] - a[9]*a[2]*a[7] + a[9]*a[3]*a[6];
            o[7] = a[0]*a[6]*a[11] - a[0]*a[7]*a[10] - a[4]*a[2]*a[11] + a[4]*a[3]*a[10] + a[8]*a[2]*a[7] - a[8]*a[3]*a[6];
            o[11] = -a[0]*a[5]*a[11] + a[0]*a[7]*a[9] + a[4]*a[1]*a[11] - a[4]*a[3]*a[9] - a[8]*a[1]*a[7] + a[8]*a[3]*a[5];
            o[15] = a[0]*a[5]*a[10] - a[0]*a[6]*a[9] - a[4]*a[1]*a[10] + a[4]*a[2]*a[9] + a[8]*a[1]*a[6] - a[8]*a[2]*a[5];
            float det = a[0]*o[0] + a[1]*o[4] + a[2]*o[8] + a[3]*o[12];
            if (MathF.Abs(det) < 1e-10f) return false;
            float d = 1f / det;
            inv.m00=o[0]*d; inv.m10=o[1]*d; inv.m20=o[2]*d; inv.m30=o[3]*d;
            inv.m01=o[4]*d; inv.m11=o[5]*d; inv.m21=o[6]*d; inv.m31=o[7]*d;
            inv.m02=o[8]*d; inv.m12=o[9]*d; inv.m22=o[10]*d; inv.m32=o[11]*d;
            inv.m03=o[12]*d; inv.m13=o[13]*d; inv.m23=o[14]*d; inv.m33=o[15]*d;
            return true;
        }

        private static void WritePng(string path, int w, int h, byte[] rgba)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Span<byte> ihdr = stackalloc byte[13];
            WriteBE(ihdr[0..4], (uint)w);
            WriteBE(ihdr[4..8], (uint)h);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(fs, "IHDR", ihdr);

            using var ms = new MemoryStream();
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                int rowBytes = w * 4;
                byte[] filterZero = { 0 };
                for (int y = 0; y < h; y++)
                {
                    zlib.Write(filterZero);
                    zlib.Write(rgba, y * rowBytes, rowBytes);
                }
            }
            WriteChunk(fs, "IDAT", ms.ToArray());
            WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(Stream s, string tag, ReadOnlySpan<byte> data)
        {
            Span<byte> hdr = stackalloc byte[8];
            WriteBE(hdr[0..4], (uint)data.Length);
            Encoding.ASCII.GetBytes(tag, hdr[4..8]);
            s.Write(hdr);
            s.Write(data);
            uint crc = Crc32(hdr[4..8], data);
            Span<byte> cbuf = stackalloc byte[4];
            WriteBE(cbuf, crc);
            s.Write(cbuf);
        }

        private static void WriteBE(Span<byte> dst, uint v)
        {
            dst[0] = (byte)(v >> 24); dst[1] = (byte)(v >> 16); dst[2] = (byte)(v >> 8); dst[3] = (byte)v;
        }

        private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte bt in a) { crc ^= bt; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
            foreach (byte bt in b) { crc ^= bt; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
            return crc ^ 0xFFFFFFFFu;
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SDL3.SDL_WaitForGPUIdle(_device);
            RenderTexture.ClearPool();
            _postFx?.Dispose();
            if (_uiVbo != null) SDL3.SDL_ReleaseGPUBuffer(_device, _uiVbo);
            if (_depthTex != null) SDL3.SDL_ReleaseGPUTexture(_device, _depthTex);
            foreach (var rec in _textures.Values) if (rec.Tex != null) SDL3.SDL_ReleaseGPUTexture(_device, rec.Tex);
            foreach (var rec in _meshes.Values) { if (rec.Vbo != null) SDL3.SDL_ReleaseGPUBuffer(_device, rec.Vbo); if (rec.Ibo != null) SDL3.SDL_ReleaseGPUBuffer(_device, rec.Ibo); }
            if (_pipeOpaqueBack != null) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, _pipeOpaqueBack);
            if (_pipeOpaqueDouble != null) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, _pipeOpaqueDouble);
            if (_pipeAlphaBlend != null) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, _pipeAlphaBlend);
            if (_pipeSkybox != null) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, _pipeSkybox);
            if (_pipeUi != null) SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, _pipeUi);
            if (_sampLinearWrap != null) SDL3.SDL_ReleaseGPUSampler(_device, _sampLinearWrap);
            if (_sampLinearClamp != null) SDL3.SDL_ReleaseGPUSampler(_device, _sampLinearClamp);
            if (_sampPointClamp != null) SDL3.SDL_ReleaseGPUSampler(_device, _sampPointClamp);
            if (_sampPointWrap != null) SDL3.SDL_ReleaseGPUSampler(_device, _sampPointWrap);
            SDL3.SDL_ReleaseWindowFromGPUDevice(_device, _window);
            SDL3.SDL_DestroyGPUDevice(_device);
        }
    }
}
