using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline
    {
        class SubsurfaceScaterringPassData
        {
            public SubsurfaceScatteringParameters parameters;
            public RenderGraphResource colorBuffer;
            public RenderGraphResource diffuseBuffer;
            public RenderGraphResource depthStencilBuffer;
            public RenderGraphResource depthTexture;
            public RenderGraphMutableResource cameraFilteringBuffer;
            public RenderGraphMutableResource hTileBuffer;
            public RenderGraphResource sssBuffer;
        }

        void RenderSubsurfaceScattering(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphMutableResource colorBuffer,
            RenderGraphResource diffuseBuffer, RenderGraphResource sssBuffer, RenderGraphResource depthStencilBuffer, RenderGraphResource depthTexture)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.SubsurfaceScattering))
                return;

            using (var builder = renderGraph.AddRenderPass<SubsurfaceScaterringPassData>("Subsurface Scattering", out var passData, CustomSamplerId.SubsurfaceScattering.GetSampler()))
            {
                passData.parameters = PrepareSubsurfaceScatteringParameters(hdCamera);
                passData.colorBuffer = builder.WriteTexture(colorBuffer);
                passData.diffuseBuffer = builder.ReadTexture(diffuseBuffer);
                passData.depthStencilBuffer = builder.ReadTexture(depthStencilBuffer);
                passData.depthTexture = builder.ReadTexture(depthTexture);
                passData.sssBuffer = builder.ReadTexture(sssBuffer);
                passData.hTileBuffer = builder.WriteTexture(builder.CreateTexture(
                        new TextureDesc(size => new Vector2Int((size.x + 7) / 8, (size.y + 7) / 8))
                        { colorFormat = GraphicsFormat.R8_UNorm, enableRandomWrite = true, slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, name = "SSSHtile" }));
                if (passData.parameters.needTemporaryBuffer)
                {
                    passData.cameraFilteringBuffer = builder.WriteTexture(builder.CreateTexture(
                                            new TextureDesc(Vector2.one)
                                            { colorFormat = GraphicsFormat.B10G11R11_UFloatPack32, enableRandomWrite = true, clearBuffer = true, clearColor = Color.clear, slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, name = "SSSCameraFiltering" }));
                }

                builder.SetRenderFunc(
                (SubsurfaceScaterringPassData data, RenderGraphContext context) =>
                {
                    var resources = new SubsurfaceScatteringResources();
                    resources.colorBuffer = context.resources.GetTexture(data.colorBuffer);
                    resources.diffuseBuffer = context.resources.GetTexture(data.diffuseBuffer);
                    resources.depthStencilBuffer = context.resources.GetTexture(data.depthStencilBuffer);
                    resources.depthTexture = context.resources.GetTexture(data.depthTexture);
                    resources.cameraFilteringBuffer = context.resources.GetTexture(data.cameraFilteringBuffer);
                    resources.hTileBuffer = context.resources.GetTexture(data.hTileBuffer);
                    resources.sssBuffer = context.resources.GetTexture(data.sssBuffer);

                    RenderSubsurfaceScattering(data.parameters, resources, context.cmd);
                });
            }
        }
    }
}
