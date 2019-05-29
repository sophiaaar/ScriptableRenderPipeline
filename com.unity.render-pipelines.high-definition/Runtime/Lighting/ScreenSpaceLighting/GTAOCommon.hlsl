#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Builtin/BuiltinData.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

CBUFFER_START(GTAOUniformBuffer)
float4 _AOBufferSize;
float4 _AOParams0;     
float4 _AOParams1;     
float4 _AOParams2;
float4 _AODepthToViewParams;
CBUFFER_END

#define _AOBaseResMip  (int)_AOParams0.x
#define _AOFOVCorrection _AOParams0.y
#define _AORadius _AOParams0.z
#define _AOStepCount (uint)_AOParams0.w
#define _AOIntensity _AOParams1.x
#define _AOInvRadiusSq _AOParams1.y
#define _AOTemporalOffsetIdx _AOParams1.z
#define _AOTemporalRotationIdx _AOParams1.w
#define _AOMipOffset _AOParams2.xy
#define _AOInvStepCountPlusOne _AOParams2.z

// If this is set to 0 best quality is achieved when full res, but performance is significantly lower.
// If set to 1, when full res, it may lead to extra aliasing and loss of detail, but still significant higher quality than half res.
#define HALF_RES_DEPTH 0 // Make this an option.

#define MIN_DEPTH_GATHERED 1

float GetDepth(float2 positionSS)
{
#if HALF_RES_DEPTH
    int2 samplePos;
    uint fullRes = (_AOBaseResMip == 0);
    if (fullRes)
    {
        samplePos = _AOMipOffset + positionSS.xy / 2;
    }
    else
    {
        samplePos = _AOMipOffset + positionSS.xy;
    }
    return LOAD_TEXTURE2D_X(_DepthPyramidTexture, samplePos).r;
#else

 #if MIN_DEPTH_GATHERED
    if (_AOBaseResMip == 1)
    {
        float2 localUVs = (positionSS.xy + 0.5f) * _AOBufferSize.zw;
        localUVs = ClampAndScaleUVForBilinear(localUVs, _AOBufferSize.zw);
        float2 offsetInUVs = float2(0.0f, 2.0f / 3.0f);
        localUVs *= float2(0.5f, 1.0f / 3.0f);
        localUVs += offsetInUVs;
        float4 gatheredDepth = GATHER_TEXTURE2D_X(_DepthPyramidTexture, s_point_clamp_sampler, localUVs);
        return min(Min3(gatheredDepth.x, gatheredDepth.y, gatheredDepth.z), gatheredDepth.w);
    }
    else
    {
        return LOAD_TEXTURE2D_X(_DepthPyramidTexture, (_AOMipOffset * _AOBaseResMip) + ((uint2)positionSS.xy)).r;
    }
#else
    return LOAD_TEXTURE2D_X(_DepthPyramidTexture, (_AOMipOffset * _AOBaseResMip) + ((uint2)positionSS.xy)).r;
#endif

#endif
}

float GTAOFastSqrt(float x)
{
    return asfloat(0x1FBD1DF5) + (asint(x) >> 1);
}

float GTAOFastAcos(float x)
{
    float outVal = -0.156583 * abs(x) + HALF_PI;
    outVal *= sqrt(1.0 - abs(x));
    return x >= 0 ? outVal : PI - outVal;
}

// --------------------------------------------
// Output functions
// --------------------------------------------
uint PackAOOutput(float AO, float depth)
{
     // 24 depth,  8 bit AO
    uint packedVal = 0;
    packedVal = BitFieldInsert(0x000000ff, UnpackInt(AO, 8), packedVal);
    packedVal = BitFieldInsert(0xffffff00, UnpackInt(depth, 24) << 8, packedVal);
    return packedVal;
}

void UnpackData(uint data, out float AO, out float depth)
{
    AO = UnpackUIntToFloat(data, 0, 8);
    depth = UnpackUIntToFloat(data, 8, 24);
}

void UnpackGatheredData(uint4 data, out float4 AOs, out float4 depths)
{
    UnpackData(data.x, AOs.x, depths.x);
    UnpackData(data.y, AOs.y, depths.y);
    UnpackData(data.z, AOs.z, depths.z);
    UnpackData(data.w, AOs.w, depths.w);
}
