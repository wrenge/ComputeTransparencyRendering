using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ComputeTransparency
{
    /// <summary>
    /// Records the compute rasterizer: setup, binning, prefix sum, scatter, raster and composite.
    /// Each stage is its own render graph pass so the graph inserts the barriers between them.
    /// </summary>
    public class ComputeTransparencyPass : ScriptableRenderPass
    {
        const int k_ScanGroup = 256;       // must match CT_SCAN_GROUP
        const int k_MaxSegments = 8;       // must match CT_MAX_SEGMENTS
        const int k_SetupGroup = 64;
        const int k_BinGroup = 64;

        // Pass names. Each is both the render graph pass's label and its GPU timer's sampler
        // name, so the profiler and the demo panel agree on what a row is.
        const string k_PassSetup = "CT Setup";
        const string k_PassBinClear = "CT Bin Clear";
        const string k_PassBinCount = "CT Bin Count";
        const string k_PassBinTotals = "CT Bin Tile Totals";
        const string k_PassScanBlocks = "CT Scan Blocks";
        const string k_PassScanBlockSums = "CT Scan Block Sums";
        const string k_PassScanAdd = "CT Scan Add";
        const string k_PassSegBase = "CT Bin Segment Bases";
        const string k_PassScatter = "CT Bin Scatter";
        const string k_PassRaster = "CT Raster";
        const string k_PassComposite = "CT Composite";
        const string k_PassSortPrepare = "CT Sort Prepare";
        const string k_PassSortCount = "CT Sort Count";
        const string k_PassSortScanBlocks = "CT Sort Scan Blocks";
        const string k_PassSortScanBlockSums = "CT Sort Scan Block Sums";
        const string k_PassSortScanAdd = "CT Sort Scan Add";
        const string k_PassSortScatter = "CT Sort Scatter";

        // Timer groups, which is how the demo panel folds seventeen rows into something readable.
        const string k_GroupSetup = "Setup";
        const string k_GroupSort = "Sort";
        const string k_GroupBin = "Bin";
        const string k_GroupRaster = "Raster";
        const string k_GroupComposite = "Composite";

        static class ShaderIds
        {
            public static readonly int Instances = Shader.PropertyToID("_CTInstances");
            public static readonly int SortedIndices = Shader.PropertyToID("_CTSortedIndices");
            public static readonly int Triangles = Shader.PropertyToID("_CTTriangles");
            public static readonly int TileCount = Shader.PropertyToID("_CTTileCount");
            public static readonly int TileOffset = Shader.PropertyToID("_CTTileOffset");
            public static readonly int TileTotal = Shader.PropertyToID("_CTTileTotal");
            public static readonly int TileEnd = Shader.PropertyToID("_CTTileEnd");
            public static readonly int TileSegCount = Shader.PropertyToID("_CTTileSegCount");
            public static readonly int TileSegBase = Shader.PropertyToID("_CTTileSegBase");
            public static readonly int TileSegCursor = Shader.PropertyToID("_CTTileSegCursor");
            public static readonly int BlockSums = Shader.PropertyToID("_CTBlockSums");
            public static readonly int TileList = Shader.PropertyToID("_CTTileList");
            public static readonly int Atlas = Shader.PropertyToID("_CTAtlas");
            public static readonly int SceneDepth = Shader.PropertyToID("_CTSceneDepth");
            public static readonly int Result = Shader.PropertyToID("_CTResult");

            public static readonly int ViewProj = Shader.PropertyToID("_CTViewProj");
            public static readonly int ScreenSize = Shader.PropertyToID("_CTScreenSize");
            public static readonly int ClipZParams = Shader.PropertyToID("_CTClipZParams");
            public static readonly int AtlasSize = Shader.PropertyToID("_CTAtlasSize");
            public static readonly int CameraRight = Shader.PropertyToID("_CTCameraRight");
            public static readonly int CameraUp = Shader.PropertyToID("_CTCameraUp");
            public static readonly int SpriteCount = Shader.PropertyToID("_CTSpriteCount");
            public static readonly int TileParams = Shader.PropertyToID("_CTTileParams");
            public static readonly int TriCount = Shader.PropertyToID("_CTTriCount");
            public static readonly int TileListCapacity = Shader.PropertyToID("_CTTileListCapacity");
            public static readonly int ReversedZ = Shader.PropertyToID("_CTReversedZ");
            public static readonly int Epsilon = Shader.PropertyToID("_CTEpsilon");
            public static readonly int TileSize = Shader.PropertyToID("_CTTileSize");
            public static readonly int DebugMode = Shader.PropertyToID("_CTDebugMode");
            public static readonly int DebugRange = Shader.PropertyToID("_CTDebugRange");
            public static readonly int SegmentCount = Shader.PropertyToID("_CTSegmentCount");

            public static readonly int SortKeysIn = Shader.PropertyToID("_CTSortKeysIn");
            public static readonly int SortValuesIn = Shader.PropertyToID("_CTSortValuesIn");
            public static readonly int SortKeysOut = Shader.PropertyToID("_CTSortKeysOut");
            public static readonly int SortValuesOut = Shader.PropertyToID("_CTSortValuesOut");
            public static readonly int SortHistogram = Shader.PropertyToID("_CTSortHistogram");
            public static readonly int SortBlockSums = Shader.PropertyToID("_CTSortBlockSums");
            public static readonly int SortCameraPosition = Shader.PropertyToID("_CTSortCameraPosition");
            public static readonly int SortCameraForward = Shader.PropertyToID("_CTSortCameraForward");
            public static readonly int SortInstanceCount = Shader.PropertyToID("_CTSortInstanceCount");
            public static readonly int SortPaddedCount = Shader.PropertyToID("_CTSortPaddedCount");
            public static readonly int SortBlockCount = Shader.PropertyToID("_CTSortBlockCount");
            public static readonly int SortShift = Shader.PropertyToID("_CTSortShift");
            public static readonly int SortHistogramSize = Shader.PropertyToID("_CTSortHistogramSize");
        }

        class ComputePassData
        {
            public ComputeShader shader;
            public int kernel;
            public Vector3Int groups;

            // Null unless the GPU timers are on. Captured at record time so the render func does
            // not have to look at the settings.
            public CustomSampler timer;

            public BufferHandle instances;
            public BufferHandle sortedIndices;
            public BufferHandle triangles;
            public BufferHandle tileCount;
            public BufferHandle tileTotal;
            public BufferHandle tileOffset;
            public BufferHandle tileEnd;
            public BufferHandle tileSegCount;
            public BufferHandle tileSegBase;
            public BufferHandle tileSegCursor;
            public BufferHandle blockSums;
            public BufferHandle tileList;

            public TextureHandle sceneDepth;
            public TextureHandle result;

            public Matrix4x4 viewProj;
            public Vector4 screenSize;
            public Vector4 clipZParams;
            public Vector2 atlasSize;
            public Vector4 cameraRight;
            public Vector4 cameraUp;
            public Vector4Int tileParams;
            public int spriteCount;
            public int triCount;
            public int tileListCapacity;
            public int reversedZ;
            public int depthTest;
            public float epsilon;
            public int tileSize;
            public int segmentCount;
            public int debugMode;
            public float debugRange;
        }

        class SortPassData
        {
            public ComputeShader shader;
            public int kernel;
            public int groups;

            public CustomSampler timer;

            public BufferHandle instances;
            public BufferHandle keysIn;
            public BufferHandle valuesIn;
            public BufferHandle keysOut;
            public BufferHandle valuesOut;
            public BufferHandle histogram;
            public BufferHandle blockSums;

            public Vector4 cameraPosition;
            public Vector4 cameraForward;
            public int instanceCount;
            public int paddedCount;
            public int blockCount;
            public int shift;
            public int histogramSize;
        }

        class CompositePassData
        {
            public TextureHandle source;
            public Material material;

            public CustomSampler timer;
        }

        readonly ComputeTransparencyRenderFeature.Settings m_Settings;
        readonly Material m_CompositeMaterial;

        readonly CTInstanceCollector m_Collector = new CTInstanceCollector();
        readonly CTRadixSorter m_Sorter = new CTRadixSorter();

        int m_KSetup, m_KClear, m_KCount, m_KTileTotals, m_KScanBlocks, m_KScanBlockSums, m_KScanAdd, m_KSegBase, m_KScatter;

        // One raster kernel per supported tile size, indexed by log2(size) - 2 so that
        // 4, 8, 16 and 32 map to 0..3. A kernel the platform could not compile stays at -1.
        readonly int[] m_KRaster = { -1, -1, -1, -1 };
        int m_KSortPrepare, m_KSortCount, m_KSortScanBlocks, m_KSortScanBlockSums, m_KSortScanAdd, m_KSortScatter;

        // Shader keywords rather than uniforms, for choices that are fixed for a whole dispatch.
        LocalKeyword m_KwTileReject;
        LocalKeyword m_KwDebug, m_KwDepthTest, m_KwUntextured;

        public ComputeTransparencyPass(ComputeTransparencyRenderFeature.Settings settings, Material compositeMaterial)
        {
            m_Settings = settings;
            m_CompositeMaterial = compositeMaterial;
            renderPassEvent = settings.renderPassEvent;

            m_KSetup = settings.setupShader.FindKernel("CSSetup");
            m_KClear = settings.binShader.FindKernel("CSClear");
            m_KCount = settings.binShader.FindKernel("CSCount");
            m_KTileTotals = settings.binShader.FindKernel("CSTileTotals");
            m_KScanBlocks = settings.binShader.FindKernel("CSScanBlocks");
            m_KScanBlockSums = settings.binShader.FindKernel("CSScanBlockSums");
            m_KScanAdd = settings.binShader.FindKernel("CSScanAdd");
            m_KSegBase = settings.binShader.FindKernel("CSSegBase");
            m_KScatter = settings.binShader.FindKernel("CSScatter");
            m_KwTileReject = new LocalKeyword(settings.binShader, "CT_TILE_REJECT");

            for (int i = 0; i < m_KRaster.Length; i++)
            {
                string name = "CSRaster" + (4 << i);
                if (settings.rasterShader.HasKernel(name))
                    m_KRaster[i] = settings.rasterShader.FindKernel(name);
            }

            m_KwDebug = new LocalKeyword(settings.rasterShader, "CT_DEBUG");
            m_KwDepthTest = new LocalKeyword(settings.rasterShader, "CT_DEPTH_TEST");
            m_KwUntextured = new LocalKeyword(settings.rasterShader, "CT_UNTEXTURED");

            if (settings.radixSortShader != null)
            {
                m_KSortPrepare = settings.radixSortShader.FindKernel("CSPrepare");
                m_KSortCount = settings.radixSortShader.FindKernel("CSCount");
                m_KSortScanBlocks = settings.radixSortShader.FindKernel("CSScanBlocks");
                m_KSortScanBlockSums = settings.radixSortShader.FindKernel("CSScanBlockSums");
                m_KSortScanAdd = settings.radixSortShader.FindKernel("CSScanAdd");
                m_KSortScatter = settings.radixSortShader.FindKernel("CSScatter");
            }
        }

        public void Dispose()
        {
            m_Collector.Dispose();
            m_Sorter.Dispose();
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // Before the early returns below: the toggle has to take effect, and the recorders
            // have to be read, even on a frame this pass decides not to record anything on.
            CTGpuTimers.Enabled = m_Settings.gpuTimers;
            CTGpuTimers.Tick();

            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var camera = cameraData.camera;

            var atlasTexture = m_Settings.atlas != null ? m_Settings.atlas.Texture : null;
            if (atlasTexture == null)
                return;

            int tileSize = (int)m_Settings.tileSize;
            int segmentCount = Mathf.Clamp(m_Settings.binSegments, 1, k_MaxSegments);
            int rasterKernel = ResolveRasterKernel(ref tileSize);
            if (rasterKernel < 0)
                return;

            bool gpuSort = m_Settings.sortMode == CTSortMode.GpuRadix && m_Settings.radixSortShader != null;

            if (!m_Collector.Collect(camera, !gpuSort))
                return;

            int spriteCount = m_Collector.Count;

            // The atlas is an ordinary project texture rather than a render graph resource, so it is
            // bound straight on the compute shader. Importing it as an RTHandle binds it as a plain
            // 2D render target identifier and the Texture2DArray sampler comes back empty.
            m_Settings.rasterShader.SetTexture(rasterKernel, ShaderIds.Atlas, atlasTexture);

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            int tilesX = DivUp(width, tileSize);
            int tilesY = DivUp(height, tileSize);
            int tileCount = tilesX * tilesY;
            int blockCount = (tileCount + k_ScanGroup - 1) / k_ScanGroup;
            int triCount = spriteCount * 2;
            int listCapacity = Mathf.Max(tileCount * m_Settings.averagePrimitivesPerTile, 1024);

            var tileParams = new Vector4Int(tilesX, tilesY, tileCount, blockCount);
            var screenSize = new Vector4(width, height, 1f / width, 1f / height);

            // The depth range of the clip space Unity hands us: OpenGL keeps z in [-1, 1],
            // every other API already uses [0, 1].
            bool openGL = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 ||
                          SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
            var clipZParams = openGL ? new Vector4(0.5f, 0.5f, 0, 0) : new Vector4(1f, 0f, 0, 0);

            // renderIntoTexture: false on purpose. The flip that flag applies exists for hardware
            // rasterization into a render texture; this rasterizer writes to a UAV with its own
            // pixel origin, and taking the flipped matrix mirrors the image vertically.
            var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
            var viewProj = proj * camera.worldToCameraMatrix;

            var sceneDepth = resourceData.cameraDepthTexture;
            bool depthTest = m_Settings.depthTest && sceneDepth.IsValid();

            // Set on the shader asset while the graph is being recorded, not through the command
            // buffer. cmd.SetKeyword inside a render graph pass needs AllowGlobalStateModification,
            // and that inserts a sync point and forbids reordering across the pass - eight of them
            // across the bin stage would cost more than the branch this replaces. Recording it here
            // is what Unity's own compute shaders do, and it is safe as long as every dispatch of a
            // given shader in a frame wants the same value, which is the case: the setting lives on
            // the feature, not on the camera.
            SetKeyword(m_Settings.binShader, m_KwTileReject, m_Settings.tileReject);
            SetKeyword(m_Settings.rasterShader, m_KwDebug, m_Settings.debugMode != CTDebugMode.None);
            SetKeyword(m_Settings.rasterShader, m_KwDepthTest, depthTest);
            SetKeyword(m_Settings.rasterShader, m_KwUntextured, !m_Settings.textures);

            // Resources. The sprite buffer is persistent because the CPU writes it every frame;
            // everything else is transient so the graph can alias the memory.
            var instanceHandle = renderGraph.ImportBuffer(m_Collector.InstanceBuffer);
            var indexHandle = gpuSort
                ? RecordRadixSort(renderGraph, camera, instanceHandle, spriteCount)
                : renderGraph.ImportBuffer(m_Collector.IndexBuffer);
            var triHandle = CreateBuffer(renderGraph, triCount, CTFormats.TriangleStride, "CT Triangles");
            var tileCountHandle = CreateBuffer(renderGraph, tileCount, sizeof(uint), "CT Tile Counts");
            var tileTotalHandle = CreateBuffer(renderGraph, tileCount, sizeof(uint), "CT Tile Totals");
            var tileOffsetHandle = CreateBuffer(renderGraph, tileCount, sizeof(uint), "CT Tile Offsets");
            var tileEndHandle = CreateBuffer(renderGraph, tileCount, sizeof(uint), "CT Tile Ends");
            int segSlots = tileCount * segmentCount;
            var tileSegCountHandle = CreateBuffer(renderGraph, segSlots, sizeof(uint), "CT Tile Segment Counts");
            var tileSegBaseHandle = CreateBuffer(renderGraph, segSlots, sizeof(uint), "CT Tile Segment Bases");
            var tileSegCursorHandle = CreateBuffer(renderGraph, segSlots, sizeof(uint), "CT Tile Segment Cursors");
            var blockSumsHandle = CreateBuffer(renderGraph, Mathf.Max(blockCount, 1), sizeof(uint), "CT Block Sums");
            var tileListHandle = CreateBuffer(renderGraph, listCapacity, sizeof(uint), "CT Tile List");

            // The accumulation target is written by the raster kernel and read straight back by the
            // composite blit, so at high resolution it is pure per pixel traffic that the hardware
            // path never pays. Without HDR it does not need to be float: accum is a sum of
            // T * alpha * colour whose total weight cannot exceed 1, so LDR content stays inside
            // [0, 1] and 8 bits per channel halves the traffic. HDR keeps the float format because
            // there accum genuinely can go above 1.
            var resultFormat = cameraData.isHdrEnabled
                ? GraphicsFormat.R16G16B16A16_SFloat
                : GraphicsFormat.R8G8B8A8_UNorm;

            var resultDesc = new TextureDesc(width, height)
            {
                format = resultFormat,
                enableRandomWrite = true,
                clearBuffer = false,
                name = "CT Result",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var resultHandle = renderGraph.CreateTexture(resultDesc);

            ComputePassData Common(IComputeRenderGraphBuilder builder, ComputePassData data, ComputeShader shader, int kernel)
            {
                builder.AllowPassCulling(false);
                data.shader = shader;
                data.kernel = kernel;
                data.screenSize = screenSize;
                data.tileParams = tileParams;
                data.triCount = triCount;
                data.spriteCount = spriteCount;
                data.tileListCapacity = listCapacity;
                data.tileSize = tileSize;
                data.segmentCount = segmentCount;
                return data;
            }

            // 1. Sprites to screen space triangles.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassSetup, out var data))
            {
                Common(builder, data, m_Settings.setupShader, m_KSetup);
                data.timer = CTGpuTimers.Sampler(k_GroupSetup, k_PassSetup);
                data.groups = new Vector3Int(DivUp(spriteCount, k_SetupGroup), 1, 1);
                data.instances = builder.UseBuffer(instanceHandle, AccessFlags.Read);
                data.sortedIndices = builder.UseBuffer(indexHandle, AccessFlags.Read);
                data.triangles = builder.UseBuffer(triHandle, AccessFlags.Write);
                data.viewProj = viewProj;
                data.clipZParams = clipZParams;
                data.atlasSize = new Vector2(atlasTexture.width, atlasTexture.height);
                data.cameraRight = camera.transform.right;
                data.cameraUp = camera.transform.up;

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeMatrixParam(d.shader, ShaderIds.ViewProj, d.viewProj);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.ScreenSize, d.screenSize);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.ClipZParams, d.clipZParams);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.AtlasSize, d.atlasSize);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.CameraRight, d.cameraRight);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.CameraUp, d.cameraUp);
                    cmd.SetComputeIntParam(d.shader, ShaderIds.SpriteCount, d.spriteCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Instances, (GraphicsBuffer)d.instances);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortedIndices, (GraphicsBuffer)d.sortedIndices);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Triangles, (GraphicsBuffer)d.triangles);
                    Dispatch(cmd, d);
                });
            }

            // 2. Reset the per tile counters.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassBinClear, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KClear);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassBinClear);
                data.groups = new Vector3Int(DivUp(Mathf.Max(segSlots, blockCount), k_ScanGroup), 1, 1);
                data.tileSegCount = builder.UseBuffer(tileSegCountHandle, AccessFlags.Write);
                data.blockSums = builder.UseBuffer(blockSumsHandle, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCount, (GraphicsBuffer)d.tileSegCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.BlockSums, (GraphicsBuffer)d.blockSums);
                    Dispatch(cmd, d);
                });
            }

            // 3. Count how many triangles overlap each tile.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassBinCount, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KCount);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassBinCount);
                data.groups = new Vector3Int(DivUp(triCount, k_BinGroup), 1, 1);
                data.triangles = builder.UseBuffer(triHandle, AccessFlags.Read);
                data.tileSegCount = builder.UseBuffer(tileSegCountHandle, AccessFlags.ReadWrite);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Triangles, (GraphicsBuffer)d.triangles);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCount, (GraphicsBuffer)d.tileSegCount);
                    Dispatch(cmd, d);
                });
            }

            // 3b. Fold the per segment counts into the tile total and each segment's place in it.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassBinTotals, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KTileTotals);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassBinTotals);
                data.groups = new Vector3Int(DivUp(tileCount, k_ScanGroup), 1, 1);
                data.tileSegCount = builder.UseBuffer(tileSegCountHandle, AccessFlags.ReadWrite);
                data.tileSegBase = builder.UseBuffer(tileSegBaseHandle, AccessFlags.Write);
                data.tileCount = builder.UseBuffer(tileCountHandle, AccessFlags.Write);
                data.tileTotal = builder.UseBuffer(tileTotalHandle, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCount, (GraphicsBuffer)d.tileSegCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegBase, (GraphicsBuffer)d.tileSegBase);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileCount, (GraphicsBuffer)d.tileCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileTotal, (GraphicsBuffer)d.tileTotal);
                    Dispatch(cmd, d);
                });
            }

            // 4. Prefix sum the (clamped) counts into pool offsets.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassScanBlocks, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KScanBlocks);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassScanBlocks);
                data.groups = new Vector3Int(Mathf.Max(blockCount, 1), 1, 1);
                data.tileTotal = builder.UseBuffer(tileTotalHandle, AccessFlags.Read);
                data.tileOffset = builder.UseBuffer(tileOffsetHandle, AccessFlags.Write);
                data.blockSums = builder.UseBuffer(blockSumsHandle, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileTotal, (GraphicsBuffer)d.tileTotal);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileOffset, (GraphicsBuffer)d.tileOffset);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.BlockSums, (GraphicsBuffer)d.blockSums);
                    Dispatch(cmd, d);
                });
            }

            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassScanBlockSums, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KScanBlockSums);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassScanBlockSums);
                data.groups = new Vector3Int(1, 1, 1);
                data.blockSums = builder.UseBuffer(blockSumsHandle, AccessFlags.ReadWrite);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.BlockSums, (GraphicsBuffer)d.blockSums);
                    Dispatch(cmd, d);
                });
            }

            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassScanAdd, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KScanAdd);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassScanAdd);
                data.groups = new Vector3Int(DivUp(tileCount, k_ScanGroup), 1, 1);
                data.tileOffset = builder.UseBuffer(tileOffsetHandle, AccessFlags.ReadWrite);
                data.blockSums = builder.UseBuffer(blockSumsHandle, AccessFlags.Read);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileOffset, (GraphicsBuffer)d.tileOffset);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.BlockSums, (GraphicsBuffer)d.blockSums);
                    Dispatch(cmd, d);
                });
            }

            // 4b. Give each segment its absolute sub-slice and aim its scatter cursor at it.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassSegBase, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KSegBase);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassSegBase);
                data.groups = new Vector3Int(DivUp(tileCount, k_ScanGroup), 1, 1);
                data.tileOffset = builder.UseBuffer(tileOffsetHandle, AccessFlags.Read);
                data.tileTotal = builder.UseBuffer(tileTotalHandle, AccessFlags.Read);
                data.tileEnd = builder.UseBuffer(tileEndHandle, AccessFlags.Write);
                data.tileSegBase = builder.UseBuffer(tileSegBaseHandle, AccessFlags.ReadWrite);
                data.tileSegCursor = builder.UseBuffer(tileSegCursorHandle, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileOffset, (GraphicsBuffer)d.tileOffset);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileTotal, (GraphicsBuffer)d.tileTotal);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileEnd, (GraphicsBuffer)d.tileEnd);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegBase, (GraphicsBuffer)d.tileSegBase);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCursor, (GraphicsBuffer)d.tileSegCursor);
                    Dispatch(cmd, d);
                });
            }

            // 5. Write the triangle indices into each tile's slice of the pool.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassScatter, out var data))
            {
                Common(builder, data, m_Settings.binShader, m_KScatter);
                data.timer = CTGpuTimers.Sampler(k_GroupBin, k_PassScatter);
                data.groups = new Vector3Int(DivUp(triCount, k_BinGroup), 1, 1);
                data.triangles = builder.UseBuffer(triHandle, AccessFlags.Read);
                data.tileSegCount = builder.UseBuffer(tileSegCountHandle, AccessFlags.Read);
                data.tileSegBase = builder.UseBuffer(tileSegBaseHandle, AccessFlags.Read);
                data.tileSegCursor = builder.UseBuffer(tileSegCursorHandle, AccessFlags.ReadWrite);
                data.tileList = builder.UseBuffer(tileListHandle, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetBinConstants(cmd, d);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Triangles, (GraphicsBuffer)d.triangles);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCount, (GraphicsBuffer)d.tileSegCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegBase, (GraphicsBuffer)d.tileSegBase);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCursor, (GraphicsBuffer)d.tileSegCursor);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileList, (GraphicsBuffer)d.tileList);
                    Dispatch(cmd, d);
                });
            }

            // 6. Shade every tile front to back.
            using (var builder = renderGraph.AddComputePass<ComputePassData>(k_PassRaster, out var data))
            {
                Common(builder, data, m_Settings.rasterShader, rasterKernel);
                data.timer = CTGpuTimers.Sampler(k_GroupRaster, k_PassRaster);
                data.groups = new Vector3Int(tilesX, tilesY, 1);
                data.triangles = builder.UseBuffer(triHandle, AccessFlags.Read);
                data.tileCount = builder.UseBuffer(tileCountHandle, AccessFlags.Read);
                data.tileEnd = builder.UseBuffer(tileEndHandle, AccessFlags.Read);
                data.tileSegCount = builder.UseBuffer(tileSegCountHandle, AccessFlags.Read);
                data.tileSegBase = builder.UseBuffer(tileSegBaseHandle, AccessFlags.Read);
                data.tileList = builder.UseBuffer(tileListHandle, AccessFlags.Read);
                data.result = resultHandle;
                builder.UseTexture(resultHandle, AccessFlags.Write);
                data.reversedZ = SystemInfo.usesReversedZBuffer ? 1 : 0;
                data.depthTest = depthTest ? 1 : 0;
                data.epsilon = m_Settings.epsilon;
                data.debugMode = (int)m_Settings.debugMode;
                data.debugRange = m_Settings.debugRange;
                data.sceneDepth = sceneDepth;
                if (depthTest)
                    builder.UseTexture(sceneDepth, AccessFlags.Read);

                builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.ScreenSize, d.screenSize);
                    cmd.SetComputeIntParam(d.shader, ShaderIds.ReversedZ, d.reversedZ);
                    cmd.SetComputeFloatParam(d.shader, ShaderIds.Epsilon, d.epsilon);
                    cmd.SetComputeIntParam(d.shader, ShaderIds.DebugMode, d.debugMode);
                    cmd.SetComputeFloatParam(d.shader, ShaderIds.DebugRange, d.debugRange);
                    cmd.SetComputeIntParam(d.shader, ShaderIds.SegmentCount, d.segmentCount);
                    cmd.SetComputeIntParams(d.shader, ShaderIds.TileParams,
                        d.tileParams.x, d.tileParams.y, d.tileParams.z, d.tileParams.w);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Triangles, (GraphicsBuffer)d.triangles);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileCount, (GraphicsBuffer)d.tileCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileEnd, (GraphicsBuffer)d.tileEnd);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegCount, (GraphicsBuffer)d.tileSegCount);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileSegBase, (GraphicsBuffer)d.tileSegBase);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.TileList, (GraphicsBuffer)d.tileList);
                    if (d.depthTest != 0)
                        cmd.SetComputeTextureParam(d.shader, d.kernel, ShaderIds.SceneDepth, d.sceneDepth);
                    cmd.SetComputeTextureParam(d.shader, d.kernel, ShaderIds.Result, d.result);
                    Dispatch(cmd, d);
                });
            }

            // 7. Blend the accumulated colour over the camera target.
            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>(k_PassComposite, out var data))
            {
                builder.AllowPassCulling(false);
                data.timer = CTGpuTimers.Sampler(k_GroupComposite, k_PassComposite);
                data.source = resultHandle;
                builder.UseTexture(resultHandle, AccessFlags.Read);
                data.material = m_CompositeMaterial;
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc((CompositePassData d, RasterGraphContext ctx) =>
                {
                    if (d.timer != null)
                        ctx.cmd.BeginSample(d.timer);

                    Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 0);

                    if (d.timer != null)
                        ctx.cmd.EndSample(d.timer);
                });
            }
        }

        /// <summary>
        /// Records the four radix passes and returns the buffer holding the sorted instance
        /// indices. Every stage is its own render graph pass so the graph inserts the barriers
        /// between them; a single pass with back to back dispatches would race.
        /// </summary>
        BufferHandle RecordRadixSort(RenderGraph renderGraph, Camera camera,
                                     BufferHandle instances, int spriteCount)
        {
            m_Sorter.Prepare(spriteCount);

            var keysA = renderGraph.ImportBuffer(m_Sorter.KeysA);
            var keysB = renderGraph.ImportBuffer(m_Sorter.KeysB);
            var valuesA = renderGraph.ImportBuffer(m_Sorter.ValuesA);
            var valuesB = renderGraph.ImportBuffer(m_Sorter.ValuesB);
            var histogram = renderGraph.ImportBuffer(m_Sorter.Histogram);
            var blockSums = renderGraph.ImportBuffer(m_Sorter.BlockSums);

            // The order only changes when the content or the camera did, and the buffers are
            // persistent, so an unchanged frame can reuse last frame's result. The CPU path
            // skips on the same condition, which keeps a comparison between them honest.
            if (!m_Collector.NeedsResort)
                return valuesA;

            var shader = m_Settings.radixSortShader;
            var cameraTransform = camera.transform;

            SortPassData Common(IComputeRenderGraphBuilder builder, SortPassData data, int kernel, int groups)
            {
                builder.AllowPassCulling(false);
                data.shader = shader;
                data.kernel = kernel;
                data.groups = Mathf.Max(1, groups);
                data.instanceCount = spriteCount;
                data.paddedCount = m_Sorter.PaddedCount;
                data.blockCount = m_Sorter.BlockCount;
                data.histogramSize = m_Sorter.HistogramSize;
                return data;
            }

            using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortPrepare, out var data))
            {
                Common(builder, data, m_KSortPrepare, DivUp(m_Sorter.PaddedCount, CTRadixSorter.GroupSize));
                data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortPrepare);
                data.instances = builder.UseBuffer(instances, AccessFlags.Read);
                data.keysIn = builder.UseBuffer(keysA, AccessFlags.Write);
                data.valuesIn = builder.UseBuffer(valuesA, AccessFlags.Write);
                data.cameraPosition = cameraTransform.position;
                data.cameraForward = cameraTransform.forward;

                builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    SetSortConstants(cmd, d);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.SortCameraPosition, d.cameraPosition);
                    cmd.SetComputeVectorParam(d.shader, ShaderIds.SortCameraForward, d.cameraForward);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.Instances, (GraphicsBuffer)d.instances);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortKeysIn, (GraphicsBuffer)d.keysIn);
                    cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortValuesIn, (GraphicsBuffer)d.valuesIn);
                    Dispatch(cmd, d, d.groups);
                });
            }

            // The radix passes reuse one sampler each, so a sort timer reports the total across
            // all of them rather than one. That is the number worth having: the question is what
            // the sort costs, not what a quarter of it costs.
            for (int pass = 0; pass < CTRadixSorter.PassCount; pass++)
            {
                // Ping-pong. PassCount is even, so the last pass lands back in the A buffers.
                bool even = (pass & 1) == 0;
                var keysIn = even ? keysA : keysB;
                var valuesIn = even ? valuesA : valuesB;
                var keysOut = even ? keysB : keysA;
                var valuesOut = even ? valuesB : valuesA;
                int shift = pass * CTRadixSorter.BitsPerPass;

                using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortCount, out var data))
                {
                    Common(builder, data, m_KSortCount, m_Sorter.BlockCount);
                    data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortCount);
                    data.shift = shift;
                    data.keysIn = builder.UseBuffer(keysIn, AccessFlags.Read);
                    data.histogram = builder.UseBuffer(histogram, AccessFlags.Write);

                    builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        SetSortConstants(cmd, d);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortKeysIn, (GraphicsBuffer)d.keysIn);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortHistogram, (GraphicsBuffer)d.histogram);
                        Dispatch(cmd, d, d.groups);
                    });
                }

                using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortScanBlocks, out var data))
                {
                    Common(builder, data, m_KSortScanBlocks, m_Sorter.ScanGroupCount);
                    data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortScanBlocks);
                    data.shift = shift;
                    data.histogram = builder.UseBuffer(histogram, AccessFlags.ReadWrite);
                    data.blockSums = builder.UseBuffer(blockSums, AccessFlags.Write);

                    builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        SetSortConstants(cmd, d);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortHistogram, (GraphicsBuffer)d.histogram);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortBlockSums, (GraphicsBuffer)d.blockSums);
                        Dispatch(cmd, d, d.groups);
                    });
                }

                using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortScanBlockSums, out var data))
                {
                    Common(builder, data, m_KSortScanBlockSums, 1);
                    data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortScanBlockSums);
                    data.shift = shift;
                    data.blockSums = builder.UseBuffer(blockSums, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        SetSortConstants(cmd, d);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortBlockSums, (GraphicsBuffer)d.blockSums);
                        Dispatch(cmd, d, 1);
                    });
                }

                using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortScanAdd, out var data))
                {
                    Common(builder, data, m_KSortScanAdd, m_Sorter.ScanGroupCount);
                    data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortScanAdd);
                    data.shift = shift;
                    data.histogram = builder.UseBuffer(histogram, AccessFlags.ReadWrite);
                    data.blockSums = builder.UseBuffer(blockSums, AccessFlags.Read);

                    builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        SetSortConstants(cmd, d);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortHistogram, (GraphicsBuffer)d.histogram);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortBlockSums, (GraphicsBuffer)d.blockSums);
                        Dispatch(cmd, d, d.groups);
                    });
                }

                using (var builder = renderGraph.AddComputePass<SortPassData>(k_PassSortScatter, out var data))
                {
                    Common(builder, data, m_KSortScatter, m_Sorter.BlockCount);
                    data.timer = CTGpuTimers.Sampler(k_GroupSort, k_PassSortScatter);
                    data.shift = shift;
                    data.keysIn = builder.UseBuffer(keysIn, AccessFlags.Read);
                    data.valuesIn = builder.UseBuffer(valuesIn, AccessFlags.Read);
                    data.keysOut = builder.UseBuffer(keysOut, AccessFlags.Write);
                    data.valuesOut = builder.UseBuffer(valuesOut, AccessFlags.Write);
                    data.histogram = builder.UseBuffer(histogram, AccessFlags.Read);

                    builder.SetRenderFunc((SortPassData d, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        SetSortConstants(cmd, d);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortKeysIn, (GraphicsBuffer)d.keysIn);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortValuesIn, (GraphicsBuffer)d.valuesIn);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortKeysOut, (GraphicsBuffer)d.keysOut);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortValuesOut, (GraphicsBuffer)d.valuesOut);
                        cmd.SetComputeBufferParam(d.shader, d.kernel, ShaderIds.SortHistogram, (GraphicsBuffer)d.histogram);
                        Dispatch(cmd, d, d.groups);
                    });
                }
            }

            return valuesA;
        }

        static void SetSortConstants(ComputeCommandBuffer cmd, SortPassData d)
        {
            cmd.SetComputeIntParam(d.shader, ShaderIds.SortInstanceCount, d.instanceCount);
            cmd.SetComputeIntParam(d.shader, ShaderIds.SortPaddedCount, d.paddedCount);
            cmd.SetComputeIntParam(d.shader, ShaderIds.SortBlockCount, d.blockCount);
            cmd.SetComputeIntParam(d.shader, ShaderIds.SortShift, d.shift);
            cmd.SetComputeIntParam(d.shader, ShaderIds.SortHistogramSize, d.histogramSize);
        }

        /// <summary>
        /// Pushes a compile time choice to a compute shader. A keyword the shader does not declare
        /// logs an error on every call, so an invalid one is skipped: that happens when the shader
        /// asset is older than the code, and a missing optimisation is a better outcome than a
        /// console full of errors.
        /// </summary>
        static void SetKeyword(ComputeShader shader, in LocalKeyword keyword, bool value)
        {
            if (shader != null && keyword.isValid)
                shader.SetKeyword(keyword, value);
        }

        static void SetBinConstants(ComputeCommandBuffer cmd, ComputePassData d)
        {
            cmd.SetComputeVectorParam(d.shader, ShaderIds.ScreenSize, d.screenSize);
            cmd.SetComputeIntParams(d.shader, ShaderIds.TileParams,
                d.tileParams.x, d.tileParams.y, d.tileParams.z, d.tileParams.w);
            cmd.SetComputeIntParam(d.shader, ShaderIds.TriCount, d.triCount);
            cmd.SetComputeIntParam(d.shader, ShaderIds.TileListCapacity, d.tileListCapacity);
            cmd.SetComputeIntParam(d.shader, ShaderIds.TileSize, d.tileSize);
            cmd.SetComputeIntParam(d.shader, ShaderIds.SegmentCount, d.segmentCount);
        }

        /// <summary>
        /// Picks the raster kernel for the configured tile size. A size whose kernel failed to
        /// compile on this platform (32 asks for 1024 threads per group) falls back to 8, and
        /// the fallback is written back so binning uses the size that is actually rasterized.
        /// </summary>
        int ResolveRasterKernel(ref int tileSize)
        {
            int slot = IndexOfTileSize(tileSize);
            if (slot >= 0 && m_KRaster[slot] >= 0)
                return m_KRaster[slot];

            int fallback = IndexOfTileSize(8);
            if (fallback >= 0 && m_KRaster[fallback] >= 0)
            {
                tileSize = 8;
                return m_KRaster[fallback];
            }

            return -1;
        }

        static int IndexOfTileSize(int tileSize)
        {
            switch (tileSize)
            {
                case 4: return 0;
                case 8: return 1;
                case 16: return 2;
                case 32: return 3;
                default: return -1;
            }
        }

        /// <summary>
        /// Dispatches, timed if the GPU timers are on. The sample brackets the dispatch alone and
        /// not the parameter binding above it, so the number is the hardware's time in the kernel
        /// rather than the cost of recording the pass.
        /// </summary>
        static void Dispatch(ComputeCommandBuffer cmd, ComputePassData d)
        {
            if (d.timer != null)
                cmd.BeginSample(d.timer);

            cmd.DispatchCompute(d.shader, d.kernel, d.groups.x, d.groups.y, d.groups.z);

            if (d.timer != null)
                cmd.EndSample(d.timer);
        }

        static void Dispatch(ComputeCommandBuffer cmd, SortPassData d, int groupsX)
        {
            if (d.timer != null)
                cmd.BeginSample(d.timer);

            cmd.DispatchCompute(d.shader, d.kernel, groupsX, 1, 1);

            if (d.timer != null)
                cmd.EndSample(d.timer);
        }

        static int DivUp(int value, int divisor) => (value + divisor - 1) / divisor;

        static BufferHandle CreateBuffer(RenderGraph renderGraph, int count, int stride, string name)
        {
            return renderGraph.CreateBuffer(new BufferDesc(count, stride) { name = name });
        }

    }

    /// <summary>Small integer vector, used to carry the tile counts into the pass data.</summary>
    public struct Vector4Int
    {
        public int x, y, z, w;
        public Vector4Int(int x, int y, int z, int w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
    }
}
