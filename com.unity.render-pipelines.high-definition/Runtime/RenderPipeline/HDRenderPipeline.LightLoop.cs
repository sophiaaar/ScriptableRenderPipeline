//using System;
//using UnityEngine.Rendering;
//using UnityEngine.Experimental.Rendering.RenderGraphModule;
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
            public BuildGPULightListParameters buildGPULightListparameters;
            public RenderGraphResource depthBuffer;
            public RenderGraphResource stencilTexture;
        }

        void BuildGPULightList(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphResource depthStencilBuffer, RenderGraphResource stencilBufferCopy)
        {
            using (var builder = renderGraph.AddRenderPass<BuildGPULightListPassData>("Build Light List", out var passData))
            {
                builder.EnableAsyncCompute(hdCamera.frameSettings.BuildLightListRunsAsync());

                // TODO: Implement compute buffer read/write
                passData.buildGPULightListparameters = PrepareBuildGPULightListParameters(hdCamera);
                passData.depthBuffer = builder.ReadTexture(depthStencilBuffer);
                passData.stencilTexture = builder.ReadTexture(stencilBufferCopy);

                builder.SetRenderFunc(
                (BuildGPULightListPassData data, RenderGraphContext context) =>
                {
                    bool tileFlagsWritten = false;

                    RTHandle depthBuffer = context.resources.GetTexture(data.depthBuffer);
                    RTHandle stencilTexture = context.resources.GetTexture(data.stencilTexture);

                    GenerateLightsScreenSpaceAABBs(data.buildGPULightListparameters, context.cmd);
                    BigTilePrepass(data.buildGPULightListparameters, context.cmd);
                    BuildPerTileLightList(data.buildGPULightListparameters, depthBuffer, ref tileFlagsWritten, context.cmd);
                    VoxelLightListGeneration(data.buildGPULightListparameters, depthBuffer, context.cmd);

                    FinalizeLightListGeneration(data.buildGPULightListparameters, stencilTexture, tileFlagsWritten, context.cmd);
                });

            }
        }
    }
}
