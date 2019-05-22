Shader "Hidden/HDRP/Sky/PbrSky"
{
    HLSLINCLUDE

    #pragma vertex Vert

    #pragma enable_d3d11_debug_symbols
    #pragma target 4.5
    #pragma only_renderers d3d11 ps4 xboxone vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PbrSky/PbrSkyCommon.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/SkyUtils.hlsl"

    float4x4 _PixelCoordToViewDirWS;     // Actually just 3x3, but Unity can only set 4x4

    int _HasGroundAlbedoTexture;    // bool...
    int _HasGroundEmissionTexture;  // bool...
    int _HasSpaceEmissionTexture;   // bool...
    int _NumLights;

    float4 _LightDirections[2];
    float4 _LightRadianceValues[2];

    // 3x3, but Unity can only set 4x4...
    float4x4 _PlanetRotation;
    float4x4 _SpaceRotation;

    TEXTURECUBE(_GroundAlbedoTexture);
    TEXTURECUBE(_GroundEmissionTexture);
    TEXTURECUBE(_SpaceEmissionTexture);

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID, UNITY_RAW_FAR_CLIP_VALUE);
        return output;
    }

    float4 RenderSky(Varyings input)
    {
        const float  A = _AtmosphericRadius;
        const float  R = _PlanetaryRadius;
        const float3 C = _PlanetCenterPosition;
        const float3 O = _WorldSpaceCameraPos * 0.001; // Convert m to km

        // Convention:
        // V points towards the camera.
        // The normal vector N points upwards (local Z).
        // The view vector V and the normal vector N span the local X-Z plane.
        // The light vector is represented as {phiL, cosThataL}.
        float3 V = GetSkyViewDirWS(input.positionCS.xy, (float3x3)_PixelCoordToViewDirWS);
        float3 P = O - C;
        float3 N = normalize(P);
        float  r = max(length(P), R); // Must not be inside the planet

        bool rayIntersectsAtmosphere = true;

        if (r <= A)
        {
            // We are inside the atmosphere.
        }
        else
        {
            // We are observing the planet from space.
            float t = IntersectSphere(A, dot(N, -V), r).x; // Min root

            if (t >= 0)
            {
                // It's in the view.
                P = P + t * -V;
                N = normalize(P);
                r = A;
            }
            else
            {
                rayIntersectsAtmosphere = false;
            }
        }

        float NdotV  = dot(N, V);
        float cosChi = -NdotV;
        float height = r - R;
        float cosHor = GetCosineOfHorizonZenithAngle(height);

        bool lookAboveHorizon = (cosChi > cosHor);

        float3 gN = 0, gBrdf = 0, transm = 1;

        if (rayIntersectsAtmosphere)
        {
            float3 oDepth = SampleOpticalDepthTexture(cosChi, height, lookAboveHorizon);
                   transm = TransmittanceFromOpticalDepth(oDepth);

            if (!lookAboveHorizon) // See the ground?
            {
                float  t  = IntersectSphere(R, cosChi, r).x;
                float3 gP = P + t * -V;
                       gN = normalize(gP);

                float3 albedo;

                if (_HasGroundAlbedoTexture)
                {
                    albedo = SAMPLE_TEXTURECUBE(_GroundAlbedoTexture, s_trilinear_clamp_sampler, mul(N, (float3x3)_PlanetRotation));
                }
                else
                {
                    albedo = _GroundAlbedo;
                }

                gBrdf = INV_PI * albedo;
            }
        }

        float3 totalRadiance = 0;

        for (int i = 0; i < min(_NumLights, 2); i++)
        {
            float3 L             = _LightDirections[i].xyz;
            float3 lightRadiance = _LightRadianceValues[i].rgb;

            // TODO: solve in spherical coords?
            float  NdotL = dot(N, L);
            float3 projL = L - N * NdotL;
            float3 projV = V - N * NdotV;
            float  phiL  = acos(clamp(dot(projL, projV) * rsqrt(max(dot(projL, projL) * dot(projV, projV), FLT_EPS)), -1, 1));

            TexCoord4D tc = ConvertPositionAndOrientationToTexCoords(height, NdotV, NdotL, phiL);

            if (rayIntersectsAtmosphere)
            {
                float3 radiance = 0;

                if (!lookAboveHorizon) // See the ground?
                {
                    float3 irradiance = SampleGroundIrradianceTexture(dot(gN, L));
                    radiance += gBrdf * transm * irradiance;
                }

                // Single scattering does not contain the phase function.
                float LdotV = dot(L, V);

                // Air.
                radiance += lerp(SAMPLE_TEXTURE3D_LOD(_AirSingleScatteringTexture,     s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w0), 0).rgb,
                                 SAMPLE_TEXTURE3D_LOD(_AirSingleScatteringTexture,     s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w1), 0).rgb,
                                 tc.a) * AirPhase(LdotV);

                // Aerosols.
                // TODO: since aerosols are in a separate texture,
                // they could use a different max height value for improved precision.
                radiance += lerp(SAMPLE_TEXTURE3D_LOD(_AerosolSingleScatteringTexture, s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w0), 0).rgb,
                                 SAMPLE_TEXTURE3D_LOD(_AerosolSingleScatteringTexture, s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w1), 0).rgb,
                                 tc.a) * AerosolPhase(LdotV);

                // MS.
                radiance += lerp(SAMPLE_TEXTURE3D_LOD(_MultipleScatteringTexture,      s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w0), 0).rgb,
                                 SAMPLE_TEXTURE3D_LOD(_MultipleScatteringTexture,      s_linear_clamp_sampler, float3(tc.u, tc.v, tc.w1), 0).rgb,
                                 tc.a);

                radiance *= lightRadiance; // Globally scale the intensity

                totalRadiance += radiance;
            }
        }

        float3 emission = 0;

        if (rayIntersectsAtmosphere && !lookAboveHorizon) // See the ground?
        {
            if (_HasGroundEmissionTexture)
            {
                emission = SAMPLE_TEXTURECUBE(_GroundEmissionTexture, s_trilinear_clamp_sampler, mul(N, (float3x3)_PlanetRotation));
            }
        }
        else // See the space?
        {
            if (_HasSpaceEmissionTexture)
            {
                // V points towards the camera.
                emission = SAMPLE_TEXTURECUBE(_SpaceEmissionTexture, s_trilinear_clamp_sampler, mul(-V, (float3x3)_SpaceRotation));
            }
        }

        totalRadiance += transm * emission;

        return float4(totalRadiance, 1.0);
    }

    float4 FragBaking(Varyings input) : SV_Target
    {
        return RenderSky(input);
    }

    float4 FragRender(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float4 color = RenderSky(input);
        color.rgb *= GetCurrentExposureMultiplier();
        return color;
    }

    ENDHLSL

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment FragBaking
            ENDHLSL

        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment FragRender
            ENDHLSL
        }

    }
    Fallback Off
}
