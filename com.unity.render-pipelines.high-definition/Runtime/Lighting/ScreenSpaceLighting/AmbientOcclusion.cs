using System;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    using RTHandle = RTHandleSystem.RTHandle;

    [Serializable, VolumeComponentMenu("Lighting/Ambient Occlusion")]
    public sealed class AmbientOcclusion : VolumeComponent
    {
        [Tooltip("Controls the strength of the ambient occlusion effect. Increase this value to produce darker areas.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 4f);

        [Tooltip("Number of steps to take along one signed direction during horizon search (this is the number of steps in positive and negative direction).")]
        public ClampedIntParameter stepCount = new ClampedIntParameter(4, 2, 32);

        [Tooltip("Sampling radius. Bigger the radius, wider AO will be achieved, risking to lose fine details and increasing cost of the effect due to increasing cache misses.")]
        public ClampedFloatParameter radius = new ClampedFloatParameter(1.5f, 0.25f, 5.0f);

        [Tooltip("The effect runs at full resolution. This increases quality, but also decreases performance significantly.")]
        public BoolParameter fullResolution = new BoolParameter(true);

        [Tooltip("Controls the thickness of occluders. Increase this value to increase the size of dark areas.")]
        public ClampedFloatParameter thicknessModifier = new ClampedFloatParameter(1f, 1f, 10f);

        [Tooltip("Controls how much the ambient light affects occlusion.")]
        public ClampedFloatParameter directLightingStrength = new ClampedFloatParameter(0f, 0f, 1f);

        // Hidden parameters
        [HideInInspector] public ClampedFloatParameter noiseFilterTolerance = new ClampedFloatParameter(0f, -8f, 0f);
        [HideInInspector] public ClampedFloatParameter blurTolerance = new ClampedFloatParameter(-4.6f, -8f, 1f);
        [HideInInspector] public ClampedFloatParameter upsampleTolerance = new ClampedFloatParameter(-12f, -12f, -1f);
    }

    public class AmbientOcclusionSystem
    {
        RenderPipelineResources m_Resources;
        RenderPipelineSettings m_Settings;

        private bool m_HistoryReady = false;
        private RTHandle m_PackedDataTex;
        private RTHandle m_PackedDataBlurred;
        private RTHandle[] m_PackedHistory;
        private RTHandle m_AmbientOcclusionTex;
        private RTHandle m_FinalHalfRes;

        private RTHandle m_BentNormalTex;

        private bool m_RunningFullRes = false;
        private int m_HistoryIndex = 0;

        // TODO_FCC: Old.
        //readonly ScaleFunc[] m_ScaleFunctors;

        //// MSAA-specifics
        //readonly RTHandle m_MultiAmbientOcclusionTex;
        //readonly MaterialPropertyBlock m_ResolvePropertyBlock;
        //readonly Material m_ResolveMaterial;

#if ENABLE_RAYTRACING
        public HDRaytracingManager m_RayTracingManager = new HDRaytracingManager();
        readonly HDRaytracingAmbientOcclusion m_RaytracingAmbientOcclusion = new HDRaytracingAmbientOcclusion();
#endif

        private void ReleaseRT()
        {
            RTHandles.Release(m_AmbientOcclusionTex);
            RTHandles.Release(m_BentNormalTex);
            RTHandles.Release(m_PackedDataTex);
            RTHandles.Release(m_PackedDataBlurred);
            for (int i = 0; i < m_PackedHistory.Length; ++i)
            {
                RTHandles.Release(m_PackedHistory[i]);
            }

            if (m_FinalHalfRes != null)
                RTHandles.Release(m_FinalHalfRes);
        }

        void AllocRT(float scaleFactor)
        {
            m_AmbientOcclusionTex = RTHandles.Alloc(Vector2.one, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8_UNorm, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Ambient Occlusion");
            m_BentNormalTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8G8B8A8_SNorm, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Bent normals");
            m_PackedDataTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed data");
            m_PackedDataBlurred = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed blurred data");
            m_PackedHistory[0] = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed history_1");
            m_PackedHistory[1] = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed history_2");

            m_FinalHalfRes = RTHandles.Alloc(Vector2.one * 0.5f, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Final Half Res AO Packed");
        }

        void EnsureRTSize(AmbientOcclusion settings)
        {
            if (settings.fullResolution != m_RunningFullRes)
            {
                ReleaseRT();

                m_RunningFullRes = settings.fullResolution.value;
                float scaleFactor = m_RunningFullRes ? 1.0f : 0.5f;
                AllocRT(scaleFactor);
            }
        }

        public AmbientOcclusionSystem(HDRenderPipelineAsset hdAsset)
        {
            m_Settings = hdAsset.currentPlatformRenderPipelineSettings;
            m_Resources = hdAsset.renderPipelineResources;

            if (!hdAsset.currentPlatformRenderPipelineSettings.supportSSAO)
                return;

            m_PackedHistory = new RTHandle[2];
            AllocRT(0.5f);

            // TODO_FCC: Consider.
            //bool supportMSAA = hdAsset.currentPlatformRenderPipelineSettings.supportMSAA;
            //if (supportMSAA)
            //{
            //    m_MultiAmbientOcclusionTex = RTHandles.Alloc(Vector2.one,
            //        filterMode: FilterMode.Bilinear,
            //        colorFormat: GraphicsFormat.R8G8_UNorm,
            //        enableRandomWrite: true,
            //        xrInstancing: true,
            //        useDynamicScale: true,
            //        name: "Ambient Occlusion MSAA"
            //    );

            //    m_ResolveMaterial = CoreUtils.CreateEngineMaterial(m_Resources.shaders.aoResolvePS);
            //    m_ResolvePropertyBlock = new MaterialPropertyBlock();
            //}
        }

        public void Cleanup()
        {
#if ENABLE_RAYTRACING
            m_RaytracingAmbientOcclusion.Release();
#endif

            ReleaseRT();
        }

#if ENABLE_RAYTRACING
        public void InitRaytracing(HDRaytracingManager raytracingManager, SharedRTManager sharedRTManager)
        {
            m_RayTracingManager = raytracingManager;
            m_RaytracingAmbientOcclusion.Init(m_Resources, m_Settings, m_RayTracingManager, sharedRTManager);
        }
#endif

        public bool IsActive(HDCamera camera, AmbientOcclusion settings) => camera.frameSettings.IsEnabled(FrameSettingsField.SSAO) && settings.intensity.value > 0f;

        public void Render(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, ScriptableRenderContext renderContext, int frameCount)
        {

#if ENABLE_RAYTRACING
            HDRaytracingEnvironment rtEnvironement = m_RayTracingManager.CurrentEnvironment();
            if (rtEnvironement != null && rtEnvironement.raytracedAO)
                m_RaytracingAmbientOcclusion.RenderAO(camera, cmd, m_AmbientOcclusionTex, renderContext, frameCount);
            else
#endif
            {
                Dispatch(cmd, camera, sharedRTManager, frameCount);
                PostDispatchWork(cmd, camera, sharedRTManager);
            }
        }

        private void RenderAO(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, int frameCount)
        {
            // Grab current settings
            var settings = VolumeManager.instance.stack.GetComponent<AmbientOcclusion>();

            if (!IsActive(camera, settings))
                return;

            EnsureRTSize(settings);

            Vector4 aoBufferInfo;
            Vector2 runningRes;

            if (settings.fullResolution.value)
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight);
                aoBufferInfo = new Vector4(camera.actualWidth, camera.actualHeight, 1.0f / camera.actualWidth, 1.0f / camera.actualHeight);
            }
            else
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight) * 0.5f;
                aoBufferInfo = new Vector4(camera.actualWidth * 0.5f, camera.actualHeight * 0.5f, 2.0f / camera.actualWidth, 2.0f / camera.actualHeight);
            }

            float invHalfTanFOV = -camera.mainViewConstants.projMatrix[1, 1];
            float aspectRatio = runningRes.y / runningRes.x;

            Vector4 aoParams0 = new Vector4(
                settings.fullResolution.value ? 0.0f : 1.0f,
                runningRes.y * invHalfTanFOV * 0.25f,
                settings.radius.value,
                settings.stepCount.value
                );


            Vector4 aoParams1 = new Vector4(
                settings.intensity.value,
                1.0f / (settings.radius.value * settings.radius.value),
                (frameCount / 6) % 4,
                (frameCount % 6)
                );


            // We start from screen space position, so we bake in this factor the 1 / resolution as well. 
            Vector4 toViewSpaceProj = new Vector4(
                2.0f / (invHalfTanFOV * aspectRatio * runningRes.x),
                2.0f / (invHalfTanFOV * runningRes.y),
                1.0f / (invHalfTanFOV * aspectRatio),
                1.0f / invHalfTanFOV
                );


            HDUtils.PackedMipChainInfo info = sharedRTManager.GetDepthBufferMipChainInfo();
            Vector4 aoParams2 = new Vector4(
                info.mipLevelOffsets[1].x,
                info.mipLevelOffsets[1].y,
                1.0f / ((float)settings.stepCount.value + 1.0f),
                0
            );

            var cs = m_Resources.shaders.GTAOCS;
            var kernel = cs.FindKernel("GTAOMain");

            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AODepthToViewParams, toViewSpaceProj);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams0, aoParams0);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams1, aoParams1);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams2, aoParams2);

            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._BentNormalsTexture, m_BentNormalTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);

            const int groupSizeX = 8;
            const int groupSizeY = 8;
            int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
            int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
            using (new ProfilingSample(cmd, "GTAO Tracing", CustomSamplerId.RenderSSAO.GetSampler()))
            {
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);
            }
        }

        private void DenoiseAO(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager)
        {
            var settings = VolumeManager.instance.stack.GetComponent<AmbientOcclusion>();

            if (!IsActive(camera, settings))
                return;

            var cs = m_Resources.shaders.GTAODenoiseCS;

            Vector4 aoBufferInfo;
            Vector2 runningRes;

            if (m_RunningFullRes)
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight);
                aoBufferInfo = new Vector4(camera.actualWidth, camera.actualHeight, 1.0f / camera.actualWidth, 1.0f / camera.actualHeight);
            }
            else
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight) * 0.5f;
                aoBufferInfo = new Vector4(camera.actualWidth * 0.5f, camera.actualHeight * 0.5f, 2.0f / camera.actualWidth, 2.0f / camera.actualHeight);
            }

            Vector4 aoParams0 = new Vector4(
                settings.fullResolution.value ? 0.0f : 1.0f,
                0, // not needed
                settings.radius.value,
                settings.stepCount.value
            );


            Vector4 aoParams1 = new Vector4(
                settings.intensity.value,
                1.0f / (settings.radius.value * settings.radius.value),
                0,
                0
            );

            HDUtils.PackedMipChainInfo info = sharedRTManager.GetDepthBufferMipChainInfo();
            Vector4 aoParams2 = new Vector4(
                info.mipLevelOffsets[m_RunningFullRes ? 0 : 1].x,
                info.mipLevelOffsets[m_RunningFullRes ? 0 : 1].y,
                1.0f / ((float)settings.stepCount.value + 1.0f),
                0
            );

            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams0, aoParams0);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams1, aoParams1);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams2, aoParams2);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);

            // Spatial
            using (new ProfilingSample(cmd, "Spatial Denoise GTAO", CustomSamplerId.ResolveSSAO.GetSampler()))
            {
                var kernel = cs.FindKernel("GTAODenoise_Spatial");

                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);
                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedBlurred, m_PackedDataBlurred);
                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);

                const int groupSizeX = 8;
                const int groupSizeY = 8;
                int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
                int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);
            }

            if (!m_HistoryReady)
            {
                cmd.Blit(m_PackedDataTex, m_PackedHistory[m_HistoryIndex]);
                m_HistoryReady = true;
            }

            // Temporal
            using (new ProfilingSample(cmd, "Temporal GTAO", CustomSamplerId.ResolveSSAO.GetSampler()))
            {
                int outputIndex = (m_HistoryIndex + 1) & 1;

                int kernel;
                if (m_RunningFullRes)
                {
                    kernel = cs.FindKernel("GTAODenoise_Temporal_FullRes");
                }
                else
                {
                    kernel = cs.FindKernel("GTAODenoise_Temporal");
                }

                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);
                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedBlurred, m_PackedDataBlurred);
                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedHistory, m_PackedHistory[m_HistoryIndex]);
                cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOOutputHistory, m_PackedHistory[outputIndex]);
                if (m_RunningFullRes)
                {
                    cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);
                }
                else
                {
                    cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_FinalHalfRes);
                }

                const int groupSizeX = 8;
                const int groupSizeY = 8;
                int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
                int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);

                m_HistoryIndex = outputIndex;
            }

            // Need upsample
            if (!m_RunningFullRes)
            {
                using (new ProfilingSample(cmd, "Upsample GTAO", CustomSamplerId.ResolveSSAO.GetSampler()))
                {
                    cs = m_Resources.shaders.GTAOUpsampleCS;
                    var kernel = cs.FindKernel("AOUpsample");

                    cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams0, aoParams0);
                    cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams1, aoParams1);
                    cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);

                    cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_FinalHalfRes);
                    cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);

                    const int groupSizeX = 8;
                    const int groupSizeY = 8;
                    int threadGroupX = ((int)camera.actualWidth + (groupSizeX - 1)) / groupSizeX;
                    int threadGroupY = ((int)camera.actualHeight + (groupSizeY - 1)) / groupSizeY;
                    cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);

                }
            }
        }

        public void Dispatch(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, int frameCount)
        {
            using (new ProfilingSample(cmd, "GTAO", CustomSamplerId.RenderSSAO.GetSampler()))
            {
                RenderAO(cmd, camera, sharedRTManager, frameCount);
                DenoiseAO(cmd, camera, sharedRTManager);
            }
        }

        public void PostDispatchWork(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager)
        {
            // Grab current settings
            var settings = VolumeManager.instance.stack.GetComponent<AmbientOcclusion>();

            if (!IsActive(camera, settings))
            {
                // No AO applied - neutral is black, see the comment in the shaders
                cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, TextureXR.GetBlackTexture());
                cmd.SetGlobalVector(HDShaderIDs._AmbientOcclusionParam, Vector4.zero);
                return;
            }

            // MSAA Resolve // TODO_FCC: Implement.
            //if (camera.frameSettings.IsEnabled(FrameSettingsField.MSAA))
            //{
            //    using (new ProfilingSample(cmd, "Resolve AO Buffer", CustomSamplerId.ResolveSSAO.GetSampler()))
            //    {
            //        HDUtils.SetRenderTarget(cmd, m_AmbientOcclusionTex);
            //        m_ResolvePropertyBlock.SetTexture(HDShaderIDs._DepthValuesTexture, sharedRTManager.GetDepthValuesTexture());
            //        m_ResolvePropertyBlock.SetTexture(HDShaderIDs._MultiAmbientOcclusionTexture, m_MultiAmbientOcclusionTex);
            //        cmd.DrawProcedural(Matrix4x4.identity, m_ResolveMaterial, 0, MeshTopology.Triangles, 3, 1, m_ResolvePropertyBlock);
            //    }
            //}

            cmd.SetGlobalTexture(HDShaderIDs._AmbientOcclusionTexture, m_AmbientOcclusionTex);
            cmd.SetGlobalVector(HDShaderIDs._AmbientOcclusionParam, new Vector4(0f, 0f, 0f, settings.directLightingStrength.value));

            // TODO: All the push debug stuff should be centralized somewhere
            (RenderPipelineManager.currentPipeline as HDRenderPipeline).PushFullScreenDebugTexture(camera, cmd, m_AmbientOcclusionTex, FullScreenDebugMode.SSAO);
        }
    }
}
