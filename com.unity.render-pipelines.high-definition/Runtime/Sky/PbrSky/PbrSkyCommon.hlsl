#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/VolumeRendering.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PbrSky/PbrSkyRenderer.cs.hlsl"

CBUFFER_START(UnityPbrSky)
    // All the entries use km and 1/km units.
    float  _PlanetaryRadius;
    float  _AtmosphericLayerHeight;
    float  _AirDensityFalloff;
    float  _AirScaleHeight;
    float  _AerosolDensityFalloff;
    float  _AerosolScaleHeight;
    float3 _SunRadiance;
    float  _RcpAtmosphericLayerHeight;
    float3 _AirSeaLevelExtinction;
    float  _AerosolSeaLevelExtinction;
CBUFFER_END

TEXTURE2D(_OpticalDepthTexture);
SAMPLER(s_linear_clamp_sampler);

float3 SampleTransmittanceTable(float height, float cosTheta)
{
	// cos(theta) = 1 - 2 * u
	// u = 0.5 - 0.5 * cos(theta)
	// h = pow(v, 1.5) * _AtmosphericLayerHeight
	// v = pow(h / _AtmosphericLayerHeight, 0.66666667)

	float2 coordNDC = float2(0.5 - 0.5 * cosTheta,
							 pow(height * _RcpAtmosphericLayerHeight, 0.66666667));

	float2 optDepth = SAMPLE_TEXTURE2D_LOD(_OpticalDepthTexture, s_linear_clamp_sampler, coordNDC, 0).xy;

	// Compose the optical depth with extinction at the sea level.
	return TransmittanceFromOpticalDepth(optDepth.x * _AirSeaLevelExtinction +
										 optDepth.y * _AerosolSeaLevelExtinction);
}
