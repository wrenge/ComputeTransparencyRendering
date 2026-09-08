using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ComputeTransparency
{
    /// <summary>Tile edge in pixels. One thread group per tile, one thread per pixel, so this
    /// is also the group size: 32 means 1024 threads per group, which not every device allows.</summary>
    public enum CTTileSize
    {
        Size4 = 4,
        Size8 = 8,
        Size16 = 16,
        Size32 = 32
    }

    /// <summary>What the rasterizer writes instead of the shaded image.</summary>
    public enum CTDebugMode
    {
        /// <summary>Normal output.</summary>
        None = 0,

        /// <summary>Blends actually performed per pixel, on a heat ramp. Stops climbing where
        /// the early-out saved the work, so this is the picture of what the renderer is for.</summary>
        Overdraw = 1,

        /// <summary>Remaining transmittance per pixel: white is untouched, black is saturated
        /// and cut short by epsilon.</summary>
        Transmittance = 2,

        /// <summary>Primitives binned into each pixel's tile, before the early-out gets a say.
        /// Magenta marks tiles that overflowed and dropped primitives.</summary>
        TileLoad = 3,

        /// <summary>How many primitives the tile's thread group walked before every pixel in it
        /// ran out of transmittance. This, not the blend count, is what the kernel costs: a
        /// saturated pixel still sits in a SIMD group that keeps evaluating for its neighbours,
        /// so the whole tile pays for its slowest pixel.</summary>
        WalkLength = 4
    }

    /// <summary>Where the front to back ordering of the sprites is produced.</summary>
    public enum CTSortMode
    {
        /// <summary>Array.Sort on the main thread over a flat key array.</summary>
        Cpu,

        /// <summary>Four pass GPU radix sort, so no sprite is touched by the CPU at all.</summary>
        GpuRadix
    }

    /// <summary>
    /// Draws every <see cref="ComputeTransparentSprite"/> with a tile based compute rasterizer
    /// instead of the normal transparent queue. Sprites are shaded front to back and a tile stops
    /// as soon as it runs out of transmittance, so near opaque sprites cost far less overdraw.
    /// </summary>
    [DisallowMultipleRendererFeature("Compute Transparency")]
    public class ComputeTransparencyRenderFeature : ScriptableRendererFeature
    {
        [Serializable]
        public class Settings
        {
            public CTAtlas atlas;

            [Tooltip("Tile edge in pixels. Smaller tiles let the early-out fire on a smaller " +
                     "group of pixels, so saturated regions bail out sooner, but every primitive " +
                     "is binned into more tiles. This is also the compute group size: 32 asks for " +
                     "1024 threads per group, which some mobile GPUs refuse.")]
            public CTTileSize tileSize = CTTileSize.Size8;

            [Tooltip("How many depth ordered segments each tile's list is split into. The scatter " +
                     "hands out slots atomically and scrambles an order that was already correct, " +
                     "so the raster has to sort the list back. Segments keep the order between " +
                     "them for free, and the raster only sorts the one it is walking - which, with " +
                     "the early-out, is almost always the first. 1 is the old behaviour.\n\n" +
                     "This also sets capacity: one segment holds at most 256 primitives, so a tile " +
                     "holds 256 times this. Too few segments for a dense scene silently drops " +
                     "primitives - the Tile Load debug view marks those tiles magenta.")]
            [Range(1, 16)]
            public int binSegments = 4;

            [Tooltip("Pool size for the per tile primitive lists, expressed as an average per tile. " +
                     "Individual tiles may exceed it as long as the average holds.")]
            [Range(4, 1024)]
            public int averagePrimitivesPerTile = 64;

            [Tooltip("Reject sprite pixels hidden by opaque geometry. Needs the depth texture enabled " +
                     "on the URP asset.")]
            public bool depthTest = true;

            [Tooltip("A pixel stops blending once its remaining transmittance drops below this. " +
                     "Larger values cut the walk short sooner and cost less, at the price of dropping " +
                     "contributions the eye may still catch. 1/255 is the point where the result is " +
                     "exact in 8 bit.")]
            [Range(0f, 0.25f)]
            public float epsilon = 1f / 255f;

            [Tooltip("Where the depth sort runs. Both produce the same order; the GPU path keeps " +
                     "the main thread out of it entirely.")]
            public CTSortMode sortMode = CTSortMode.Cpu;

            [Tooltip("Sample the sprite atlas. Turning it off shades from the tint alone, on both " +
                     "renderers at once, which is what makes the comparison still mean something.\n\n" +
                     "It is not a clean measure of what sampling costs: without the texture's alpha " +
                     "every sprite is as opaque as its tint, so transmittance falls faster and the " +
                     "early-out fires sooner. Read it next to the Walk Length view.")]
            public bool textures = true;

            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

            [Header("Debug")]
            [Tooltip("Replaces the shaded image with a visualisation. The debug views are drawn " +
                     "opaque over the frame, so the rest of the scene is hidden while one is on.")]
            public CTDebugMode debugMode = CTDebugMode.None;

            [Tooltip("Count mapped to the top (red) of the heat ramp in the Overdraw and Tile Load " +
                     "views. Anything above it clamps to red.")]
            [Range(1f, 512f)]
            public float debugRange = 32f;

            [Tooltip("Wrap every dispatch in a GPU timestamp query and report the per pass cost. " +
                     "This is the only way to see which stage the frame is actually spent in - " +
                     "wall clock bundles the CPU gather, the GPU and the editor together.\n\n" +
                     "Off by default: timestamp queries cost something themselves, and on a tile " +
                     "based GPU they can split work that would otherwise batch, so the numbers are " +
                     "for finding the expensive pass and not for quoting as the renderer's cost.")]
            public bool gpuTimers;

            [Header("Shaders")]
            public ComputeShader setupShader;
            public ComputeShader binShader;
            public ComputeShader rasterShader;
            public ComputeShader radixSortShader;
            public Shader compositeShader;

            [Tooltip("Draws the Overdraw view for the hardware baseline. Only used while a " +
                     "CTInstancedSpriteBatch asks for it; the compute path counts its own blends.")]
            public Shader traditionalOverdrawShader;
        }

        public Settings settings = new Settings();

        // Global rather than a material property: the hardware baseline is drawn immediate mode
        // from CTInstancedSpriteBatch and the compute path from this pass, and the two have to
        // switch together or an A/B between them measures the switch instead of the renderers.
        static readonly int s_UntexturedId = Shader.PropertyToID("_CTUntextured");

        ComputeTransparencyPass m_Pass;
        CTTraditionalOverdrawPass m_OverdrawPass;
        Material m_CompositeMaterial;
        Material m_OverdrawMaterial;

        public override void Create()
        {
            // The hardware baseline's debug view does not need any of the compute machinery, so
            // it is built whether or not the rasterizer itself validates.
            if (settings.traditionalOverdrawShader != null)
            {
                if (m_OverdrawMaterial == null)
                    m_OverdrawMaterial = CoreUtils.CreateEngineMaterial(settings.traditionalOverdrawShader);
                m_OverdrawPass = new CTTraditionalOverdrawPass(settings, m_OverdrawMaterial);
            }

            if (!Validate())
                return;

            if (m_CompositeMaterial == null)
                m_CompositeMaterial = CoreUtils.CreateEngineMaterial(settings.compositeShader);

            m_Pass?.Dispose();
            m_Pass = new ComputeTransparencyPass(settings, m_CompositeMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Preview ||
                renderingData.cameraData.cameraType == CameraType.Reflection)
                return;

            // The sense is inverted so that an unset global - a scene with this feature disabled,
            // or the frames before it first runs - reads 0 and shades normally.
            Shader.SetGlobalFloat(s_UntexturedId, settings.textures ? 0f : 1f);

            if (m_OverdrawPass != null && CTTraditionalOverdrawPass.Armed)
                renderer.EnqueuePass(m_OverdrawPass);

            if (m_Pass == null)
                return;

            m_Pass.renderPassEvent = settings.renderPassEvent;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
            m_OverdrawPass = null;
            CoreUtils.Destroy(m_CompositeMaterial);
            m_CompositeMaterial = null;
            CoreUtils.Destroy(m_OverdrawMaterial);
            m_OverdrawMaterial = null;
        }

        bool Validate()
        {
            if (settings.setupShader == null || settings.binShader == null || settings.rasterShader == null)
            {
                Debug.LogWarning("[ComputeTransparency] Compute shaders are not assigned on the renderer feature.", this);
                return false;
            }

            if (settings.sortMode == CTSortMode.GpuRadix && settings.radixSortShader == null)
            {
                Debug.LogWarning("[ComputeTransparency] GPU sorting is selected but the radix sort " +
                                 "shader is not assigned; falling back to the CPU sort.", this);
                settings.sortMode = CTSortMode.Cpu;
            }

            if (settings.compositeShader == null)
            {
                Debug.LogWarning("[ComputeTransparency] Composite shader is not assigned on the renderer feature.", this);
                return false;
            }

            // An asset serialized before the setting existed deserializes to 0, which is not a
            // valid size, so normalise it before anything divides by it.
            if (settings.tileSize != CTTileSize.Size4 && settings.tileSize != CTTileSize.Size8 &&
                settings.tileSize != CTTileSize.Size16 && settings.tileSize != CTTileSize.Size32)
                settings.tileSize = CTTileSize.Size8;

            if (settings.debugRange < 1f)
                settings.debugRange = 32f;

            int threads = (int)settings.tileSize * (int)settings.tileSize;
            if (threads > SystemInfo.maxComputeWorkGroupSize)
            {
                Debug.LogWarning($"[ComputeTransparency] A {(int)settings.tileSize} pixel tile needs " +
                                 $"{threads} threads per group but this device allows " +
                                 $"{SystemInfo.maxComputeWorkGroupSize}; falling back to 8.", this);
                settings.tileSize = CTTileSize.Size8;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning("[ComputeTransparency] Compute shaders are not supported on this device.", this);
                return false;
            }

            return true;
        }
    }
}
