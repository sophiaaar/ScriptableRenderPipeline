using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public class PbrSkyRenderer : SkyRenderer
    {
        PbrSkySettings          m_Settings;
        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        int                     lastPrecomputationParamHash;
        // Precomputed data below.
        RTHandleSystem.RTHandle m_OpticalDepthTable;

        static ComputeShader    s_OpticalDepthTablePrecomputationCS;

        [GenerateHLSL]
        public enum PbrSkyConfig
        {
            OpticalDepthTableSizeX = 64,
            OpticalDepthTableSizeY = 64,
        }

        public PbrSkyRenderer(PbrSkySettings settings)
        {
            m_Settings = settings;
        }

        public override bool IsValid()
        {
            /* TODO */
            return true;
        }

        public override void Build()
        {
            var hdrpAsset     = GraphicsSettings.renderPipelineAsset as HDRenderPipelineAsset;
            var hdrpResources = hdrpAsset.renderPipelineResources;

            // Shaders
            s_OpticalDepthTablePrecomputationCS = hdrpResources.shaders.opticalDepthTablePrecomputationCS;
            Debug.Assert(s_OpticalDepthTablePrecomputationCS != null);

            // Textures
            m_OpticalDepthTable = RTHandles.Alloc((int)PbrSkyConfig.OpticalDepthTableSizeX, (int)PbrSkyConfig.OpticalDepthTableSizeY,
                                                  filterMode: FilterMode.Bilinear, colorFormat: GraphicsFormat.R32G32_SFloat,
                                                  enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                  name: "OpticalDepthTable");
            Debug.Assert(m_OpticalDepthTable != null);
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

        void UpdateSharedConstantBuffer(CommandBuffer cmd)
        {
            cmd.SetGlobalFloat("_PlanetaryRadius",        m_Settings.planetaryRadius             * 1000.0f); // km to m
            cmd.SetGlobalFloat("_AtmosphericLayerHeight", m_Settings.GetAtmosphericLayerHeight() * 1000.0f); // km to m
            cmd.SetGlobalFloat("_AirDensityFalloff",      m_Settings.airDensityFalloff           * 0.001f);  // km^-1 to m^-1
            cmd.SetGlobalFloat("_AirScaleHeight",         1.0f / m_Settings.airDensityFalloff    * 1000.0f); // km to m
            cmd.SetGlobalFloat("_AerosolDensityFalloff",  m_Settings.aerosolDensityFalloff       * 0.001f);  // km^-1 to m^-1
            cmd.SetGlobalFloat("_AerosolScaleHeight",     1.0f / m_Settings.airDensityFalloff    * 1000.0f); // km to m
        }

        void PrecomputeTables(CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, "Optical Depth Table Precomputation"))
            {
                cmd.SetComputeTextureParam(s_OpticalDepthTablePrecomputationCS, 0, "_OpticalDepthTable", m_OpticalDepthTable);
                cmd.DispatchCompute(s_OpticalDepthTablePrecomputationCS, 0, (int)PbrSkyConfig.OpticalDepthTableSizeX / 8, (int)PbrSkyConfig.OpticalDepthTableSizeY / 8, 1);
            }
        }

        // 'renderSunDisk' parameter is meaningless and is thus ignored.
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            CommandBuffer cmd = builtinParams.commandBuffer;

            UpdateSharedConstantBuffer(cmd);

            int currentParamHash = m_Settings.GetHashCode();

            if (currentParamHash != lastPrecomputationParamHash)
            {
                PrecomputeTables(cmd);

                //lastPrecomputationParamHash = currentParamHash;
            }

            /* TODO */
        }
    }
}
