using System;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline
    {
        void ExecuteWithRenderGraph(RenderRequest renderRequest, ScriptableRenderContext renderContext, CommandBuffer cmd, DensityVolumeList densityVolumes)
        {
            var hdCamera = renderRequest.hdCamera;
            var camera = hdCamera.camera;
            var cullingResults = renderRequest.cullingResults.cullingResults;
            bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA);
            //var target = renderRequest.target;

            CreateSharedResources(m_RenderGraph, hdCamera, m_CurrentDebugDisplaySettings);

#if UNITY_EDITOR
            var showGizmos = camera.cameraType == CameraType.Game
                || camera.cameraType == CameraType.SceneView;
#endif

            RenderGraphMutableResource colorBuffer = CreateColorBuffer(hdCamera, true);

            if (m_CurrentDebugDisplaySettings.IsDebugMaterialDisplayEnabled() || m_CurrentDebugDisplaySettings.IsMaterialValidationEnabled())
            {
                StartLegacyStereo(m_RenderGraph, hdCamera);
                RenderDebugViewMaterial(m_RenderGraph, cullingResults, hdCamera, colorBuffer);
                colorBuffer = ResolveMSAAColor(m_RenderGraph, hdCamera, colorBuffer);
                StopLegacyStereo(m_RenderGraph, hdCamera);
            }
            else
            {
                var prepassOutput = RenderPrepass(m_RenderGraph, cullingResults, hdCamera);

                //// Caution: We require sun light here as some skies use the sun light to render, it means that UpdateSkyEnvironment must be called after PrepareLightsForGPU.
                //// TODO: Try to arrange code so we can trigger this call earlier and use async compute here to run sky convolution during other passes (once we move convolution shader to compute).
                //UpdateSkyEnvironment(hdCamera, cmd);

                StartLegacyStereo(m_RenderGraph, hdCamera);

#if ENABLE_RAYTRACING
                bool raytracedIndirectDiffuse = m_RaytracingIndirectDiffuse.RenderIndirectDiffuse(hdCamera, cmd, renderContext, m_FrameCount);
                if(raytracedIndirectDiffuse)
                {
                    PushFullScreenDebugTexture(hdCamera, cmd, m_RaytracingIndirectDiffuse.GetIndirectDiffuseTexture(), FullScreenDebugMode.IndirectDiffuse);
                }
#endif

                //if (!hdCamera.frameSettings.SSAORunsAsync())
                //    m_AmbientOcclusionSystem.Render(cmd, hdCamera, m_SharedRTManager, renderContext, m_FrameCount);

                var stencilBufferCopy = CopyStencilBufferIfNeeded(m_RenderGraph, hdCamera, GetDepthStencilBuffer(), m_CopyStencil, m_CopyStencilForSSR);

                //// When debug is enabled we need to clear otherwise we may see non-shadows areas with stale values.
                //if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.ContactShadows) && m_CurrentDebugDisplaySettings.data.fullScreenDebugMode == FullScreenDebugMode.ContactShadows)
                //{
                //    HDUtils.SetRenderTarget(cmd, m_ContactShadowBuffer, ClearFlag.Color, Color.clear);
                //}

//#if ENABLE_RAYTRACING
//                // Update the light clusters that we need to update
//                m_RayTracingManager.UpdateCameraData(cmd, hdCamera);

//                // We only request the light cluster if we are gonna use it for debug mode
//                if (FullScreenDebugMode.LightCluster == m_CurrentDebugDisplaySettings.data.fullScreenDebugMode)
//                {
//                    var settings = VolumeManager.instance.stack.GetComponent<ScreenSpaceReflection>();
//                    HDRaytracingEnvironment rtEnv = m_RayTracingManager.CurrentEnvironment();
//                    if(settings.enableRaytracing.value && rtEnv != null)
//                    {
//                        HDRaytracingLightCluster lightCluster = m_RayTracingManager.RequestLightCluster(rtEnv.reflLayerMask);
//                        PushFullScreenDebugTexture(hdCamera, cmd, lightCluster.m_DebugLightClusterTexture, FullScreenDebugMode.LightCluster);
//                    }
//                    else if(rtEnv != null && rtEnv.raytracedObjects)
//                    {
//                        HDRaytracingLightCluster lightCluster = m_RayTracingManager.RequestLightCluster(rtEnv.raytracedLayerMask);
//                        PushFullScreenDebugTexture(hdCamera, cmd, lightCluster.m_DebugLightClusterTexture, FullScreenDebugMode.LightCluster);
//                    }
//                }
//#endif

//                // Evaluate raytraced area shadows if required
//                bool areaShadowsRendered = false;
//#if ENABLE_RAYTRACING
//                areaShadowsRendered = m_RaytracingShadows.RenderAreaShadows(hdCamera, cmd, renderContext, m_FrameCount);
//#endif
//                cmd.SetGlobalInt(HDShaderIDs._RaytracedAreaShadow, areaShadowsRendered ? 1 : 0);

                hdCamera.xr.StopLegacyStereo(camera, cmd, renderContext);

                BuildGPULightList(m_RenderGraph, hdCamera, GetDepthStencilBuffer(msaa), stencilBufferCopy);

                RenderShadows(m_RenderGraph, hdCamera, cullingResults);
            }

            ExecuteRenderGraph(m_RenderGraph, hdCamera, m_MSAASamples, renderContext, cmd);
        }

        static void ExecuteRenderGraph(RenderGraph renderGraph, HDCamera hdCamera, MSAASamples msaaSample, ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            var renderGraphParams = new RenderGraphExecuteParams()
            {
                renderingWidth = hdCamera.actualWidth,
                renderingHeight = hdCamera.actualHeight,
                msaaSamples = msaaSample
            };

            renderGraph.Execute(renderContext, cmd, renderGraphParams);
        }

        RenderGraphMutableResource CreateColorBuffer(HDCamera hdCamera, bool allowMSAA)
        {
            bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA) && allowMSAA;
            return m_RenderGraph.CreateTexture(
                new TextureDesc(Vector2.one)
                {
                    colorFormat = GetColorBufferFormat(),
                    enableRandomWrite = !msaa,
                    bindTextureMS = msaa,
                    enableMSAA = msaa,
                    xrInstancing = true,
                    useDynamicScale = true,
                    clearBuffer = NeedClearColorBuffer(hdCamera),
                    clearColor = GetColorBufferClearColor(hdCamera),
                    name = "CameraColor" });
        }

        class DebugViewMaterialData
        {
            public RenderGraphMutableResource   outputColor;
            public RenderGraphMutableResource   outputDepth;
            public RenderGraphResource          opaqueRendererList;
            public RenderGraphResource          transparentRendererList;
            public Material                     debugGBufferMaterial;
            public FrameSettings                frameSettings;
        }

        void RenderDebugViewMaterial(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera, RenderGraphMutableResource output)
        {
            if (m_CurrentDebugDisplaySettings.data.materialDebugSettings.IsDebugGBufferEnabled() && hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred)
            {
                using (var builder = renderGraph.AddRenderPass<DebugViewMaterialData>("DebugViewMaterialGBuffer", out var passData, CustomSamplerId.DebugViewMaterialGBuffer.GetSampler()))
                {
                    passData.debugGBufferMaterial = m_currentDebugViewMaterialGBuffer;
                    passData.outputColor = builder.WriteTexture(output);

                    builder.SetRenderFunc(
                    (DebugViewMaterialData data, RenderGraphContext context) =>
                    {
                        var res = context.resources;
                        HDUtils.DrawFullScreen(context.cmd, data.debugGBufferMaterial, res.GetTexture(data.outputColor));
                    });
                }
            }
            else
            {
                using (var builder = renderGraph.AddRenderPass<DebugViewMaterialData>("DisplayDebug ViewMaterial", out var passData, CustomSamplerId.DisplayDebugViewMaterial.GetSampler()))
                {
                    passData.frameSettings = hdCamera.frameSettings;
                    passData.outputColor = builder.UseColorBuffer(output, 0);
                    passData.outputDepth = builder.UseDepthBuffer(GetDepthStencilBuffer(hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA)));

                    // When rendering debug material we shouldn't rely on a depth prepass for optimizing the alpha clip test. As it is control on the material inspector side
                    // we must override the state here.
                    passData.opaqueRendererList = builder.UseRendererList(
                        builder.CreateRendererList(CreateOpaqueRendererListDesc(cull, hdCamera.camera, m_AllForwardOpaquePassNames,
                            rendererConfiguration : m_currentRendererConfigurationBakedLighting,
                            stateBlock : m_DepthStateOpaque)));
                    passData.transparentRendererList= builder.UseRendererList(
                        builder.CreateRendererList(CreateTransparentRendererListDesc(cull, hdCamera.camera, m_AllTransparentPassNames,
                            rendererConfiguration : m_currentRendererConfigurationBakedLighting,
                            stateBlock : m_DepthStateOpaque)));

                    builder.SetRenderFunc(
                    (DebugViewMaterialData data, RenderGraphContext context) =>
                    {
                        var res = context.resources;
                        DrawOpaqueRendererList(context, data.frameSettings, res.GetRendererList(data.opaqueRendererList));
                        DrawTransparentRendererList(context, data.frameSettings, res.GetRendererList(data.transparentRendererList));
                    });
                }
            }
        }

        class ResolveColorData
        {
            public RenderGraphResource          input;
            public RenderGraphMutableResource   output;
            public Material                     resolveMaterial;
            public int                          passIndex;
        }

        RenderGraphMutableResource ResolveMSAAColor(RenderGraph renderGraph, HDCamera hdCamera, RenderGraphMutableResource input)
        {
            if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA))
            {
                using (var builder = renderGraph.AddRenderPass<ResolveColorData>("ResolveColor", out var passData))
                {
                    passData.input = builder.ReadTexture(input);
                    passData.output = builder.UseColorBuffer(CreateColorBuffer(hdCamera, false), 0);
                    passData.resolveMaterial = m_ColorResolveMaterial;
                    passData.passIndex = SampleCountToPassIndex(m_MSAASamples);

                    builder.SetRenderFunc(
                    (ResolveColorData data, RenderGraphContext context) =>
                    {
                        var res = context.resources;
                        var mpb = context.renderGraphPool.GetTempMaterialPropertyBlock();
                        mpb.SetTexture(HDShaderIDs._ColorTextureMS, res.GetTexture(data.input));
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.resolveMaterial, data.passIndex, MeshTopology.Triangles, 3, 1, mpb);
                    });

                    return passData.output;
                }
            }
            else
            {
                return input;
            }
        }

        static void DrawOpaqueRendererList(in RenderGraphContext context, in FrameSettings frameSettings, in RendererList rendererList)
        {
            DrawOpaqueRendererList(context.renderContext, context.cmd, frameSettings, rendererList);
        }

        static void DrawTransparentRendererList(in RenderGraphContext context, in FrameSettings frameSettings, RendererList rendererList)
        {
            DrawTransparentRendererList(context.renderContext, context.cmd, frameSettings, rendererList);
        }

        static int SampleCountToPassIndex(MSAASamples samples)
        {
            switch (samples)
            {
                case MSAASamples.None:
                    return 0;
                case MSAASamples.MSAA2x:
                    return 1;
                case MSAASamples.MSAA4x:
                    return 2;
                case MSAASamples.MSAA8x:
                    return 3;
            };
            return 0;
        }

        bool NeedClearColorBuffer(HDCamera hdCamera)
        {
            if (hdCamera.clearColorMode == HDAdditionalCameraData.ClearColorMode.Color ||
                // If the luxmeter is enabled, the sky isn't rendered so we clear the background color
                m_CurrentDebugDisplaySettings.data.lightingDebugSettings.debugLightingMode == DebugLightingMode.LuxMeter ||
                // If we want the sky but the sky doesn't exist, still clear with background color
                (hdCamera.clearColorMode == HDAdditionalCameraData.ClearColorMode.Sky && !m_SkyManager.IsVisualSkyValid()) ||
                m_CurrentDebugDisplaySettings.IsDebugMaterialDisplayEnabled() || m_CurrentDebugDisplaySettings.IsMaterialValidationEnabled() ||
                // Special handling for Preview we force to clear with background color (i.e black)
                HDUtils.IsRegularPreviewCamera(hdCamera.camera)
                )
            {
                return true;
            }

            return false;
        }

        Color GetColorBufferClearColor(HDCamera hdCamera)
        {
            Color clearColor = hdCamera.backgroundColorHDR;
            // We set the background color to black when the luxmeter is enabled to avoid picking the sky color
            if (debugDisplaySettings.data.lightingDebugSettings.debugLightingMode == DebugLightingMode.LuxMeter)
                clearColor = Color.black;

            return clearColor;
        }

        // XR Specific
        class StereoRenderingPassData
        {
            public Camera camera;
            public XRPass xr;
        }

        void StartLegacyStereo(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (hdCamera.xr.enabled)
            {
                using (var builder = renderGraph.AddRenderPass<StereoRenderingPassData>("StartStereoRendering", out var passData))
                {
                    passData.camera = hdCamera.camera;
                    passData.xr = hdCamera.xr;

                    builder.SetRenderFunc(
                    (StereoRenderingPassData data, RenderGraphContext context) =>
                    {
                        data.xr.StartLegacyStereo(data.camera, context.cmd, context.renderContext);
                    });
                }
            }
        }

        void StopLegacyStereo(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (hdCamera.xr.enabled && hdCamera.camera.stereoEnabled)
            {
                using (var builder = renderGraph.AddRenderPass<StereoRenderingPassData>("StopStereoRendering", out var passData))
                {
                    passData.camera = hdCamera.camera;
                    passData.xr = hdCamera.xr;

                    builder.SetRenderFunc(
                    (StereoRenderingPassData data, RenderGraphContext context) =>
                    {
                        data.xr.StopLegacyStereo(data.camera, context.cmd, context.renderContext);
                    });
                }
            }
        }
    }
}
