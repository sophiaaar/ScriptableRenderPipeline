//using System;
//using UnityEngine.Rendering;
//using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    using RTHandle = RTHandleSystem.RTHandle;

    public partial class HDRenderPipeline
    {
        class CopyStencilBufferPassData
        {
            public HDCamera hdCamera;
            public RenderGraphResource depthStencilBuffer;
            public RenderGraphMutableResource stencilBufferCopy;
            public Material copyStencil;
            public Material copyStencilForSSR;
        }

        RenderGraphResource CopyStencilBufferIfNeeded(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphResource depthStencilBuffer, Material copyStencil, Material copyStencilForSSR)
        {
            using (var builder = renderGraph.AddRenderPass<CopyStencilBufferPassData>(out var passData))
            {
                passData.hdCamera = hdCamera;
                passData.depthStencilBuffer = depthStencilBuffer;
                passData.stencilBufferCopy = builder.WriteTexture(builder.CreateTexture(new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8_UNorm, enableRandomWrite = true, name = "CameraStencilCopy", useDynamicScale = true, xrInstancing = true }));
                passData.copyStencil = copyStencil;
                passData.copyStencilForSSR = copyStencilForSSR;

                builder.SetRenderFunc(
                (CopyStencilBufferPassData data, RenderGraphContext context) =>
                {
                    RTHandle depthBuffer = context.resources.GetTexture(data.depthStencilBuffer);
                    RTHandle stencilCopy = context.resources.GetTexture(data.stencilBufferCopy);
                    CopyStencilBufferIfNeeded(context.cmd, data.hdCamera, depthBuffer, stencilCopy, data.copyStencil, data.copyStencilForSSR);
                });

                return passData.stencilBufferCopy;
            }
        }

        class BuildGPULightListPassData
        {
            public BuildGPULightListParameters buildGPULightListParameters;
            public BuildGPULightListResources buildGPULightListResources;
            public RenderGraphResource depthBuffer;
            public RenderGraphResource stencilTexture;
        }

        void BuildGPULightList(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphResource depthStencilBuffer, RenderGraphResource stencilBufferCopy)
        {
            using (var builder = renderGraph.AddRenderPass<BuildGPULightListPassData>("Build Light List", out var passData))
            {
                builder.EnableAsyncCompute(hdCamera.frameSettings.BuildLightListRunsAsync());

                passData.buildGPULightListParameters = PrepareBuildGPULightListParameters(hdCamera);
                // TODO: Move this inside the render function onces compute buffers are RenderGraph ready
                passData.buildGPULightListResources = PrepareBuildGPULightListResources(null, null);
                passData.depthBuffer = builder.ReadTexture(depthStencilBuffer);
                passData.stencilTexture = builder.ReadTexture(stencilBufferCopy);

                builder.SetRenderFunc(
                (BuildGPULightListPassData data, RenderGraphContext context) =>
                {
                    bool tileFlagsWritten = false;

                    data.buildGPULightListResources.depthBuffer = context.resources.GetTexture(data.depthBuffer);
                    data.buildGPULightListResources.stencilTexture = context.resources.GetTexture(data.stencilTexture);

                    GenerateLightsScreenSpaceAABBs(data.buildGPULightListParameters, data.buildGPULightListResources, context.cmd);
                    BigTilePrepass(data.buildGPULightListParameters, data.buildGPULightListResources, context.cmd);
                    BuildPerTileLightList(data.buildGPULightListParameters, data.buildGPULightListResources, ref tileFlagsWritten, context.cmd);
                    VoxelLightListGeneration(data.buildGPULightListParameters, data.buildGPULightListResources, context.cmd);

                    BuildDispatchIndirectArguments(data.buildGPULightListParameters, data.buildGPULightListResources, tileFlagsWritten, context.cmd);
                });

            }
        }

        void RenderShadows(RenderGraph renderGraph, HDCamera hdCamera, CullingResults cullResults)
        {
            m_ShadowManager.RenderShadows(m_RenderGraph, hdCamera, cullResults);
        }

        class DeferredLightingPassData
        {
            public DeferredLightingParameters   parameters;
            public DeferredLightingResources    resources;

            public RenderGraphMutableResource   colorBuffer;
            public RenderGraphMutableResource   sssDifuseLightingBuffer;
            public RenderGraphResource          depthBuffer;
            public RenderGraphResource          depthTexture;
        }

        struct DeferredLightingOutput
        {
            public RenderGraphMutableResource colorBuffer;
            public RenderGraphMutableResource sssDifuseLightingBuffer;
        }

        DeferredLightingOutput RenderDeferredLighting(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphMutableResource colorBuffer, GBufferOutput gbuffer)
        {
            if (hdCamera.frameSettings.litShaderMode != LitShaderMode.Deferred)
                return new DeferredLightingOutput();

            using (var builder = renderGraph.AddRenderPass<DeferredLightingPassData>("Deferred Lighting", out var passData))
            {
                passData.parameters = PrepareDeferredLightingParameters(hdCamera, debugDisplaySettings);

                // TODO: Move this inside the render function onces compute buffers are RenderGraph ready
                passData.resources = new  DeferredLightingResources();
                passData.resources.lightListBuffer = s_LightList;
                passData.resources.tileFeatureFlagsBuffer = s_TileFeatureFlags;
                passData.resources.tileListBuffer = s_TileList;
                passData.resources.dispatchIndirectBuffer = s_DispatchIndirectBuffer;

                passData.colorBuffer = builder.WriteTexture(colorBuffer);
                if (passData.parameters.outputSplitLighting)
                {
                    passData.sssDifuseLightingBuffer = builder.WriteTexture(builder.CreateTexture(
                        new TextureDesc(Vector2.one)
                        { colorFormat = GraphicsFormat.B10G11R11_UFloatPack32, enableRandomWrite = true, clearBuffer = true, clearColor = Color.clear, xrInstancing = true, useDynamicScale = true, name = "CameraSSSDiffuseLighting" }));
                }
                passData.depthBuffer = builder.ReadTexture(GetDepthStencilBuffer());
                passData.depthTexture = builder.ReadTexture(GetDepthTexture());

                // No need to pass handles to render func as these will be automatically bound (ShaderTagId was passed at creation time)
                for (int i = 0; i < gbuffer.gbuffer.Length; ++i)
                    builder.ReadTexture(gbuffer.gbuffer[i]);

                var output = new DeferredLightingOutput();
                output.colorBuffer = passData.colorBuffer;
                output.sssDifuseLightingBuffer = passData.sssDifuseLightingBuffer;

                builder.SetRenderFunc(
                (DeferredLightingPassData data, RenderGraphContext context) =>
                {
                    data.resources.colorBuffers = context.renderGraphPool.GetTempArray<RenderTargetIdentifier>(2);
                    data.resources.colorBuffers[0] = context.resources.GetTexture(data.colorBuffer);
                    if (data.parameters.outputSplitLighting)
                        data.resources.colorBuffers[1] = context.resources.GetTexture(data.sssDifuseLightingBuffer);
                    data.resources.depthStencilBuffer = context.resources.GetTexture(data.depthBuffer);
                    data.resources.depthTexture = context.resources.GetTexture(data.depthTexture);

                    if (data.parameters.enableTile)
                    {
                        bool useCompute = data.parameters.useComputeLightingEvaluation && !k_PreferFragment;
                        if (useCompute)
                            RenderComputeDeferredLighting(data.parameters, data.resources, context.cmd);
                        else
                            RenderComputeAsPixelDeferredLighting(data.parameters, data.resources, context.cmd);
                    }
                    else
                    {
                        RenderPixelDeferredLighting(data.parameters, data.resources, context.cmd);
                    }
                });

                return output;
            }
        }
    }
}
