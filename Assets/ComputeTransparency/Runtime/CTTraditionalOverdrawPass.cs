using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ComputeTransparency
{
    /// <summary>
    /// The Overdraw debug view for the hardware baseline: the blends per pixel a
    /// <see cref="CTInstancedSpriteBatch"/> with <see cref="CTInstancedSpriteBatch.Overdraw"/> set
    /// is performing, on the same heat ramp the compute rasterizer's own Overdraw view uses.
    /// </summary>
    /// <remarks>
    /// The compute path counts its blends inside the loop that performs them. The hardware path
    /// has no such loop to instrument, so the count is a second draw of the same billboards into a
    /// single channel target with additive blending, which is then resolved through the ramp over
    /// the frame. The count is drawn here rather than by the batch because an immediate mode
    /// <c>Graphics.RenderMeshInstanced</c> can only go into the camera's own colour target, and
    /// accumulating a count in there would mean fighting whatever format and colour encoding the
    /// camera happens to use.
    ///
    /// This is the honest comparison to the compute Overdraw view: both count the sprites covering
    /// a pixel that pass the depth test, and the difference between the two pictures is exactly
    /// what the early-out saves.
    /// </remarks>
    public class CTTraditionalOverdrawPass : ScriptableRenderPass
    {
        static readonly int s_DebugRange = Shader.PropertyToID("_CTDebugRange");

        class CountPassData
        {
            public Material material;
            public List<CTInstancedSpriteBatch> batches;
        }

        class ResolvePassData
        {
            public TextureHandle source;
            public Material material;
            public float debugRange;
        }

        readonly ComputeTransparencyRenderFeature.Settings m_Settings;
        readonly Material m_Material;
        readonly List<CTInstancedSpriteBatch> m_Armed = new List<CTInstancedSpriteBatch>();

        public CTTraditionalOverdrawPass(ComputeTransparencyRenderFeature.Settings settings, Material material)
        {
            m_Settings = settings;
            m_Material = material;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        /// <summary>True when at least one live batch is asking for the view, so the feature can
        /// leave the pass out of the frame entirely the rest of the time.</summary>
        public static bool Armed
        {
            get
            {
                var batches = CTInstancedSpriteBatch.Batches;
                for (int i = 0; i < batches.Count; i++)
                    if (batches[i].Overdraw && batches[i].Enabled && batches[i].Count > 0)
                        return true;
                return false;
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null)
                return;

            m_Armed.Clear();
            var batches = CTInstancedSpriteBatch.Batches;
            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                if (batch.Overdraw && batch.Enabled && batch.Count > 0 &&
                    batch.OverdrawBuffer != null && batch.OverdrawProperties != null)
                    m_Armed.Add(batch);
            }

            if (m_Armed.Count == 0)
                return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var descriptor = cameraData.cameraTargetDescriptor;

            // One float per pixel so the sum is exact and cannot saturate; the view is only ever
            // on while someone is looking at it, so the extra target costs nothing in a build.
            var countDesc = new TextureDesc(descriptor.width, descriptor.height)
            {
                format = GraphicsFormat.R32_SFloat,
                clearBuffer = true,
                clearColor = Color.clear,
                // Has to match the depth attachment borrowed from the camera below.
                msaaSamples = (MSAASamples)descriptor.msaaSamples,
                name = "CT Traditional Overdraw",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var countHandle = renderGraph.CreateTexture(countDesc);

            using (var builder = renderGraph.AddRasterRenderPass<CountPassData>("CT Traditional Overdraw", out var data))
            {
                builder.AllowPassCulling(false);
                data.material = m_Material;
                data.batches = m_Armed;
                builder.SetRenderAttachment(countHandle, 0, AccessFlags.Write);
                // Read only: the count must be depth tested against the opaque scene, but nothing
                // here should disturb the depth the rest of the frame is still using.
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);

                builder.SetRenderFunc((CountPassData d, RasterGraphContext ctx) =>
                {
                    for (int i = 0; i < d.batches.Count; i++)
                    {
                        var batch = d.batches[i];
                        // The instance buffer rides in a property block rather than on the
                        // material: material state is resolved once when the command buffer
                        // executes, so several batches sharing one material would all draw the
                        // last one's instances.
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0,
                                               MeshTopology.Triangles, 6, batch.Count,
                                               batch.OverdrawProperties);
                    }
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<ResolvePassData>("CT Traditional Overdraw Resolve", out var data))
            {
                builder.AllowPassCulling(false);
                data.source = countHandle;
                data.material = m_Material;
                data.debugRange = m_Settings.debugRange;
                builder.UseTexture(countHandle, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc((ResolvePassData d, RasterGraphContext ctx) =>
                {
                    d.material.SetFloat(s_DebugRange, d.debugRange);
                    Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 1);
                });
            }
        }
    }
}
