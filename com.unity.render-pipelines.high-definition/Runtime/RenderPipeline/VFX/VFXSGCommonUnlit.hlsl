#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/BuiltinUtilities.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"
#include "Packages/com.unity.shadergraph/ShaderGraphLibrary/Functions.hlsl"

#include "VFXSGCommonHDRP.hlsl"

FragInputForSG InitializeFragStructs(inout FragInputs input, PositionInputs posInput, float3 V, out SurfaceData surfaceData, out BuiltinData builtinData)
{
    return InitializeFragStructsHDRP(input,posInput,V,surfaceData,builtinData);
}

void PostInit(FragInputs input, inout SurfaceData surfaceData, inout BuiltinData builtinData, PositionInputs posInput,float3 bentNormalWS, float alpha, float3 V)
{
    PostInitBuiltinData(V, posInput, surfaceData, builtinData);
}

