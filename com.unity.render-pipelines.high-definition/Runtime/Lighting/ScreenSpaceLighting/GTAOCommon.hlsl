#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Builtin/BuiltinData.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// TODOs:
// - Consider interleaving. 
// - Ask chris:
//      - Kept at half res or upsampled?
//      -
// - Can we have a filter minimum sampler?
// - Have 2 modes: half res and full res.


StructuredBuffer<int2>  _DepthPyramidMipLevelOffsets;

CBUFFER_START(GTAOUniformBuffer)
float4 _AOBufferSize;  // xy: buffer size, zw: texel size       // X: Contains pow at the upsampling stage.
float4 _AOParams0;     // x: mip level of the base AO (0 if full res, 1 if half res).  y: Half proj scale
float4 _AOParams1;     // x: mip level of the base AO (0 if full res, 1 if half res).  y: Half proj scale
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

float GetDepth(float2 positionSS, int offset)
{
    uint finalMip = _AOBaseResMip + offset;
    int2 mipOffset = _DepthPyramidMipLevelOffsets[finalMip];
    return LOAD_TEXTURE2D_X(_DepthPyramidTexture, mipOffset + ((uint2)positionSS.xy << offset)).r;
}


// TODO_FCC: Use ours?
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
uint PackOutputToDenoise(float AO, float depth)
{
    // BitFieldInsert(uint mask, uint src, uint dst)
     // 24 depth,  8 bit AO
    float linearDepth = LinearEyeDepth(depth, _ZBufferParams);;
    uint packedVal = 0;
    packedVal = BitFieldInsert(0xffffff00, UnpackInt(linearDepth, 24), packedVal);
    packedVal = BitFieldInsert(0x000000ff, UnpackInt(AO, 8), packedVal);
    return packedVal;
}
