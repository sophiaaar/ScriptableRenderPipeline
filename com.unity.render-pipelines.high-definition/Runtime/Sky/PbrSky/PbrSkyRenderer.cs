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
            GroundIrradianceTableSize     = 256, // <N, L>

            // 32 MiB
            InScatteredRadianceTableSizeX = 128, // <N, V>
            InScatteredRadianceTableSizeY = 32,  // height
            InScatteredRadianceTableSizeZ = 16,  // AzimuthAngle(L) w.r.t. the view vector
            InScatteredRadianceTableSizeW = 64,  // <N, L>
        }

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        int m_LastPrecomputationParamHash;

        // We compute at most one bounce per frame for perf reasons.
        int m_LastPrecomputedBounce;

        PbrSkySettings               m_Settings;
        // Precomputed data below.
        RTHandleSystem.RTHandle      m_OpticalDepthTable;
        RTHandleSystem.RTHandle[]    m_GroundIrradianceTables;    // All orders, one order
        RTHandleSystem.RTHandle[]    m_InScatteredRadianceTables; // Air SS, Aerosol SS, Atmosphere MS, Atmosphere one order, Temp

        static ComputeShader         s_OpticalDepthPrecomputationCS;
        static ComputeShader         s_GroundIrradiancePrecomputationCS;
        static ComputeShader         s_InScatteredRadiancePrecomputationCS;
        static Material              s_PbrSkyMaterial;
        static MaterialPropertyBlock s_PbrSkyMaterialProperties;

        static GraphicsFormat s_ColorFormat = GraphicsFormat.R16G16B16A16_SFloat;

        RTHandleSystem.RTHandle AllocateOpticalDepthTable()
        {
            var table = RTHandles.Alloc((int)PbrSkyConfig.OpticalDepthTableSizeX,
                                        (int)PbrSkyConfig.OpticalDepthTableSizeY,
                                        filterMode: FilterMode.Bilinear,
                                        colorFormat: GraphicsFormat.R16G16_SFloat,
                                        enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                        name: "OpticalDepthTable");

            Debug.Assert(table != null);

            return table;
        }

        RTHandleSystem.RTHandle AllocateGroundIrradianceTable(int index)
        {
            var table = RTHandles.Alloc((int)PbrSkyConfig.GroundIrradianceTableSize, 1,
                                        filterMode: FilterMode.Bilinear,
                                        colorFormat: s_ColorFormat,
                                        enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                        name: string.Format("GroundIrradianceTable{0}", index));

            Debug.Assert(table != null);

            return table;
        }

        RTHandleSystem.RTHandle AllocateInScatteredRadianceTable(int index)
        {
            // Emulate a 4D texture with a "deep" 3D texture.
            var table = RTHandles.Alloc((int)PbrSkyConfig.InScatteredRadianceTableSizeX,
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeY,
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeZ *
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeW,
                                        dimension: TextureDimension.Tex3D,
                                        filterMode: FilterMode.Bilinear,
                                        colorFormat: s_ColorFormat,
                                        enableRandomWrite: true, xrInstancing: false, useDynamicScale: false,
                                        name: string.Format("InScatteredRadianceTable{0}", index));

            Debug.Assert(table != null);

            return table;
        }

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

            // Textures
            m_OpticalDepthTable = AllocateOpticalDepthTable();

            // No temp tables.
            m_GroundIrradianceTables       = new RTHandleSystem.RTHandle[2];
            m_GroundIrradianceTables[0]    = AllocateGroundIrradianceTable(0);

            m_InScatteredRadianceTables    = new RTHandleSystem.RTHandle[5];
            m_InScatteredRadianceTables[0] = AllocateInScatteredRadianceTable(0);
            m_InScatteredRadianceTables[1] = AllocateInScatteredRadianceTable(1);
            m_InScatteredRadianceTables[2] = AllocateInScatteredRadianceTable(2);
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
            float H = m_Settings.ComputeAtmosphericDepth();

            cmd.SetGlobalFloat( "_PlanetaryRadius",           R);
            cmd.SetGlobalFloat( "_RcpPlanetaryRadius",        1.0f / R);
            cmd.SetGlobalFloat( "_AtmosphericDepth",          H);
            cmd.SetGlobalFloat( "_RcpAtmosphericDepth",       1.0f / H);

            cmd.SetGlobalFloat( "_AtmosphericRadius",         R + H);
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
        }

        void PrecomputeTables(CommandBuffer cmd)
        {
            if (m_LastPrecomputedBounce == 0)
            {
                // Only needs to be done once.
                using (new ProfilingSample(cmd, "Optical Depth Precomputation"))
                {
                    cmd.SetComputeTextureParam(s_OpticalDepthPrecomputationCS, 0, "_OpticalDepthTable", m_OpticalDepthTable);
                    cmd.DispatchCompute(s_OpticalDepthPrecomputationCS, 0, (int)PbrSkyConfig.OpticalDepthTableSizeX / 8, (int)PbrSkyConfig.OpticalDepthTableSizeY / 8, 1);
                }
            }

            using (new ProfilingSample(cmd, "In-Scattered Radiance Precomputation"))
            {
                //for (int order = 1; order <= m_Settings.numBounces; order++)
                int order = m_LastPrecomputedBounce + 1;
                {
                    // For efficiency reasons, multiple scattering is computed in 2 passes:
                    // 1. Gather the in-scattered radiance over the entire sphere of directions.
                    // 2. Accumulate the in-scattered radiance along the ray.
                    // Single scattering performs both steps during the same pass.

                    int firstPass = Math.Min(order - 1, 2);
                    int accumPass = 3;
                    int numPasses = Math.Min(order, 2);

                    for (int i = 0; i < numPasses; i++)
                    {
                        int pass = (i == 0) ? firstPass : accumPass;

                        {
                            // Used by all passes.
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_OpticalDepthTexture",            m_OpticalDepthTable);
                        }

                        switch (pass)
                        {
                        case 0:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AirSingleScatteringTable",       m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AerosolSingleScatteringTable",   m_InScatteredRadianceTables[1]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[2]); // MS orders
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTableOrder",   m_InScatteredRadianceTables[3]); // One order
                            break;
                        case 1:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AirSingleScatteringTexture",     m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_AerosolSingleScatteringTexture", m_InScatteredRadianceTables[1]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_GroundIrradianceTexture",        m_GroundIrradianceTables[1]);    // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[4]); // Temp
                            break;
                        case 2:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTexture",      m_InScatteredRadianceTables[3]); // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_GroundIrradianceTexture",        m_GroundIrradianceTables[1]);    // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[4]); // Temp
                            break;
                        case 3:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTexture",      m_InScatteredRadianceTables[4]); // Temp
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTableOrder",   m_InScatteredRadianceTables[3]); // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, "_MultipleScatteringTable",        m_InScatteredRadianceTables[2]); // MS orders
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                        }

                        // Re-illuminate the sky with each bounce.
                        // Emulate a 4D dispatch with a "deep" 3D dispatch.
                        cmd.DispatchCompute(s_InScatteredRadiancePrecomputationCS, pass, (int)PbrSkyConfig.InScatteredRadianceTableSizeX / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeY / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeZ / 4 *
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeW);
                    }

                    {
                        // Used by all passes.
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_GroundIrradianceTable",          m_GroundIrradianceTables[0]); // All orders
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_GroundIrradianceTableOrder",     m_GroundIrradianceTables[1]); // One order
                    }

                    switch (firstPass)
                    {
                    case 0:
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_OpticalDepthTexture",            m_OpticalDepthTable);
                        break;
                    case 1:
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_AirSingleScatteringTexture",     m_InScatteredRadianceTables[0]);
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_AerosolSingleScatteringTexture", m_InScatteredRadianceTables[1]);
                        break;
                    case 2:
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, "_MultipleScatteringTexture",      m_InScatteredRadianceTables[3]); // One order
                        break;
                    default:
                        Debug.Assert(false);
                        break;
                    }

                    // Re-illuminate the ground with each bounce.
                    cmd.DispatchCompute(s_GroundIrradiancePrecomputationCS, firstPass, (int)PbrSkyConfig.GroundIrradianceTableSize / 64, 1, 1);
                }
            }
        }

        // 'renderSunDisk' parameter is meaningless and is thus ignored.
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            CommandBuffer cmd = builtinParams.commandBuffer;
            UpdateSharedConstantBuffer(cmd);

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

            int currentParamHash = m_Settings.GetHashCode();

            if (currentParamHash != m_LastPrecomputationParamHash)
            {
                // Hash does not match, have to restart the precomputation from scratch.
                m_LastPrecomputedBounce = 0;
            }

            if (m_LastPrecomputedBounce == 0)
            {
                // Allocate temp tables.
                m_GroundIrradianceTables[1]    = AllocateGroundIrradianceTable(1);
                m_InScatteredRadianceTables[3] = AllocateInScatteredRadianceTable(3);
                m_InScatteredRadianceTables[4] = AllocateInScatteredRadianceTable(4);
            }

            if (m_LastPrecomputedBounce < m_Settings.numBounces)
            {
                PrecomputeTables(cmd);
                m_LastPrecomputedBounce++;
            }

            if (m_LastPrecomputedBounce == m_Settings.numBounces)
            {
                // Free temp tables.
                m_GroundIrradianceTables[1]    = null;
                m_InScatteredRadianceTables[3] = null;
                m_InScatteredRadianceTables[4] = null;
            }

            // Update the hash for the current bounce.
            m_LastPrecomputationParamHash = currentParamHash;

            Color sunRadiance = sun.color.linear * sun.intensity;

            var sunData = sun.GetComponent<HDAdditionalLightData>();

            if (sunData != null && sunData.useColorTemperature)
            {
               sunRadiance *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            }

            // This matrix needs to be updated at the draw call frequency.
            s_PbrSkyMaterialProperties.SetMatrix( HDShaderIDs._PixelCoordToViewDirWS, builtinParams.pixelCoordToViewDirMatrix);
            s_PbrSkyMaterialProperties.SetVector( "_SunDirection",                    L);
            s_PbrSkyMaterialProperties.SetVector( "_SunRadiance",                     sunRadiance);
            s_PbrSkyMaterialProperties.SetTexture("_OpticalDepthTexture",             m_OpticalDepthTable);
            s_PbrSkyMaterialProperties.SetTexture("_GroundIrradianceTexture",         m_GroundIrradianceTables[0]);
            s_PbrSkyMaterialProperties.SetTexture("_AirSingleScatteringTexture",      m_InScatteredRadianceTables[0]);
            s_PbrSkyMaterialProperties.SetTexture("_AerosolSingleScatteringTexture",  m_InScatteredRadianceTables[1]);
            s_PbrSkyMaterialProperties.SetTexture("_MultipleScatteringTexture",       m_InScatteredRadianceTables[2]);

            if (m_Settings.groundTexture.value != null)
            {
                s_PbrSkyMaterialProperties.SetFloat("_HasGroundTexture", 1);
                s_PbrSkyMaterialProperties.SetTexture("_GroundTexture", m_Settings.groundTexture.value);
            }
            else
            {
                s_PbrSkyMaterialProperties.SetFloat("_HasGroundTexture", 0);
            }

            CoreUtils.DrawFullScreen(builtinParams.commandBuffer, s_PbrSkyMaterial, s_PbrSkyMaterialProperties, renderForCubemap ? 0 : 1);
        }
    }
}
