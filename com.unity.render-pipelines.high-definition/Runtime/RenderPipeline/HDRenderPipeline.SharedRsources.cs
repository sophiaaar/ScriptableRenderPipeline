using System;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline
    {
        // The render target used when we do not support MSAA
        RenderGraphMutableResource    m_DepthBuffer;
        RenderGraphMutableResource    m_NormalBuffer;
        RenderGraphMutableResource    m_MotionVectorsBuffer;

        RenderGraphMutableResource    m_DepthBufferMipChain;
        HDUtils.PackedMipChainInfo    m_DepthBufferMipChainInfo; // This is metadata

        // MSAA Render targets
        RenderGraphMutableResource    m_DepthBufferMSAA;
        RenderGraphMutableResource    m_NormalBufferMSAA;
        RenderGraphMutableResource    m_MotionVectorsBufferMSAA;
        // This texture must be used because reading directly from an MSAA Depth buffer is way to expensive. The solution that we went for is writing the depth in an additional color buffer (10x cheaper to solve on ps4)
        RenderGraphMutableResource    m_DepthAsColorBufferMSAA;

        Vector2Int ComputeDepthBufferMipChainSize(Vector2Int screenSize)
        {
            m_DepthBufferMipChainInfo.ComputePackedMipChainInfo(screenSize);
            return m_DepthBufferMipChainInfo.textureSize;
        }

        void InitializeSharedResources(HDRenderPipelineAsset hdAsset)
        {
            m_DepthBufferMipChainInfo = new HDUtils.PackedMipChainInfo();
            m_DepthBufferMipChainInfo.Allocate();
        }

        void CleanupSharedResources()
        {
        }

        void CreateSharedResources(RenderGraph renderGraph, HDCamera hdCamera, DebugDisplaySettings debugDisplaySettings)
        {
            TextureDesc depthDesc = new TextureDesc(Vector2.one) { depthBufferBits = DepthBits.Depth32, clearBuffer = true, xrInstancing = true, useDynamicScale = true, name = "CameraDepthStencil" };

            m_DepthBuffer = renderGraph.CreateTexture(depthDesc);
            m_DepthBufferMSAA = renderGraph.CreateTexture(new TextureDesc(depthDesc) { bindTextureMS = true, enableMSAA = true, name = "CameraDepthStencilMSAA" });
            m_DepthAsColorBufferMSAA = renderGraph.CreateTexture(new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R32_SFloat, clearBuffer = true, clearColor = Color.black, bindTextureMS = true, enableMSAA = true, xrInstancing = true, useDynamicScale = true, name = "DepthAsColorMSAA" }, HDShaderIDs._DepthTextureMS);
            m_DepthBufferMipChain = renderGraph.CreateTexture(new TextureDesc(ComputeDepthBufferMipChainSize) { colorFormat = GraphicsFormat.R32_SFloat, enableRandomWrite = true, xrInstancing = true, useDynamicScale = true, name = "CameraDepthBufferMipChain" }, HDShaderIDs._CameraDepthTexture);

            TextureDesc normalDesc = new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_UNorm, clearBuffer = NeedClearGBuffer(), clearColor = Color.black, xrInstancing = true, useDynamicScale = true, enableRandomWrite = true, name = "NormalBuffer" };
            m_NormalBuffer = renderGraph.CreateTexture(normalDesc, HDShaderIDs._NormalBufferTexture);
            m_NormalBufferMSAA = renderGraph.CreateTexture(new TextureDesc(normalDesc) { bindTextureMS = true, enableMSAA = true, enableRandomWrite = false, name = "NormalBufferMSAA" }, HDShaderIDs._NormalTextureMS);

            TextureDesc motionVectorDesc = new TextureDesc(Vector2.one) { colorFormat = Builtin.GetMotionVectorFormat(), xrInstancing = true, useDynamicScale = true, name = "Motion Vectors" };
            m_MotionVectorsBuffer = renderGraph.CreateTexture(motionVectorDesc, HDShaderIDs._CameraMotionVectorsTexture);
            m_MotionVectorsBufferMSAA = renderGraph.CreateTexture(new TextureDesc(motionVectorDesc) { bindTextureMS = true, enableMSAA = true, name = "Motion Vectors MSAA" });

            m_IsDepthBufferCopyValid = false;
        }

        RenderGraphMutableResource GetDepthStencilBuffer(bool isMSAA = false)
        {
            return isMSAA ? m_DepthBufferMSAA : m_DepthBuffer;
        }

        RenderGraphMutableResource GetNormalBuffer(bool isMSAA = false)
        {
            return isMSAA ? m_NormalBufferMSAA : m_NormalBuffer;
        }

        RenderGraphMutableResource GetDepthTexture(bool isMSAA = false)
        {
            return isMSAA ? m_DepthAsColorBufferMSAA : m_DepthBufferMipChain;
        }

        RenderGraphMutableResource GetMotionVectorsBuffer(bool isMSAA = false)
        {
            return isMSAA ? m_MotionVectorsBufferMSAA : m_MotionVectorsBuffer;
        }

        bool NeedClearGBuffer()
        {
            // TODO: Add an option to force clear
            return m_CurrentDebugDisplaySettings.IsDebugDisplayEnabled();
        }

        HDUtils.PackedMipChainInfo GetDepthBufferMipChainInfo()
        {
            return m_DepthBufferMipChainInfo;
        }
    }
}
