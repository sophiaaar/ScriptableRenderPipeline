using System;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    using RTHandle = RTHandleSystem.RTHandle;

    [Serializable, VolumeComponentMenu("Lighting/GTAO")]
    public sealed class GTAO : VolumeComponent
    {
        // TODO_FCC: This might not be relevant.
        [Tooltip("Controls the strength of the ambient occlusion effect. Increase this value to produce darker areas.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 8f);

        [Tooltip("TODO.")]
        public ClampedIntParameter stepCount = new ClampedIntParameter(4, 2, 32);

        [Tooltip("The sampling radius of AO.")]
        public ClampedFloatParameter radius = new ClampedFloatParameter(1.0f, 0.25f, 5.0f);

        [Tooltip("Runs at full resolution.")]
        public BoolParameter fullRes = new BoolParameter(false);
    }

    public class GTAOSystem
    {
        RenderPipelineResources m_Resources;

        private bool historyReady = false;
        private RTHandle m_PackedDataTex;
        private RTHandle m_PackedHistory;
        private RTHandle m_AmbientOcclusionTex;
        private RTHandle m_BentNormalTex;

        private bool m_RunningFullRes = false;

        public bool IsActive(HDCamera camera, GTAO settings) => camera.frameSettings.IsEnabled(FrameSettingsField.SSAO) && settings.intensity.value > 0f;

        private void ReleaseRT()
        {
            RTHandles.Release(m_AmbientOcclusionTex);
            RTHandles.Release(m_BentNormalTex);
            RTHandles.Release(m_PackedDataTex);
            RTHandles.Release(m_PackedHistory);
        }
        public void Cleanup()
        {
            ReleaseRT();
        }

        void AllocRT(float scaleFactor)
        {
            m_AmbientOcclusionTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8_UNorm, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Occlusion texture");
            m_BentNormalTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8G8B8A8_SNorm /*GraphicsFormat.R32G32B32A32_SFloat *//*  */, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Bent normal tex");
            m_PackedDataTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed data");
            m_PackedHistory = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_UInt, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "AO Packed history");
        }

        void EnsureRTSize(GTAO settings)
        {
            if(settings.fullRes != m_RunningFullRes)
            {
                ReleaseRT();

                m_RunningFullRes = settings.fullRes.value;
                float scaleFactor = m_RunningFullRes ? 1.0f : 0.5f;
                AllocRT(scaleFactor);
            }

        }

        public RTHandle GetAOTex()
        {
            return m_AmbientOcclusionTex;
        }

        public GTAOSystem(HDRenderPipelineAsset hdAsset)
        {
            if (!hdAsset.currentPlatformRenderPipelineSettings.supportSSAO)
                return;

            m_Resources = hdAsset.renderPipelineResources;
            AllocRT(0.5f);
        }

        // TODO_FCC: Make the following steps private and expose one global thing.
        public void Render(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, ComputeBuffer depthPyramidOffsets, int frameCount)
        {
            // Grab current settings
            var settings = VolumeManager.instance.stack.GetComponent<GTAO>();

            //if (!IsActive(camera, settings))
            //    return;

            EnsureRTSize(settings);

            // TODO: tmp remove me
          //  HDUtils.SetRenderTarget(cmd, m_AmbientOcclusionTex, sharedRTManager.GetDepthStencilBuffer(false), ClearFlag.Color, Color.black);

            Vector4 aoBufferInfo;
            Vector2 runningRes;

            if (settings.fullRes.value)
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
            //Vector2 focalLen = new Vector2(invHalfTanFov * ((float)RenderResolution.y / (float)RenderResolution.x), invHalfTanFov);
            //Vector2 invFocalLen = new Vector2(1 / focalLen.x, 1 / focalLen.y);
            //new Vector4(2 * invFocalLen.x, 2 * invFocalLen.y, -1 * invFocalLen.x, -1 * invFocalLen.y)

            Vector4 aoParams0 = new Vector4(
                settings.fullRes.value ? 0.0f : 1.0f,
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

            var cs = m_Resources.shaders.GTAOCS;
            var kernel = cs.FindKernel("GTAOMain");

            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AODepthToViewParams, toViewSpaceProj);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams0, aoParams0);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams1, aoParams1);


            HDUtils.PackedMipChainInfo info = sharedRTManager.GetDepthBufferMipChainInfo();
            cmd.SetComputeBufferParam(cs, kernel, HDShaderIDs._DepthPyramidMipLevelOffsets, info.GetOffsetBufferData(depthPyramidOffsets));

            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._BentNormalsTexture, m_BentNormalTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);

            const int groupSizeX = 8;
            const int groupSizeY = 8;
            int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
            int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
            using (new ProfilingSample(cmd, "TEST_GTAO", CustomSamplerId.MotionBlurKernel.GetSampler()))
            {
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);

            }

        }

        public void Denoise(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, ComputeBuffer depthPyramidOffsets)
        {
            // Grab current settings
            var settings = VolumeManager.instance.stack.GetComponent<GTAO>();

            var cs = m_Resources.shaders.GTAODenoiseCS;
            var kernel = cs.FindKernel("GTAODenoise_Temporal");
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._BentNormalsTexture, m_BentNormalTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);

            Vector4 aoBufferInfo;
            Vector2 runningRes;

            if (settings.fullRes.value)
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight);
                aoBufferInfo = new Vector4(camera.actualWidth, camera.actualHeight, 1.0f / camera.actualWidth, 1.0f / camera.actualHeight);
            }
            else
            {
                runningRes = new Vector2(camera.actualWidth, camera.actualHeight) * 0.5f;
                aoBufferInfo = new Vector4(camera.actualWidth * 0.5f, camera.actualHeight * 0.5f, 2.0f / camera.actualWidth, 2.0f / camera.actualHeight);
            }

            if(!historyReady)
            {
                cmd.Blit(m_PackedDataTex, m_PackedHistory);
                historyReady = true;
            }

            Vector4 aoParams1 = new Vector4(
                settings.intensity.value,
                1.0f / (settings.radius.value * settings.radius.value),
                0,
                0
            );
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams1, aoParams1);



            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);

            HDUtils.PackedMipChainInfo info = sharedRTManager.GetDepthBufferMipChainInfo();
            cmd.SetComputeBufferParam(cs, kernel, HDShaderIDs._DepthPyramidMipLevelOffsets, info.GetOffsetBufferData(depthPyramidOffsets));

            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._BentNormalsTexture, m_BentNormalTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedData, m_PackedDataTex);
            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._AOPackedHistory, m_PackedHistory);

            const int groupSizeX = 8;
            const int groupSizeY = 8;
            int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
            int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
            using (new ProfilingSample(cmd, "DENOISE GTAO", CustomSamplerId.MotionBlurKernel.GetSampler()))
            {
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);

            }


        }
    }
}
