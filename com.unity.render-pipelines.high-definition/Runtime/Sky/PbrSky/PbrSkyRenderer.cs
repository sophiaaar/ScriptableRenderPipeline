using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public class PbrSkyRenderer : SkyRenderer
    {
        PbrSkySettings          m_Settings;
        RTHandleSystem.RTHandle m_TransmittanceTable;

        static ComputeShader    s_TransmittancePrecomputationCS;

        [GenerateHLSL]
        public enum PbrSkyConfig
        {
            TransmittanceTableSizeX = 64,
            TransmittanceTableSizeY = 64,
        }

        public PbrSkyRenderer(PbrSkySettings settings)
        {
            m_Settings = settings;
        }

        public override bool IsValid()
        {
            /* TODO */
            return false;
        }

        public override void Build()
        {
            var hdrpAsset     = GraphicsSettings.renderPipelineAsset as HDRenderPipelineAsset;
            var hdrpResources = hdrpAsset.renderPipelineResources;

            // Shaders
            s_TransmittancePrecomputationCS = hdrpResources.shaders.transmittancePrecomputationCS;

            // Textures
            m_TransmittanceTable = RTHandles.Alloc((int)PbrSkyConfig.TransmittanceTableSizeX, (int)PbrSkyConfig.TransmittanceTableSizeY,
                                                   filterMode: FilterMode.Bilinear, colorFormat: GraphicsFormat.R16G16_UNorm,
                                                   enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                   name: "TransmittanceTable");
        }

        public override void Cleanup()
        {
            /* TODO */
        }

        public override void SetRenderTargets(BuiltinSkyParameters builtinParams)
        {
            /* TODO: why is this overridable? */

            if (builtinParams.depthBuffer == BuiltinSkyParameters.nullRT)
            {
                HDUtils.SetRenderTarget(builtinParams.commandBuffer, builtinParams.hdCamera, builtinParams.colorBuffer);
            }
            else
            {
                HDUtils.SetRenderTarget(builtinParams.commandBuffer, builtinParams.hdCamera, builtinParams.colorBuffer, builtinParams.depthBuffer);
            }
        }

        // 'renderSunDisk' parameter is meaningless and is thus ignored.
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            /* TODO */
        }
    }
}
