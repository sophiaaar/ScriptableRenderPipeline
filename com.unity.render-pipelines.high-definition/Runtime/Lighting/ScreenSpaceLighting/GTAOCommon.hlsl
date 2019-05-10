#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Builtin/BuiltinData.hlsl"

// TODOs:
// - Consider interleaving. 
// - Ask chris:
//      - Kept at half res or upsampled?
//      -
// - Can we have a filter minimum sampler?
// - Have 2 modes: half res and full res.


StructuredBuffer<int2>  _DepthPyramidMipLevelOffsets;

CBUFFER_START(GTAOUniformBuffer)
float4 _AOBufferSize;  // xy: buffer size, zw: texel size
float4 _AOParams0;     // x: mip level of the base AO (0 if full res, 1 if half res).  y: Half proj scale
CBUFFER_END

#define _AOBaseResMip  (int)_AOParams0.x
#define _AOFOVCorrection _AOParams0.y
#define _AORadius _AOParams0.z
#define _AOStepCount (uint)_AOParams0.w


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
