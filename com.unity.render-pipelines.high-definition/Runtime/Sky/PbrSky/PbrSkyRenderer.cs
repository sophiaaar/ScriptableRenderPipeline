using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public class PbrSkyRenderer : SkyRenderer
    {
        [GenerateHLSL]
        public enum PbrSkyConfig
        {
            // 64 KiB
            OpticalDepthTableSizeX        = 128,    // <N, X>
            OpticalDepthTableSizeY        = 128,    // height

            // Tiny
            GroundIrradianceTableSize     = 128,    // <N, L>

            // 32 MiB
            InScatteredRadianceTableSizeX = 64,     // <N, V>
            InScatteredRadianceTableSizeY = 32,     // height
            InScatteredRadianceTableSizeZ = 64,     // <N, L>
            InScatteredRadianceTableSizeW = 16 * 2, // <L, V>, the 1st half is view above the horizon, the 2nd half is below
        }

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        int lastPrecomputationParamHash;

        PbrSkySettings            m_Settings;
        // Precomputed data below.
        RTHandleSystem.RTHandle   m_OpticalDepthTable;
        RTHandleSystem.RTHandle   m_GroundIrradianceTable;
        RTHandleSystem.RTHandle[] m_InScatteredRadianceTable;

        static ComputeShader      s_OpticalDepthPrecomputationCS;
        static ComputeShader      s_GroundIrradiancePrecomputationCS;
        static ComputeShader      s_InScatteredRadiancePrecomputationCS;

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
            s_OpticalDepthPrecomputationCS        = hdrpResources.shaders.opticalDepthPrecomputationCS;
            s_GroundIrradiancePrecomputationCS    = hdrpResources.shaders.groundIrradiancePrecomputationCS;
            s_InScatteredRadiancePrecomputationCS = hdrpResources.shaders.inScatteredRadiancePrecomputationCS;

            Debug.Assert(s_OpticalDepthPrecomputationCS        != null);
            Debug.Assert(s_GroundIrradiancePrecomputationCS    != null);
            Debug.Assert(s_InScatteredRadiancePrecomputationCS != null);

            // Textures
            m_OpticalDepthTable = RTHandles.Alloc((int)PbrSkyConfig.OpticalDepthTableSizeX,
                                                  (int)PbrSkyConfig.OpticalDepthTableSizeY,
                                                  filterMode: FilterMode.Bilinear,
                                                  colorFormat: GraphicsFormat.R16G16_SFloat,
                                                  enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                  name: "OpticalDepthTable");

            m_GroundIrradianceTable = RTHandles.Alloc((int)PbrSkyConfig.GroundIrradianceTableSize, 1,
                                                      filterMode: FilterMode.Bilinear,
                                                      colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                                                      enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                      name: "GroundIrradianceTable");

            m_InScatteredRadianceTable = new RTHandleSystem.RTHandle[(int)PbrSkyConfig.InScatteredRadianceTableSizeW];

            for (int w = 0; w < (int)PbrSkyConfig.InScatteredRadianceTableSizeW; w++)
            {
                m_InScatteredRadianceTable[w] = RTHandles.Alloc((int)PbrSkyConfig.InScatteredRadianceTableSizeX,
                                                                (int)PbrSkyConfig.InScatteredRadianceTableSizeY,
                                                                (int)PbrSkyConfig.InScatteredRadianceTableSizeZ,
                                                                dimension: TextureDimension.Tex3D,
                                                                filterMode: FilterMode.Bilinear,
                                                                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                                                                enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                                name: string.Format("InScatteredRadianceTable{0}", w));
            }

            // Texture validation.
            Debug.Assert(m_OpticalDepthTable     != null);
            Debug.Assert(m_GroundIrradianceTable != null);

            for (int w = 0; w < (int)PbrSkyConfig.InScatteredRadianceTableSizeW; w++)
            {
                Debug.Assert(m_InScatteredRadianceTable[w] != null);
            }
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

        static float CornetteShanksPhasePartConstant(float anisotropy)
        {
            float g = anisotropy;

            return (3.0f / (8.0f * Mathf.PI)) * (1.0f - g * g) / (2.0f + g * g);
        }

        static float UnmapCosineOfLightViewAngle(float u)
        {
            float s = ((0.5f - u) >= 0) ? 1 : -1;
            float p = Mathf.Clamp01(s * (1 - 2 * u));
            float a = Mathf.Pow(p, 0.66666667f);
            float y = s * a;
            float x = 0.5f - 0.5f * y;

            return Mathf.Cos(Mathf.PI * x);
        }

        void UpdateSharedConstantBuffer(CommandBuffer cmd)
        {
            float R = m_Settings.planetaryRadius;
            float H = m_Settings.atmosphericDepth;

            cmd.SetGlobalFloat( "_PlanetaryRadius",           R);
            cmd.SetGlobalFloat( "_RcpPlanetaryRadius",        1.0f / R);
            cmd.SetGlobalFloat( "_AtmosphericDepth",          H);
            cmd.SetGlobalFloat( "_RcpAtmosphericDepth",       1.0f / H);

            cmd.SetGlobalFloat( "_PlanetaryRadiusSquared",    (R * R));
            cmd.SetGlobalFloat( "_AtmosphericRadiusSquared",  (R + H) * (R + H));
            cmd.SetGlobalFloat( "_AerosolAnisotropy",         m_Settings.aerosolAnisotropy);
            cmd.SetGlobalFloat( "_AerosolPhasePartConstant",  CornetteShanksPhasePartConstant(m_Settings.aerosolAnisotropy));

            cmd.SetGlobalFloat( "_AirDensityFalloff",         m_Settings.airDensityFalloff);
            cmd.SetGlobalFloat( "_AirScaleHeight",            1.0f / m_Settings.airDensityFalloff);
            cmd.SetGlobalFloat( "_AerosolDensityFalloff",     m_Settings.aerosolDensityFalloff);
            cmd.SetGlobalFloat( "_AerosolScaleHeight",        1.0f / m_Settings.airDensityFalloff);

            cmd.SetGlobalVector("_AirSeaLevelExtinction",     m_Settings.airThickness.value     * 0.001f); // Convert to 1/km
            cmd.SetGlobalFloat( "_AerosolSeaLevelExtinction", m_Settings.aerosolThickness.value * 0.001f); // Convert to 1/km

            cmd.SetGlobalVector("_AirSeaLevelScattering",     m_Settings.airAlbedo.value     * m_Settings.airThickness.value     * 0.001f); // Convert to 1/km
            cmd.SetGlobalFloat( "_AerosolSeaLevelScattering", m_Settings.aerosolAlbedo.value * m_Settings.aerosolThickness.value * 0.001f); // Convert to 1/km

            cmd.SetGlobalVector("_GroundAlbedo",              m_Settings.groundColor.value);

            cmd.SetGlobalVector("_SunRadiance",               m_Settings.sunRadiance.value);
            cmd.SetGlobalVector("_SunDirection",              m_Settings.sunDirection.value);
        }

        void PrecomputeTables(CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, "Optical Depth Precomputation"))
            {
                cmd.SetComputeTextureParam(s_OpticalDepthPrecomputationCS, 0, "_OpticalDepthTable", m_OpticalDepthTable);
                cmd.DispatchCompute(s_OpticalDepthPrecomputationCS, 0, (int)PbrSkyConfig.OpticalDepthTableSizeX / 8, (int)PbrSkyConfig.OpticalDepthTableSizeY / 8, 1);
            }

            using (new ProfilingSample(cmd, "Ground Irradiance Precomputation"))
            {
                cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, 0, "_OpticalDepthTexture",   m_OpticalDepthTable);
                cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, 0, "_GroundIrradianceTable", m_GroundIrradianceTable);
                cmd.DispatchCompute(s_GroundIrradiancePrecomputationCS, 0, (int)PbrSkyConfig.GroundIrradianceTableSize / 64, 1, 1);
            }

            using (new ProfilingSample(cmd, "InScattered Radiance Precomputation"))
            {
                cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, 0, "_OpticalDepthTexture", m_OpticalDepthTable);

                const int numBounces = 0;
                for (int i = 0; i <= numBounces; i++)
                {
                    cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, 0, "_GroundIrradianceTexture", m_GroundIrradianceTable);

                    for (int w = 0; w < (int)PbrSkyConfig.InScatteredRadianceTableSizeW; w++)
                    {
                        bool aboveHorizon = w < ((int)PbrSkyConfig.InScatteredRadianceTableSizeW / 2);

                        int   k     = w % ((int)PbrSkyConfig.InScatteredRadianceTableSizeW / 2);
                        float u     = k / (float)((int)PbrSkyConfig.InScatteredRadianceTableSizeW / 2);
                        float LdotV = UnmapCosineOfLightViewAngle(u);

                        cmd.SetComputeFloatParam(s_InScatteredRadiancePrecomputationCS, "_LdotV", LdotV);
                        cmd.SetComputeFloatParam(s_InScatteredRadiancePrecomputationCS, "_AboveHorizon", aboveHorizon ? 1.0f : 0.0f);
                        cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, 0, "_InScatteredRadianceTable", m_InScatteredRadianceTable[w]);
                        cmd.DispatchCompute(s_InScatteredRadiancePrecomputationCS, 0, (int)PbrSkyConfig.InScatteredRadianceTableSizeX / 4,
                                                                                      (int)PbrSkyConfig.InScatteredRadianceTableSizeY / 4,
                                                                                      (int)PbrSkyConfig.InScatteredRadianceTableSizeZ / 4);
                    }
                }
            }

        }

        // 'renderSunDisk' parameter is meaningless and is thus ignored.
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            CommandBuffer cmd = builtinParams.commandBuffer;

            m_Settings.UpdateParameters(builtinParams);
            int currentParamHash = m_Settings.GetHashCode();

            if (currentParamHash != lastPrecomputationParamHash)
            {
                UpdateSharedConstantBuffer(cmd);
                PrecomputeTables(cmd);

                // lastPrecomputationParamHash = currentParamHash;
            }

            /* TODO */
        }
    }
}
