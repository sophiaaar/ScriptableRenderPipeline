using System;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public class PbrSkyRenderer : SkyRenderer
    {
        [GenerateHLSL]
        public enum PbrSkyConfig
        {
            // 64 KiB
            OpticalDepthTableSizeX        = 128, // <N, X>
            OpticalDepthTableSizeY        = 128, // height

            // Tiny
            GroundIrradianceTableSize     = 128, // <N, L>

            // 32 MiB
            InScatteredRadianceTableSizeX = 128, // <N, V>
            InScatteredRadianceTableSizeY = 32,  // height
            InScatteredRadianceTableSizeZ = 16,  // AzimuthAngle(L) w.r.t. the view vector
            InScatteredRadianceTableSizeW = 64,  // <N, L>
        }

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        int lastPrecomputationParamHash;

        PbrSkySettings               m_Settings;
        // Precomputed data below.
        RTHandleSystem.RTHandle      m_OpticalDepthTable;
        RTHandleSystem.RTHandle      m_GroundIrradianceTable;
        RTHandleSystem.RTHandle[]    m_InScatteredRadianceTables; // Air SS, Aerosol SS, Atmosphere MS, (double-buffer, one is temp)

        static ComputeShader         s_OpticalDepthPrecomputationCS;
        static ComputeShader         s_GroundIrradiancePrecomputationCS;
        static ComputeShader         s_InScatteredRadiancePrecomputationCS;
        static Material              s_PbrSkyMaterial;
        static MaterialPropertyBlock s_PbrSkyMaterialProperties;

        public PbrSkyRenderer(PbrSkySettings settings)
        {
            m_Settings = settings;
        }

        public override void Build()
        {
            var hdrpAsset     = GraphicsSettings.renderPipelineAsset as HDRenderPipelineAsset;
            var hdrpResources = hdrpAsset.renderPipelineResources;

            // Shaders
            s_OpticalDepthPrecomputationCS        = hdrpResources.shaders.opticalDepthPrecomputationCS;
            s_GroundIrradiancePrecomputationCS    = hdrpResources.shaders.groundIrradiancePrecomputationCS;
            s_InScatteredRadiancePrecomputationCS = hdrpResources.shaders.inScatteredRadiancePrecomputationCS;
            s_PbrSkyMaterial                      = CoreUtils.CreateEngineMaterial(hdrpResources.shaders.pbrSkyPS);
            s_PbrSkyMaterialProperties            = new MaterialPropertyBlock();

            Debug.Assert(s_OpticalDepthPrecomputationCS        != null);
            Debug.Assert(s_GroundIrradiancePrecomputationCS    != null);
            Debug.Assert(s_InScatteredRadiancePrecomputationCS != null);

            //var colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            var colorFormat = GraphicsFormat.R32G32B32A32_SFloat;

            // Textures
            m_OpticalDepthTable = RTHandles.Alloc((int)PbrSkyConfig.OpticalDepthTableSizeX,
                                                  (int)PbrSkyConfig.OpticalDepthTableSizeY,
                                                  filterMode: FilterMode.Bilinear,
                                                  colorFormat: GraphicsFormat.R16G16_SFloat,
                                                  enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                  name: "OpticalDepthTable");

            m_GroundIrradianceTable = RTHandles.Alloc((int)PbrSkyConfig.GroundIrradianceTableSize, 1,
                                                      filterMode: FilterMode.Bilinear,
                                                      colorFormat: colorFormat,
                                                      enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                      name: "GroundIrradianceTable");

            m_InScatteredRadianceTables = new RTHandleSystem.RTHandle[4];

            for (int i = 0; i < 4; i++)
            {
                // Emulate a 4D texture with a "deep" 3D texture.
                m_InScatteredRadianceTables[i] = RTHandles.Alloc((int)PbrSkyConfig.InScatteredRadianceTableSizeX,
                                                                 (int)PbrSkyConfig.InScatteredRadianceTableSizeY,
                                                                 (int)PbrSkyConfig.InScatteredRadianceTableSizeZ *
                                                                 (int)PbrSkyConfig.InScatteredRadianceTableSizeW,
                                                                 dimension: TextureDimension.Tex3D,
                                                                 filterMode: FilterMode.Bilinear,
                                                                 colorFormat: colorFormat,
                                                                 enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                                                 name: string.Format("InScatteredRadianceTable{0}", i));

                Debug.Assert(m_InScatteredRadianceTables[i] != null);
            }

            Debug.Assert(m_OpticalDepthTable     != null);
            Debug.Assert(m_GroundIrradianceTable != null);
        }

        public override bool IsValid()
        {
            /* TODO */
            return true;
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
            cmd.SetGlobalVector("_PlanetCenterPosition",      m_Settings.planetCenterPosition.value);
            cmd.SetGlobalVector("_SunRadiance",               m_Settings.sunRadiance.value);
        }

        void PrecomputeTables(CommandBuffer cmd)
        {
            using (new ProfilingSample(cmd, "Optical Depth Precomputation"))
            {
                cmd.SetComputeTextureParam(s_OpticalDepthPrecomputationCS, 0, "_OpticalDepthTable", m_OpticalDepthTable);
                cmd.DispatchCompute(s_OpticalDepthPrecomputationCS, 0, (int)PbrSkyConfig.OpticalDepthTableSizeX / 8, (int)PbrSkyConfig.OpticalDepthTableSizeY / 8, 1);
            }

            using (new ProfilingSample(cmd, "In-Scattered Radiance Precomputation"))
            {
                int numBounces = 2;

                for (int order = 1; order <= numBounces; order++)
                {
                    // For efficiency reasons, multiple scattering is computed in 2 passes:
                    // 1. Gather the in-scattered radiance over the entire sphere of directions.
                    // 2. Accumulate the in-scattered radiance along the ray.
                    // Single scattering only performs the 2nd step.

                    int firstPass = Math.Min(order - 1, 1);
                    int numPasses = Math.Min(order, 2);

                    for (int pass = firstPass; pass < (firstPass + numPasses); pass++)
                    {
                        {
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_OpticalDepthTexture",            m_OpticalDepthTable);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_GroundIrradianceTexture",        m_GroundIrradianceTable);
                        }

                        switch (order)
                        {
                        case 1:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AirSingleScatteringTable",       m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AerosolSingleScatteringTable",   m_InScatteredRadianceTables[1]);
                            break;
                        case 2:
                        if (pass == 0)
                        {
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AirSingleScatteringTexture",     m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AerosolSingleScatteringTexture", m_InScatteredRadianceTables[1]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[3]);
                        }
                        else
                        {
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTexture",      m_InScatteredRadianceTables[3]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[2]);
                        }
                            break;
                        default:
                            break;
                        }

                        // Emulate a 4D dispatch with a "deep" 3D dispatch.
                        cmd.DispatchCompute(s_InScatteredRadiancePrecomputationCS, pass, (int)PbrSkyConfig.InScatteredRadianceTableSizeX / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeY / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeZ / 4 *
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeW);
                    }

                    // Re-illuminate the ground with each bounce.
                    cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_OpticalDepthTexture",   m_OpticalDepthTable);
                    cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_GroundIrradianceTable", m_GroundIrradianceTable);

                    cmd.DispatchCompute(s_GroundIrradiancePrecomputationCS, firstPass, (int)PbrSkyConfig.GroundIrradianceTableSize / 64, 1, 1);
                }
            }
        }

        // 'renderSunDisk' parameter is meaningless and is thus ignored.
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            CommandBuffer cmd = builtinParams.commandBuffer;

            Light sun = builtinParams.sunLight;

            Vector3 L;

            if (sun != null)
            {
                L = -builtinParams.sunLight.transform.forward;
            }
            else
            {
                L = Vector3.zero;
            }
            m_Settings.UpdateParameters(builtinParams);
            int currentParamHash = m_Settings.GetHashCode();

            if (currentParamHash != lastPrecomputationParamHash)
            {
                UpdateSharedConstantBuffer(cmd);
                PrecomputeTables(cmd);

                // lastPrecomputationParamHash = currentParamHash;
            }

            // This matrix needs to be updated at the draw call frequency.
            s_PbrSkyMaterialProperties.SetMatrix( HDShaderIDs._PixelCoordToViewDirWS, builtinParams.pixelCoordToViewDirMatrix);
            s_PbrSkyMaterialProperties.SetVector( "_SunDirection",                    L);
            s_PbrSkyMaterialProperties.SetTexture("_OpticalDepthTexture",             m_OpticalDepthTable);
            s_PbrSkyMaterialProperties.SetTexture("_GroundIrradianceTexture",         m_GroundIrradianceTable);
            s_PbrSkyMaterialProperties.SetTexture("_AirSingleScatteringTexture",      m_InScatteredRadianceTables[0]);
            s_PbrSkyMaterialProperties.SetTexture("_AerosolSingleScatteringTexture",  m_InScatteredRadianceTables[1]);
            s_PbrSkyMaterialProperties.SetTexture("_MultipleScatteringTexture",       m_InScatteredRadianceTables[2]);

            CoreUtils.DrawFullScreen(builtinParams.commandBuffer, s_PbrSkyMaterial, s_PbrSkyMaterialProperties, renderForCubemap ? 0 : 1);
        }
    }
}
