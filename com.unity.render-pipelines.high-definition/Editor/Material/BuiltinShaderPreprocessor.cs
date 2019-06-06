using System.Collections.Generic;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.HDPipeline;

namespace UnityEditor.Experimental.Rendering.HDPipeline
{
    public class BuiltinShaderPreprocessor : BaseShaderPreprocessor
    {
        public BuiltinShaderPreprocessor() {}

        public override bool ShadersStripper(HDRenderPipelineAsset hdrpAsset, Shader shader, ShaderSnippetData snippet, ShaderCompilerData inputData)
        {
            bool isDistortionPass = snippet.passName == "DistortionVectors";
            bool isTransparentBackface = snippet.passName == "TransparentBackface";
            bool isTransparentPostpass = snippet.passName == "TransparentDepthPostpass";
            bool isTransparentPrepass = snippet.passName == "TransparentDepthPrepass";
            bool isForwardPass = snippet.passName == "Forward";
            bool isDepthOnlyPass = snippet.passName == "DepthOnly";
            bool isMotionPass = snippet.passName == "MotionVectors";
            bool isTransparentForwardPass = isTransparentPostpass || isTransparentBackface || isTransparentPrepass || isDistortionPass;

            if (!inputData.shaderKeywordSet.IsEnabled(m_Transparent)) // Opaque
            {
                // If opaque, we never need transparent specific passes (even in forward only mode)
                if (isTransparentForwardPass)
                    return true;

                if (hdrpAsset.currentPlatformRenderPipelineSettings.supportedLitShaderMode == RenderPipelineSettings.SupportedLitShaderMode.DeferredOnly)
                {
                    // When we are in deferred, we only support tile lighting
                    if (inputData.shaderKeywordSet.IsEnabled(m_ClusterLighting))
                        return true;
                    
                    if (isForwardPass && !inputData.shaderKeywordSet.IsEnabled(m_DebugDisplay))
                        return true;
                }

                // TODO: Should we remove Cluster version if we know MSAA is disabled ? This prevent to manipulate LightLoop Settings (useFPTL option)
                // For now comment following code
                // if (inputData.shaderKeywordSet.IsEnabled(m_ClusterLighting) && !hdrpAsset.currentPlatformRenderPipelineSettings.supportMSAA)
                //    return true;
            }

            // We strip passes for transparent passes outside of isBuiltInLit because we want Hair, Fabric
            // and StackLit shader graphs to be taken in account.
            if (inputData.shaderKeywordSet.IsEnabled(m_Transparent))
            {
                // If transparent we don't need the depth only pass
                if (isDepthOnlyPass)
                    return true;

                // If transparent we don't need the motion vector pass
                if (isMotionPass)
                    return true;

                // If we are transparent we use cluster lighting and not tile lighting
                if (inputData.shaderKeywordSet.IsEnabled(m_TileLighting))
                    return true;
            }

            return false;
        }
    }
}
