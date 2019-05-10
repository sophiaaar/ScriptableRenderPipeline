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
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 4f);

        [Tooltip("TODO.")]
        public ClampedIntParameter stepCount = new ClampedIntParameter(8, 2, 32);

        [Tooltip("The sampling radius of AO.")]
        public ClampedFloatParameter radius = new ClampedFloatParameter(3.0f, 0.25f, 5.0f);

        [Tooltip("Runs at full resolution.")]
        public BoolParameter fullRes = new BoolParameter(false);
    }

    public class GTAOSystem
    {
        RenderPipelineResources m_Resources;

        RTHandle m_AmbientOcclusionTex;
        RTHandle m_BentNormalTex;

        private bool m_RunningFullRes = false;

        public bool IsActive(HDCamera camera, GTAO settings) => camera.frameSettings.IsEnabled(FrameSettingsField.SSAO) && settings.intensity.value > 0f;

        public void Cleanup()
        {
            RTHandles.Release(m_AmbientOcclusionTex);
            RTHandles.Release(m_BentNormalTex);
        }

        void EnsureRTSize(GTAO settings)
        {
            if(settings.fullRes != m_RunningFullRes)
            {
                RTHandles.Release(m_AmbientOcclusionTex);
                RTHandles.Release(m_BentNormalTex);

                m_RunningFullRes = settings.fullRes.value;
                float scaleFactor = m_RunningFullRes ? 1.0f : 0.5f;
                m_AmbientOcclusionTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32G32B32A32_SFloat, xrInstancing: true,  useDynamicScale: true, enableRandomWrite: true, name: "Occlusion texture");
                m_BentNormalTex = RTHandles.Alloc(Vector2.one * scaleFactor, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8G8B8A8_UNorm, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Bent normal tex");
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

            m_AmbientOcclusionTex = RTHandles.Alloc(Vector2.one * 0.5f, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32G32B32A32_SFloat, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Occlusion texture");
            m_BentNormalTex = RTHandles.Alloc(Vector2.one * 0.5f, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R8G8B8A8_UNorm, xrInstancing: true, useDynamicScale: true, enableRandomWrite: true, name: "Bent normal tex");
        }

        public void Render(CommandBuffer cmd, HDCamera camera, SharedRTManager sharedRTManager, ComputeBuffer depthPyramidOffsets)
        {
            // Grab current settings
            var settings = VolumeManager.instance.stack.GetComponent<GTAO>();

            //if (!IsActive(camera, settings))
            //    return;

            EnsureRTSize(settings);

            // TODO: tmp remove me
            HDUtils.SetRenderTarget(cmd, m_AmbientOcclusionTex, sharedRTManager.GetDepthStencilBuffer(false), ClearFlag.Color, Color.black);

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

            Vector4 aoParams0 = new Vector4(
                settings.fullRes.value ? 0.0f : 1.0f,
                runningRes.y * (camera.mainViewConstants.projMatrix[1,1] * -0.5f) * 0.5f,
                settings.radius.value,
                settings.stepCount.value
                );

            var cs = m_Resources.shaders.GTAOCS;
            var kernel = cs.FindKernel("GTAOMain");

            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOBufferSize, aoBufferInfo);
            cmd.SetComputeVectorParam(cs, HDShaderIDs._AOParams0, aoParams0);

            HDUtils.PackedMipChainInfo info = sharedRTManager.GetDepthBufferMipChainInfo();
            cmd.SetComputeBufferParam(cs, kernel, HDShaderIDs._DepthPyramidMipLevelOffsets, info.GetOffsetBufferData(depthPyramidOffsets));

            cmd.SetComputeTextureParam(cs, kernel, HDShaderIDs._OcclusionTexture, m_AmbientOcclusionTex);

            const int groupSizeX = 8;
            const int groupSizeY = 8;
            int threadGroupX = ((int)runningRes.x + (groupSizeX - 1)) / groupSizeX;
            int threadGroupY = ((int)runningRes.y + (groupSizeY - 1)) / groupSizeY;
            using (new ProfilingSample(cmd, "TEST_GTAO", CustomSamplerId.MotionBlurKernel.GetSampler()))
            {
                cmd.DispatchCompute(cs, kernel, threadGroupX, threadGroupY, camera.computePassCount);

            }

        }

    }
}
