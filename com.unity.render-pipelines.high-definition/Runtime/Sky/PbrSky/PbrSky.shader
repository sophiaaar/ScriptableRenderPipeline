Shader "Hidden/HDRP/Sky/RenderPbrSky"
{
    HLSLINCLUDE

    #pragma vertex Vert

    #pragma target 4.5
    #pragma only_renderers d3d11 ps4 xboxone vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PbrSky/PbrSkyCommon.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/SkyUtils.hlsl"

    float4x4 _PixelCoordToViewDirWS; // Actually just 3x3, but Unity can only set 4x4
    float3   _SunDirection;
    float3   _PlanetCenterPosition;

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
        const uint n = PBRSKYCONFIG_IN_SCATTERED_RADIANCE_TABLE_SIZE_W / 2;

        float3 L = _SunDirection;
        float3 V = GetSkyViewDirWS(input.positionCS.xy, (float3x3)_PixelCoordToViewDirWS);
        float3 O = _WorldSpaceCameraPos;
        float3 C = _PlanetCenterPosition;
        float3 P = O - C;
        float3 N = normalize(P);
        float  h = max(0, length(P) - _PlanetaryRadius); // Must not be inside the planet

        if (h <= _AtmosphericDepth)
        {
            // We are inside the atmosphere.
        }
        else
        {
            // We are observing the planet from space.
            float t = IntersectAtmosphereFromOutside(-dot(N, V), h);

            if (t >= 0)
            {
                // It's in the view.
                P = (O - C) - t * V;
                N = normalize(P);
                h = _AtmosphericDepth;
            }
            else
            {
                return float4(0, 0, 0, 1);
            }
        }

        float NdotL = dot(N, L);
        float NdotV = dot(N, V);
        float LdotV = dot(L, V);

        float u = MapAerialPerspective(NdotV, h).x;
        float v = MapAerialPerspective(NdotV, h).y;
        float s = MapAerialPerspective(NdotV, h).z;
        float t = MapCosineOfZenithAngle(NdotL);
        float k = (n - 1) * MapCosineOfLightViewAngle(LdotV); // Index

        const uint zTexSize  = PBRSKYCONFIG_IN_SCATTERED_RADIANCE_TABLE_SIZE_Z;
        const uint zTexCount = PBRSKYCONFIG_IN_SCATTERED_RADIANCE_TABLE_SIZE_W;
        // We have (2 * n) NdotL textures along the Z dimension.
        // We treat them as separate textures (slices) and must NOT leak between them.
        t = clamp(t, 0 - 0.5 * rcp(zTexSize),
                     1 - 0.5 * rcp(zTexSize));

        // Shrink by the 'zTexCount' and offset according to the above/below horizon direction and LdotV.
        float w0 = t * rcp(zTexCount) + 0.5 * (1 - s) + 0.5 * (floor(k) * rcp(n));
        float w1 = t * rcp(zTexCount) + 0.5 * (1 - s) + 0.5 * (ceil(k)  * rcp(n));

        float3 radiance = lerp(SAMPLE_TEXTURE3D(_InScatteredRadianceTexture, s_linear_clamp_sampler, float3(u, v, w0)),
                               SAMPLE_TEXTURE3D(_InScatteredRadianceTexture, s_linear_clamp_sampler, float3(u, v, w1)),
                               frac(k));

        return float4(radiance, 1.0);
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
