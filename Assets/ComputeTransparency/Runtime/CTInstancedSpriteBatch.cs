using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ComputeTransparency
{
    /// <summary>
    /// The baseline the compute rasterizer is measured against: the same billboarded quads,
    /// the same Texture2DArray, blended by the hardware through URP's transparent queue.
    /// </summary>
    /// <remarks>
    /// Instanced rather than baked into one mesh. Baking rewrote four vertices per sprite every
    /// frame because billboards follow the camera, so the baseline was paying a mesh upload that
    /// has nothing to do with what is being measured. Here the per instance data is a transform
    /// plus a tint, the quad is oriented in the vertex shader, and the only per frame CPU work
    /// is the depth sort the transparent queue genuinely requires - which is exactly the work
    /// the compute path also has to do.
    ///
    /// The sort produces a separate draw ordered copy rather than reordering in place, so
    /// <see cref="Instances"/> stays index aligned with whatever generated it and can be updated
    /// incrementally.
    /// </remarks>
    public sealed class CTInstancedSpriteBatch : IDisposable
    {
        /// <summary>
        /// One instance as <c>Graphics.RenderMeshInstanced</c> consumes it: nothing but the object
        /// to world matrix, with the tint and the atlas slice packed into the elements a translate
        /// plus a per axis scale leaves empty.
        /// </summary>
        /// <remarks>
        /// The obvious way to carry a tint is an instanced property block alongside the matrix,
        /// which <c>RenderMeshInstanced</c> documents. It does not arrive: the draw renders, the
        /// matrices are per instance, and every <c>UNITY_ACCESS_INSTANCED_PROP</c> reads the
        /// material's own value instead, so the whole cloud comes out one colour. The transform is
        /// the piece of per instance data that is delivered reliably, and a translate plus a per
        /// axis scale uses six of its sixteen elements, so the tint and the slice ride in the six
        /// off diagonal ones the sprite has no use for.
        ///
        /// The cost is that the matrix is no longer a well formed transform. Nothing here needs it
        /// to be - the vertex shader reads the elements out one at a time and never multiplies by
        /// it, and the sprites are unlit, so the inverse Unity derives for <c>unity_WorldToObject</c>
        /// is never sampled. The only visible effect is on culling, which grows each instance's
        /// bounds by at most a unit.
        /// </remarks>
        public struct Instance
        {
            public float4x4 objectToWorld;

            /// <summary>
            /// Packs one sprite. Element names below are HLSL's row-column ones, which is how
            /// <c>CTReferenceSprite.shader</c> unpacks them.
            /// </summary>
            public static Instance Create(float3 centre, float2 size, float4 color, float slice)
            {
                float4x4 m = float4x4.identity;

                m.c0.x = size.x;    // _m00, full width
                m.c1.y = size.y;    // _m11, full height
                m.c3.xyz = centre;  // _m03, _m13, _m23

                m.c1.x = color.x;   // _m01
                m.c2.x = color.y;   // _m02
                m.c0.y = color.z;   // _m10
                m.c2.y = color.w;   // _m12
                m.c0.z = slice;     // _m20

                return new Instance { objectToWorld = m };
            }
        }

        static readonly List<CTInstancedSpriteBatch> s_Batches = new List<CTInstancedSpriteBatch>();

        static class ShaderIds
        {
            public static readonly int Instances = Shader.PropertyToID("_CTOverdrawInstances");
            public static readonly int Offset = Shader.PropertyToID("_CTOverdrawOffset");
        }

        NativeArray<Instance> m_Instances;
        NativeArray<Instance> m_Draw;
        NativeArray<ulong> m_Keys;

        Mesh m_Quad;
        Material m_Material;
        int m_Count;
        int m_Version;

        GraphicsBuffer m_OverdrawBuffer;
        MaterialPropertyBlock m_OverdrawProperties;
        int m_UploadedVersion = -1;
        int m_UploadedCount = -1;

        public string Name { get; }

        /// <summary>Every live batch, so the renderer feature can find the ones asking for the
        /// overdraw view without the scene having to hand them over.</summary>
        public static IReadOnlyList<CTInstancedSpriteBatch> Batches => s_Batches;

        public CTInstancedSpriteBatch(int capacity, string name = null)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "A batch needs room for at least one sprite.");

            Name = name ?? "CT Instanced Sprites";
            m_Instances = new NativeArray<Instance>(capacity, Allocator.Persistent);
            m_Draw = new NativeArray<Instance>(capacity, Allocator.Persistent);
            m_Keys = new NativeArray<ulong>(capacity, Allocator.Persistent);
            m_Quad = BuildQuad();
            s_Batches.Add(this);
        }

        /// <summary>Writable instance storage. Only the first <see cref="Count"/> entries are drawn.</summary>
        public NativeArray<Instance> Instances => m_Instances;

        public int Capacity => m_Instances.IsCreated ? m_Instances.Length : 0;

        public int Count
        {
            get => m_Count;
            set => m_Count = Mathf.Clamp(value, 0, Capacity);
        }

        /// <summary>Bumped whenever <see cref="Instances"/> changes, so the overdraw view knows
        /// when its GPU copy is stale. The shaded draw reads the native array directly and does
        /// not care.</summary>
        public int Version => m_Version;

        public void MarkChanged() => m_Version++;

        /// <summary>
        /// Count the fragments instead of shading them. The batch stops submitting its own draw
        /// and instead keeps a GPU copy of the instances for the renderer feature's overdraw pass,
        /// which draws them into its own target and maps the count through the heat ramp.
        /// </summary>
        public bool Overdraw { get; set; }

        internal GraphicsBuffer OverdrawBuffer => m_OverdrawBuffer;

        internal MaterialPropertyBlock OverdrawProperties => m_OverdrawProperties;

        /// <summary>Skip the draw without disposing the batch.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Extra offset applied to every sprite, used when both paths are shown side by side.</summary>
        public Vector3 Offset { get; set; }

        /// <summary>Bounds handed to the renderer. Per instance culling is not done, so this covers everything.</summary>
        public Bounds WorldBounds { get; set; } = new Bounds(Vector3.zero, Vector3.one * 10000f);

        public void SetMaterial(Shader shader, CTAtlas atlas)
        {
            if (shader == null)
                return;

            if (m_Material == null || m_Material.shader != shader)
            {
                CoreUtils.Destroy(m_Material);
                m_Material = new Material(shader) { name = Name, hideFlags = HideFlags.DontSave };
                // Without this the draw falls back to one submission per sprite.
                m_Material.enableInstancing = true;
            }

            if (atlas != null)
                m_Material.SetTexture("_BaseMap", atlas.Texture);
        }

        /// <summary>
        /// Sorts back to front for <paramref name="camera"/> and submits the draw. Call once per
        /// frame, after whatever moves the camera.
        /// </summary>
        public void Render(Camera camera)
        {
            if (!Enabled || m_Count == 0 || camera == null || m_Quad == null)
                return;

            if (Overdraw)
            {
                // No sort and no draw: an additive count does not care about order, and the draw
                // itself belongs to the renderer feature, which owns the target it goes into.
                SyncOverdrawBuffer();
                return;
            }

            if (m_Material == null)
                return;

            var cameraTransform = camera.transform;

            var keys = new DepthKeyJob
            {
                instances = m_Instances,
                keys = m_Keys,
                cameraPosition = cameraTransform.position,
                cameraForward = cameraTransform.forward
            }.Schedule(m_Count, 256);

            // Sorting a packed key rather than the instances keeps the comparison to one 64 bit
            // integer and leaves the reorder to a single linear gather.
            var sorted = m_Keys.GetSubArray(0, m_Count).SortJob().Schedule(keys);

            new GatherJob
            {
                source = m_Instances,
                keys = m_Keys,
                offset = new float3(Offset.x, Offset.y, Offset.z),
                draw = m_Draw
            }.Schedule(m_Count, 256, sorted).Complete();

            var parameters = new RenderParams(m_Material)
            {
                worldBounds = WorldBounds,
                // Bound to one camera because the order was computed for that camera; a second
                // view would blend the same sprites in the wrong order.
                camera = camera,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off
            };

            Graphics.RenderMeshInstanced(parameters, m_Quad, 0, m_Draw, m_Count);
        }

        /// <summary>
        /// Mirrors the instances into a GraphicsBuffer for the overdraw pass, re-uploading only
        /// when they have actually changed. A static cloud uploads once.
        /// </summary>
        void SyncOverdrawBuffer()
        {
            int stride = UnsafeUtility.SizeOf<Instance>();
            if (m_OverdrawBuffer == null || m_OverdrawBuffer.count < m_Count)
            {
                m_OverdrawBuffer?.Dispose();
                m_OverdrawBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                                                      Mathf.Max(m_Count, 1), stride);
                m_UploadedVersion = -1;
                m_UploadedCount = -1;
            }

            if (m_UploadedVersion != m_Version || m_UploadedCount != m_Count)
            {
                m_OverdrawBuffer.SetData(m_Instances, 0, 0, m_Count);
                m_UploadedVersion = m_Version;
                m_UploadedCount = m_Count;
            }

            m_OverdrawProperties ??= new MaterialPropertyBlock();
            m_OverdrawProperties.SetBuffer(ShaderIds.Instances, m_OverdrawBuffer);
            m_OverdrawProperties.SetVector(ShaderIds.Offset, Offset);
        }

        public void EnsureCapacity(int capacity)
        {
            if (Capacity >= capacity)
                return;

            int grown = Mathf.NextPowerOfTwo(capacity);
            var instances = new NativeArray<Instance>(grown, Allocator.Persistent);
            if (m_Instances.IsCreated)
            {
                NativeArray<Instance>.Copy(m_Instances, instances, m_Count);
                m_Instances.Dispose();
            }
            m_Instances = instances;

            if (m_Draw.IsCreated)
                m_Draw.Dispose();
            m_Draw = new NativeArray<Instance>(grown, Allocator.Persistent);

            if (m_Keys.IsCreated)
                m_Keys.Dispose();
            m_Keys = new NativeArray<ulong>(grown, Allocator.Persistent);
            MarkChanged();
        }

        public void Dispose()
        {
            s_Batches.Remove(this);
            m_OverdrawBuffer?.Dispose();
            m_OverdrawBuffer = null;
            if (m_Instances.IsCreated) m_Instances.Dispose();
            if (m_Draw.IsCreated) m_Draw.Dispose();
            if (m_Keys.IsCreated) m_Keys.Dispose();
            CoreUtils.Destroy(m_Material);
            CoreUtils.Destroy(m_Quad);
            m_Material = null;
            m_Quad = null;
            m_Count = 0;
        }

        /// <summary>A unit quad in the XY plane. The vertex shader turns it towards the camera.</summary>
        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "CT Instanced Quad", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>
        /// Packs view depth and instance index into one sortable key. The float bits are made
        /// monotonic first: negative floats compare backwards as raw bits, and a sprite behind
        /// the camera does produce one.
        /// </summary>
        [BurstCompile]
        struct DepthKeyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Instance> instances;
            [WriteOnly] public NativeArray<ulong> keys;
            public float3 cameraPosition;
            public float3 cameraForward;

            public void Execute(int i)
            {
                float3 centre = instances[i].objectToWorld.c3.xyz;
                // Ascending on the negated depth is back to front.
                float key = -math.dot(centre - cameraPosition, cameraForward);
                uint bits = math.asuint(key);
                bits = (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
                keys[i] = ((ulong)bits << 32) | (uint)i;
            }
        }

        [BurstCompile]
        struct GatherJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Instance> source;
            [ReadOnly] public NativeArray<ulong> keys;
            public float3 offset;
            [WriteOnly] public NativeArray<Instance> draw;

            public void Execute(int i)
            {
                var instance = source[(int)(keys[i] & 0xFFFFFFFFu)];
                instance.objectToWorld.c3.xyz += offset;
                draw[i] = instance;
            }
        }
    }
}
