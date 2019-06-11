using System;
using System.Collections.Generic;

namespace UnityEngine.Experimental.Rendering.RenderGraphModule
{
    public struct RenderGraphBuilder : IDisposable
    {
        RenderGraphResourceRegistry m_RenderGraphResources;
        RenderGraph.RenderPass      m_RenderPass;
        bool                        m_Disposed;

        #region Public Interface
        public RenderGraphMutableResource CreateTexture( in TextureDesc desc, int shaderProperty = 0)
        {
            return m_RenderGraphResources.CreateTexture(desc, shaderProperty);
        }

        public RenderGraphMutableResource UseColorBuffer(in RenderGraphMutableResource input, int index)
        {
            if (input.type != RenderGraphResourceType.Texture)
                throw new ArgumentException("Trying to write to a resource that is not a texture or is invalid.");

            m_RenderPass.SetColorBuffer(input, index);
            return input;
        }

        public RenderGraphMutableResource UseDepthBuffer(in RenderGraphMutableResource input, DepthAccess flags)
        {
            if (input.type != RenderGraphResourceType.Texture)
                throw new ArgumentException("Trying to write to a resource that is not a texture or is invalid.");

            m_RenderPass.SetDepthBuffer(input, flags);
            return input;
        }

        public RenderGraphResource ReadTexture(in RenderGraphResource input)
        {
            if (input.type != RenderGraphResourceType.Texture)
                throw new ArgumentException("Trying to read a resource that is not a texture or is invalid.");
            m_RenderPass.resourceReadList.Add(input);
            return input;
        }

        public RenderGraphMutableResource WriteTexture(in RenderGraphMutableResource input)
        {
            if (input.type != RenderGraphResourceType.Texture)
                throw new ArgumentException("Trying to write to a resource that is not a texture or is invalid.");
            // TODO: Manage resource "version" for debugging purpose
            m_RenderPass.resourceWriteList.Add(input);
            return input;
        }

        public RenderGraphResource CreateRendererList(in RendererListDesc desc)
        {
            return m_RenderGraphResources.CreateRendererList(desc);
        }

        public RenderGraphResource UseRendererList(in RenderGraphResource resource)
        {
            if (resource.type != RenderGraphResourceType.RendererList)
                throw new ArgumentException("Trying use a resource that is not a renderer list.");
            m_RenderPass.usedRendererListList.Add(resource);
            return resource;
        }
        public void SetRenderFunc<PassData>(RenderFunc<PassData> renderFunc) where PassData : class, new()
        {
            ((RenderGraph.RenderPass<PassData>)m_RenderPass).renderFunc = renderFunc;
        }

        public void EnableAsyncCompute(bool value)
        {
            m_RenderPass.enableAsyncCompute = value;
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

        #region Internal Interface
        internal RenderGraphBuilder(RenderGraphResourceRegistry resources, RenderGraph.RenderPass renderPass)
        {
            m_RenderPass = renderPass;
            m_Disposed = false;
            m_RenderGraphResources = resources;
        }

        void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;

            m_Disposed = true;
        }
        #endregion
    }
}
