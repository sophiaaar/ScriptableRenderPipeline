using System;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline
    {
        Material m_DepthResolveMaterial = null;

        void InitializePrepass(HDRenderPipelineAsset hdAsset)
        {
            m_DepthResolveMaterial = CoreUtils.CreateEngineMaterial(asset.renderPipelineResources.shaders.depthValuesPS);
        }

        void CleanupPrepass()
        {
            CoreUtils.Destroy(m_DepthResolveMaterial);
        }

        struct PrepassOutput
        {
            public GBufferOutput       gbuffer;
            public RenderGraphResource depthValuesMSAA;
        }

        PrepassOutput RenderPrepass(RenderGraph renderGraph, CullingResults cullingResults, HDCamera hdCamera)
        {
            StartLegacyStereo(renderGraph, hdCamera);

            var result = new PrepassOutput();

            bool renderMotionVectorAfterGBuffer = RenderDepthPrepass(renderGraph, cullingResults, hdCamera);

            if (!renderMotionVectorAfterGBuffer)
            {
                // If objects motion vectors are enabled, this will render the objects with motion vector into the target buffers (in addition to the depth)
                // Note: An object with motion vector must not be render in the prepass otherwise we can have motion vector write that should have been rejected
                RenderObjectsMotionVectors(renderGraph, cullingResults, hdCamera);
            }

            // At this point in forward all objects have been rendered to the prepass (depth/normal/motion vectors) so we can resolve them
            result.depthValuesMSAA = ResolvePrepassBuffers(renderGraph, hdCamera);

            /*
            // This will bind the depth buffer if needed for DBuffer)
            RenderDBuffer(hdCamera, cmd, renderContext, cullingResults);
            // We can call DBufferNormalPatch after RenderDBuffer as it only affect forward material and isn't affected by RenderGBuffer
            // This reduce lifteime of stencil bit
            DBufferNormalPatch(hdCamera, cmd, renderContext, cullingResults);

#if ENABLE_RAYTRACING
            bool raytracedIndirectDiffuse = m_RaytracingIndirectDiffuse.RenderIndirectDiffuse(hdCamera, cmd, renderContext, m_FrameCount);
            PushFullScreenDebugTexture(hdCamera, cmd, m_RaytracingIndirectDiffuse.GetIndirectDiffuseTexture(), FullScreenDebugMode.IndirectDiffuse);
            cmd.SetGlobalInt(HDShaderIDs._RaytracedIndirectDiffuse, raytracedIndirectDiffuse ? 1 : 0);
#endif
*/

            result.gbuffer = RenderGBuffer(renderGraph, cullingResults, hdCamera);

            // In both forward and deferred, everything opaque should have been rendered at this point so we can safely copy the depth buffer for later processing.
            GenerateDepthPyramid(renderGraph, hdCamera, FullScreenDebugMode.DepthPyramid);

            if (renderMotionVectorAfterGBuffer)
            {
                // See the call RenderObjectsMotionVectors() above and comment
                RenderObjectsMotionVectors(renderGraph, cullingResults, hdCamera);
            }

            RenderCameraMotionVectors(renderGraph, hdCamera);

            StopLegacyStereo(renderGraph, hdCamera);

            return result;
        }

        class DepthPrepassData
        {
            public FrameSettings frameSettings;
            public bool msaaEnabled;
            public bool hasDepthOnlyPrepass;

            public RenderGraphMutableResource depthBuffer;
            public RenderGraphMutableResource depthAsColorBuffer;
            public RenderGraphMutableResource normalBuffer;

            public RenderGraphResource rendererListMRT;
            public RenderGraphResource rendererListDepthOnly;

#if ENABLE_RAYTRACING
            public HDRaytracingManager rayTracingManager;
            public RenderGraphResource renderListRayTracingOpaque;
            public RenderGraphResource renderListRayTracingTransparent;
#endif
        }

        // RenderDepthPrepass render both opaque and opaque alpha tested based on engine configuration.
        // Lit Forward only: We always render all materials
        // Lit Deferred: We always render depth prepass for alpha tested (optimization), other deferred material are render based on engine configuration.
        // Forward opaque with deferred renderer (DepthForwardOnly pass): We always render all materials
        // True is returned if motion vector must be rendered after GBuffer pass
        bool RenderDepthPrepass(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            var depthPrepassParameters = PrepareDepthPrepass(cull, hdCamera);

            bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA);

            using (var builder = renderGraph.AddRenderPass<DepthPrepassData>(depthPrepassParameters.passName, out var passData, CustomSamplerId.DepthPrepass.GetSampler()))
            {
                passData.frameSettings = hdCamera.frameSettings;
                passData.msaaEnabled = msaa;
                passData.hasDepthOnlyPrepass = depthPrepassParameters.hasDepthOnlyPass;

                passData.depthBuffer = builder.WriteTexture(GetDepthStencilBuffer(msaa));
                passData.normalBuffer = builder.WriteTexture(GetNormalBuffer(msaa));
                if (msaa)
                    passData.depthAsColorBuffer = builder.WriteTexture(GetDepthTexture(true));

                if (depthPrepassParameters.hasDepthOnlyPass)
                {
                    passData.rendererListDepthOnly = builder.UseRendererList(builder.CreateRendererList(depthPrepassParameters.depthOnlyRendererListDesc));
                }

                passData.rendererListMRT = builder.UseRendererList(builder.CreateRendererList(depthPrepassParameters.mrtRendererListDesc));

#if ENABLE_RAYTRACING
                passData.renderListRayTracingOpaque = builder.UseRendererList(builder.CreateRendererList(depthPrepassParameters.rayTracingOpaqueRLDesc));
                passData.renderListRayTracingTransparent builder.UseRendererList(builder.CreateRendererList(depthPrepassParameters.rayTracingTransparentRLDesc));
#endif

                builder.SetRenderFunc(
                (DepthPrepassData data, RenderGraphContext context) =>
                {
                    var mrt = RenderGraphUtils.GetMRTArray(data.msaaEnabled ? 2 : 1);
                    mrt[0] = context.resources.GetTexture(data.normalBuffer);
                    if (data.msaaEnabled)
                        mrt[1] = context.resources.GetTexture(data.depthAsColorBuffer);

                    RenderDepthPrepass(context.renderContext, context.cmd, data.frameSettings,
                                    mrt,
                                    context.resources.GetTexture(data.depthBuffer),
                                    context.resources.GetRendererList(data.rendererListDepthOnly),
                                    context.resources.GetRendererList(data.rendererListMRT),
                                    data.hasDepthOnlyPrepass
#if ENABLE_RAYTRACING
                                    data.rayTracingManager,
                                    context.resources.GetRendererList(data.renderListRayTracingOpaque),
                                    context.resources.GetRendererList(data.renderListRayTracingTransparent)
#endif
                                    );
                });
            }

            return depthPrepassParameters.shouldRenderMotionVectorAfterGBuffer;
        }

        class ObjectMotionVectorsPassData
        {
            public FrameSettings                frameSettings;
            public RenderGraphMutableResource   depthBuffer;
            public RenderGraphMutableResource   motionVectorsBuffer;
            public RenderGraphMutableResource   normalBuffer;
            public RenderGraphMutableResource   depthAsColorMSAABuffer;
            public RenderGraphResource          rendererList;
        }

        void RenderObjectsMotionVectors(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.ObjectMotionVectors))
                return;

            using (var builder = renderGraph.AddRenderPass<ObjectMotionVectorsPassData>("Objects Motion Vectors Rendering", out var passData, CustomSamplerId.ObjectsMotionVector.GetSampler()))
            {
                bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA);

                passData.frameSettings = hdCamera.frameSettings;
                passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer(msaa));
                passData.motionVectorsBuffer = builder.UseColorBuffer(GetMotionVectorsBuffer(msaa), 0);
                passData.normalBuffer = builder.UseColorBuffer(GetNormalBuffer(msaa), 1);
                if (msaa)
                    passData.depthAsColorMSAABuffer = builder.UseColorBuffer(GetDepthTexture(msaa), 2);

                passData.rendererList = builder.UseRendererList(
                    builder.CreateRendererList(CreateOpaqueRendererListDesc(cull, hdCamera.camera, HDShaderPassNames.s_MotionVectorsName, PerObjectData.MotionVectors)));

                builder.SetRenderFunc(
                (ObjectMotionVectorsPassData data, RenderGraphContext context) =>
                {
                    DrawOpaqueRendererList(context, data.frameSettings, context.resources.GetRendererList(data.rendererList));
                });
            }
        }

        class GBufferPassData
        {
            public FrameSettings                frameSettings;
            public RenderGraphResource          rendererList;
            public RenderGraphMutableResource[] gbufferRT = new RenderGraphMutableResource[RenderGraphUtils.kMaxMRTCount];
            public RenderGraphMutableResource   depthBuffer;
        }

        struct GBufferOutput
        {
            public RenderGraphResource[] gbuffer;
        }

        void SetupGBufferTargets(GBufferPassData passData, ref GBufferOutput output, FrameSettings frameSettings, RenderGraphBuilder builder)
        {
            bool clearGBuffer = NeedClearGBuffer();
            bool lightLayers = frameSettings.IsEnabled(FrameSettingsField.LightLayers);
            bool shadowMasks = frameSettings.IsEnabled(FrameSettingsField.ShadowMask);

            passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer());
            passData.gbufferRT[0] = builder.UseColorBuffer(GetSSSBuffer(false), 0);
            passData.gbufferRT[1] = builder.UseColorBuffer(GetNormalBuffer(), 1);
            passData.gbufferRT[2] = builder.UseColorBuffer(builder.CreateTexture(
                new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_UNorm, slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "GBuffer2" }, HDShaderIDs._GBufferTexture[2]), 2);
            passData.gbufferRT[3] = builder.UseColorBuffer(builder.CreateTexture(
                new TextureDesc(Vector2.one) { colorFormat = Builtin.GetLightingBufferFormat(), slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "GBuffer3" }, HDShaderIDs._GBufferTexture[3]), 3);

            int currentIndex = 4;
            if (lightLayers)
            {
                passData.gbufferRT[currentIndex] = builder.UseColorBuffer(builder.CreateTexture(
                    new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_UNorm, slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "LightLayers" }, HDShaderIDs._LightLayersTexture), currentIndex);
                currentIndex++;
            }
            if (shadowMasks)
            {
                passData.gbufferRT[currentIndex] = builder.UseColorBuffer(builder.CreateTexture(
                    new TextureDesc(Vector2.one) { colorFormat = Builtin.GetShadowMaskBufferFormat(), slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "ShadowMasks" }, HDShaderIDs._ShadowMaskTexture), currentIndex);
                currentIndex++;
            }

            output.gbuffer = new RenderGraphResource[currentIndex];
            for (int i = 0; i < currentIndex; ++i)
                output.gbuffer[i] = passData.gbufferRT[i];
        }

        // RenderGBuffer do the gbuffer pass. This is only called with deferred. If we use a depth prepass, then the depth prepass will perform the alpha testing for opaque alpha tested and we don't need to do it anymore
        // during Gbuffer pass. This is handled in the shader and the depth test (equal and no depth write) is done here.
        GBufferOutput RenderGBuffer(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            var output = new GBufferOutput();

            if (hdCamera.frameSettings.litShaderMode != LitShaderMode.Deferred)
                return output;

            using (var builder = renderGraph.AddRenderPass<GBufferPassData>("GBuffer", out var passData, CustomSamplerId.GBuffer.GetSampler()))
            {
                FrameSettings frameSettings = hdCamera.frameSettings;

                passData.frameSettings = frameSettings;
                SetupGBufferTargets(passData, ref output, frameSettings, builder);
                passData.rendererList = builder.UseRendererList(
                    builder.CreateRendererList(CreateOpaqueRendererListDesc(cull, hdCamera.camera, HDShaderPassNames.s_GBufferName, m_currentRendererConfigurationBakedLighting)));

                builder.SetRenderFunc(
                (GBufferPassData data, RenderGraphContext context) =>
                {
                    DrawOpaqueRendererList(context, data.frameSettings, context.resources.GetRendererList(data.rendererList));
                });
            }

            return output;
        }

        class ResolvePrepassData
        {
            public RenderGraphMutableResource   depthBuffer;
            public RenderGraphMutableResource   depthValuesBuffer;
            public RenderGraphMutableResource   normalBuffer;
            public RenderGraphResource          depthAsColorBufferMSAA;
            public RenderGraphResource          normalBufferMSAA;
            public Material                     depthResolveMaterial;
            public int                          depthResolvePassIndex;
        }

        // SharedRTManager.ResolveSharedRT
        RenderGraphResource ResolvePrepassBuffers(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA))
                return new RenderGraphResource();

            using (var builder = renderGraph.AddRenderPass<ResolvePrepassData>("Resolve Prepass MSAA", out var passData))
            {
                // This texture stores a set of depth values that are required for evaluating a bunch of effects in MSAA mode (R = Samples Max Depth, G = Samples Min Depth, G =  Samples Average Depth)
                RenderGraphMutableResource depthValuesBuffer = builder.CreateTexture(new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat, slices = TextureXR.slices, dimension = TextureXR.dimension, useDynamicScale = true, name = "DepthValuesBuffer" });

                passData.depthResolveMaterial = m_DepthResolveMaterial;
                passData.depthResolvePassIndex = SampleCountToPassIndex(m_MSAASamples);

                passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer(false));
                //passData.velocityBuffer = builder.UseColorBuffer(GetVelocityBufferResource(msaa), 0);
                passData.depthValuesBuffer = builder.UseColorBuffer(depthValuesBuffer, 0);
                passData.normalBuffer = builder.UseColorBuffer(GetNormalBuffer(false), 1);

                passData.normalBufferMSAA = builder.ReadTexture(GetNormalBuffer(true));
                passData.depthAsColorBufferMSAA = builder.ReadTexture(GetDepthTexture(true));

                builder.SetRenderFunc(
                (ResolvePrepassData data, RenderGraphContext context) =>
                {
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.depthResolveMaterial, data.depthResolvePassIndex, MeshTopology.Triangles, 3, 1);
                });

                return depthValuesBuffer;
            }
        }

        class CopyDepthPassData
        {
            public RenderGraphResource          inputDepth;
            public RenderGraphMutableResource   outputDepth;
            public GPUCopy                      GPUCopy;
            public int                          width;
            public int                          height;
        }

        void CopyDepthBufferIfNeeded(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (!m_IsDepthBufferCopyValid)
            {
                using (var builder = renderGraph.AddRenderPass<CopyDepthPassData>("Copy depth buffer", out var passData, CustomSamplerId.CopyDepthBuffer.GetSampler()))
                {
                    passData.inputDepth = builder.ReadTexture(GetDepthStencilBuffer());
                    passData.outputDepth = builder.WriteTexture(GetDepthTexture());
                    passData.GPUCopy = m_GPUCopy;
                    passData.width = hdCamera.actualWidth;
                    passData.height = hdCamera.actualHeight;

                    builder.SetRenderFunc(
                    (CopyDepthPassData data, RenderGraphContext context) =>
                    {
                        RenderGraphResourceRegistry resources = context.resources;
                        // TODO: maybe we don't actually need the top MIP level?
                        // That way we could avoid making the copy, and build the MIP hierarchy directly.
                        // The downside is that our SSR tracing accuracy would decrease a little bit.
                        // But since we never render SSR at full resolution, this may be acceptable.

                        // TODO: reading the depth buffer with a compute shader will cause it to decompress in place.
                        // On console, to preserve the depth test performance, we must NOT decompress the 'm_CameraDepthStencilBuffer' in place.
                        // We should call decompressDepthSurfaceToCopy() and decompress it to 'm_CameraDepthBufferMipChain'.
                        data.GPUCopy.SampleCopyChannel_xyzw2x(context.cmd, resources.GetTexture(data.inputDepth), resources.GetTexture(data.outputDepth), new RectInt(0, 0, data.width, data.height));
                    });
                }

                m_IsDepthBufferCopyValid = true;
            }
        }

        class GenerateDepthPyramidPassData
        {
            public RenderGraphMutableResource   depthTexture;
            public HDUtils.PackedMipChainInfo   mipInfo;
            public MipGenerator                 mipGenerator;
        }

        void GenerateDepthPyramid(RenderGraph renderGraph, HDCamera hdCamera, FullScreenDebugMode debugMode)
        {
            // If the depth buffer hasn't been already copied by the decal pass, then we do the copy here.
            CopyDepthBufferIfNeeded(renderGraph, hdCamera);

            using (var builder = renderGraph.AddRenderPass<GenerateDepthPyramidPassData>("Generate Depth Buffer MIP Chain", out var passData, CustomSamplerId.DepthPyramid.GetSampler()))
            {
                passData.depthTexture = builder.WriteTexture(GetDepthTexture());
                passData.mipInfo = GetDepthBufferMipChainInfo();
                passData.mipGenerator = m_MipGenerator;

                builder.SetRenderFunc(
                (GenerateDepthPyramidPassData data, RenderGraphContext context) =>
                {
                    data.mipGenerator.RenderMinDepthPyramid(context.cmd, context.resources.GetTexture(data.depthTexture), data.mipInfo);
                });
            }

            //int mipCount = GetDepthBufferMipChainInfo().mipLevelCount;

            //float scaleX = hdCamera.actualWidth / (float)m_SharedRTManager.GetDepthTexture().rt.width;
            //float scaleY = hdCamera.actualHeight / (float)m_SharedRTManager.GetDepthTexture().rt.height;
            //m_PyramidSizeV4F.Set(hdCamera.actualWidth, hdCamera.actualHeight, 1f / hdCamera.actualWidth, 1f / hdCamera.actualHeight);
            //m_PyramidScaleLod.Set(scaleX, scaleY, mipCount, 0.0f);
            //m_PyramidScale.Set(scaleX, scaleY, 0f, 0f);
            //cmd.SetGlobalTexture(HDShaderIDs._DepthPyramidTexture, m_SharedRTManager.GetDepthTexture());
            //cmd.SetGlobalVector(HDShaderIDs._DepthPyramidScale, m_PyramidScaleLod);

            //PushFullScreenDebugTextureMip(hdCamera, cmd, m_SharedRTManager.GetDepthTexture(), mipCount, m_PyramidScale, debugMode);
        }

        class CameraMotionVectorsPassData
        {
            public Material cameraMotionVectorsMaterial;
            public RenderGraphMutableResource motionVectorsBuffer;
            public RenderGraphMutableResource depthBuffer;
        }

        void RenderCameraMotionVectors(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.MotionVectors))
                return;

            using (var builder = renderGraph.AddRenderPass<CameraMotionVectorsPassData>("Camera Motion Vectors Rendering", out var passData, CustomSamplerId.CameraMotionVectors.GetSampler()))
            {
                // These flags are still required in SRP or the engine won't compute previous model matrices...
                // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
                hdCamera.camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

                passData.cameraMotionVectorsMaterial = m_CameraMotionVectorsMaterial;
                passData.depthBuffer = builder.WriteTexture(GetDepthStencilBuffer());
                passData.motionVectorsBuffer = builder.WriteTexture(GetMotionVectorsBuffer());

                builder.SetRenderFunc(
                (CameraMotionVectorsPassData data, RenderGraphContext context) =>
                {
                    var res = context.resources;
                    HDUtils.DrawFullScreen(context.cmd, data.cameraMotionVectorsMaterial, res.GetTexture(data.motionVectorsBuffer), res.GetTexture(data.depthBuffer), null, 0);
                });
            }

            //            PushFullScreenDebugTexture(hdCamera, cmd, m_SharedRTManager.GetMotionVectorsBuffer(), FullScreenDebugMode.MotionVectors);
            //#if UNITY_EDITOR

            //            // In scene view there is no motion vector, so we clear the RT to black
            //            if (hdCamera.camera.cameraType == CameraType.SceneView && !CoreUtils.AreAnimatedMaterialsEnabled(hdCamera.camera))
            //            {
            //                HDUtils.SetRenderTarget(cmd, hdCamera, m_SharedRTManager.GetVelocityBuffer(), m_SharedRTManager.GetDepthStencilBuffer(), ClearFlag.Color, Color.clear);
            //            }
            //#endif
        }
    }
}
