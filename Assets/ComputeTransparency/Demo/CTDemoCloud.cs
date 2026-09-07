using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace ComputeTransparency.Demo
{
    /// <summary>Which of the two rendering paths the demo is currently showing.</summary>
    public enum CTDemoPath
    {
        Compute,
        Traditional
    }

    /// <summary>
    /// The ball of alpha blended sprites the demo orbits around, feeding both the compute
    /// rasterizer and the hardware baseline from one generated set.
    /// </summary>
    /// <remarks>
    /// Changing the instance count never rebuilds what is already there. Every sprite is a pure
    /// function of its index and the seed, so growing the cloud only fills the indices that did
    /// not exist yet and shrinking it only lowers a count - the storage for the largest step on
    /// the ladder is allocated once, up front, and never resized. Changing the alpha does touch
    /// every sprite, because that is what the slider means, but it is a Burst job over two flat
    /// arrays rather than a teardown.
    /// </remarks>
    [ExecuteAlways]
    public class CTDemoCloud : MonoBehaviour
    {
        /// <summary>One sprite as generated. Alpha is deliberately absent: it is a live slider
        /// value applied while building the instances, so moving it costs no regeneration.</summary>
        public struct Item
        {
            public float3 position;
            public float2 size;
            public float3 rgb;
            public uint slice;
        }

        [Header("Content")]
        [Tooltip("Atlas both paths sample. The compute feature must be pointed at the same asset.")]
        public CTAtlas atlas;

        [Tooltip("Material for the hardware path. Expects CTReferenceSprite.mat, which must have " +
                 "GPU instancing enabled: a runtime created material is invisible to shader " +
                 "variant stripping and the instanced variant is then missing from the build.")]
        public Material instancedMaterial;

        [Tooltip("Instance counts the -/+ buttons step through. The largest one sets the allocation.")]
        public int[] countLadder = { 100, 1000, 5000, 10_000, 50_000, 100_000, 500_000, 1_000_000 };

        public int startStep = 3;

        [Header("Shape")]
        [Tooltip("Radius of the ball the sprites fill, uniformly by volume.")]
        public float radius = 11f;

        public Vector2 sizeRange = new Vector2(0.7f, 2.0f);

        [Tooltip("Sprites nearer the centre than this are pushed out, so the opaque object in " +
                 "the middle is not buried and the depth test stays visible.")]
        public float hollowRadius = 3.2f;

        [Range(0f, 1f)] public float alpha = 0.9f;

        public int sliceCount = 2;
        public int seed = 12345;

        [Tooltip("Path the demo starts on. Entering play mode reconstructs the cloud, so this is " +
                 "also the only way to open straight into the hardware baseline.")]
        public CTDemoPath startPath = CTDemoPath.Compute;

        NativeArray<Item> m_Items;
        CTSpriteBatch m_Compute;
        CTInstancedSpriteBatch m_Traditional;

        int m_Generated;
        int m_Count;
        int m_Step;
        CTDemoPath m_Path = CTDemoPath.Compute;
        bool m_Overdraw;

        public int Count => m_Count;
        public int Step => m_Step;
        public int StepCount => countLadder != null ? countLadder.Length : 0;

        public event Action Changed;

        void OnEnable()
        {
            Allocate();
            m_Step = Mathf.Clamp(startStep, 0, Mathf.Max(StepCount - 1, 0));
            m_Path = startPath;
            SetCount(CountForStep(m_Step));
            ApplyPath();
        }

        void OnDisable() => Release();

        // Immediate mode instanced drawing has to be re-issued every frame, and its depth sort
        // needs the camera where it will be when the frame renders, so this runs after whatever
        // moves the camera.
        void LateUpdate() => SubmitHardwareDraw(Camera.main);

        /// <summary>
        /// Issues the hardware path's instanced draw for one camera. Called once per frame from
        /// LateUpdate; exposed so an offscreen capture can drive it for its own camera, which
        /// immediate mode rendering otherwise gives no way to do.
        /// </summary>
        public void SubmitHardwareDraw(Camera camera)
        {
            if (m_Path == CTDemoPath.Traditional)
                m_Traditional?.Render(camera);
        }

        public CTDemoPath RenderPath
        {
            get => m_Path;
            set
            {
                if (m_Path == value)
                    return;
                m_Path = value;
                ApplyPath();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Show the hardware path's blends per pixel instead of the image. Ignored by the compute
        /// path, which produces its own debug views inside the rasterizer.
        /// </summary>
        public bool Overdraw
        {
            get => m_Overdraw;
            set
            {
                if (m_Overdraw == value)
                    return;
                m_Overdraw = value;
                if (m_Traditional != null)
                    m_Traditional.Overdraw = value;
            }
        }

        public float Alpha
        {
            get => alpha;
            set
            {
                float clamped = Mathf.Clamp01(value);
                if (Mathf.Approximately(clamped, alpha) && m_Generated > 0)
                    return;
                alpha = clamped;
                // Only the instances are rebuilt; the generated items never carried the alpha.
                Build(0, m_Generated);
                Changed?.Invoke();
            }
        }

        /// <summary>Moves one step along the instance ladder. Clamped, so the buttons never wrap.</summary>
        public void StepInstances(int direction)
        {
            int step = Mathf.Clamp(m_Step + direction, 0, Mathf.Max(StepCount - 1, 0));
            if (step == m_Step)
                return;

            m_Step = step;
            SetCount(CountForStep(step));
            Changed?.Invoke();
        }

        public void ResetDefaults()
        {
            m_Step = Mathf.Clamp(startStep, 0, Mathf.Max(StepCount - 1, 0));
            alpha = 0.9f;
            SetCount(CountForStep(m_Step));
            Build(0, m_Generated);
            Overdraw = false;
            RenderPath = startPath;
            Changed?.Invoke();
        }

        int CountForStep(int step)
        {
            if (countLadder == null || countLadder.Length == 0)
                return 0;
            return Mathf.Max(countLadder[Mathf.Clamp(step, 0, countLadder.Length - 1)], 0);
        }

        int Capacity
        {
            get
            {
                int max = 0;
                if (countLadder != null)
                    foreach (int c in countLadder)
                        max = Mathf.Max(max, c);
                return Mathf.Max(max, 1);
            }
        }

        void Allocate()
        {
            int capacity = Capacity;
            if (m_Items.IsCreated && m_Items.Length == capacity)
                return;

            Release();
            m_Items = new NativeArray<Item>(capacity, Allocator.Persistent);
            m_Compute = new CTSpriteBatch(capacity, "CT Demo Cloud");
            m_Traditional = new CTInstancedSpriteBatch(capacity, "CT Demo Cloud (Hardware)");
            m_Traditional.SetMaterial(instancedMaterial, atlas);
            m_Traditional.Overdraw = m_Overdraw;
            m_Traditional.WorldBounds = new Bounds(transform.position, Vector3.one * (radius * 4f));
            m_Generated = 0;
            m_Count = 0;
        }

        void Release()
        {
            if (m_Items.IsCreated)
                m_Items.Dispose();
            m_Compute?.Dispose();
            m_Compute = null;
            m_Traditional?.Dispose();
            m_Traditional = null;
            m_Generated = 0;
            m_Count = 0;
        }

        void SetCount(int count)
        {
            if (!m_Items.IsCreated)
                return;

            count = Mathf.Clamp(count, 0, m_Items.Length);

            // The only work a bigger cloud costs is the sprites that did not exist before.
            if (count > m_Generated)
            {
                Generate(m_Generated, count);
                Build(m_Generated, count);
                m_Generated = count;
            }

            m_Count = count;
            m_Compute.Count = count;
            m_Traditional.Count = count;
        }

        void Generate(int start, int end)
        {
            if (end <= start)
                return;

            new GenerateJob
            {
                start = start,
                seed = (uint)seed,
                radius = Mathf.Max(radius, 0.01f),
                hollowRadius = Mathf.Clamp(hollowRadius, 0f, radius),
                sizeRange = sizeRange,
                sliceCount = (uint)Mathf.Max(sliceCount, 1),
                items = m_Items
            }.Schedule(end - start, 128).Complete();
        }

        void Build(int start, int end)
        {
            if (end <= start || !m_Items.IsCreated)
                return;

            var handle = new BuildJob
            {
                start = start,
                alpha = alpha,
                origin = transform.position,
                items = m_Items,
                computeInstances = m_Compute.Instances,
                hardwareInstances = m_Traditional.Instances
            }.Schedule(end - start, 128);
            handle.Complete();

            m_Compute.MarkChanged();
            m_Traditional.MarkChanged();
        }

        void ApplyPath()
        {
            if (m_Compute == null || m_Traditional == null)
                return;

            m_Compute.Enabled = m_Path == CTDemoPath.Compute;
            m_Traditional.Enabled = m_Path == CTDemoPath.Traditional;
        }

        /// <summary>
        /// Places one sprite from its index alone. Everything here is a hash of the index, which
        /// is what makes growing the cloud additive: index 900 gets the same sprite whether the
        /// cloud holds 1000 or 10000.
        /// </summary>
        [BurstCompile]
        struct GenerateJob : IJobParallelFor
        {
            public int start;
            public uint seed;
            public float radius;
            public float hollowRadius;
            public float2 sizeRange;
            public uint sliceCount;

            [NativeDisableParallelForRestriction] public NativeArray<Item> items;

            public void Execute(int i)
            {
                int index = start + i;
                uint id = (uint)index + 1u;

                float u0 = Random(id, 0u);
                float u1 = Random(id, 1u);
                float u2 = Random(id, 2u);
                float u3 = Random(id, 3u);
                float u4 = Random(id, 4u);
                float u5 = Random(id, 5u);

                // Uniform by volume: the cube root undoes the r^2 growth of a shell's area, so
                // the ball does not end up dense at the centre and empty at the rim.
                float distance = math.lerp(hollowRadius, radius, math.pow(u0, 1f / 3f));
                float z = u1 * 2f - 1f;
                float phi = u2 * 2f * math.PI;
                float ring = math.sqrt(math.max(1f - z * z, 0f));
                float3 direction = new float3(ring * math.cos(phi), ring * math.sin(phi), z);

                float size = math.lerp(sizeRange.x, sizeRange.y, u3);

                items[index] = new Item
                {
                    position = direction * distance,
                    size = new float2(size, size),
                    // Saturated hues: averaging many random per channel values converges on white
                    // and hides whether the blend is doing anything.
                    rgb = HsvToRgb(u4, 0.85f, 1f),
                    slice = (uint)math.min((int)(u5 * sliceCount), (int)sliceCount - 1)
                };
            }

            float Random(uint id, uint stream)
            {
                uint x = id * 0x9E3779B9u + stream * 0x85EBCA6Bu + seed * 0xC2B2AE35u;
                x ^= x >> 16; x *= 0x7FEB352Du;
                x ^= x >> 15; x *= 0x846CA68Bu;
                x ^= x >> 16;
                return (x >> 8) * (1f / 16777216f);
            }

            static float3 HsvToRgb(float h, float s, float v)
            {
                float3 k = new float3(1f, 2f / 3f, 1f / 3f);
                float3 p = math.abs(math.frac(h + k) * 6f - 3f);
                return v * math.lerp(new float3(1f), math.saturate(p - 1f), s);
            }
        }

        /// <summary>
        /// Turns generated items into the two instance formats. Both are written in one pass so
        /// switching paths costs nothing at the moment of the switch, and so the two are provably
        /// built from the same numbers.
        /// </summary>
        [BurstCompile]
        struct BuildJob : IJobParallelFor
        {
            public int start;
            public float alpha;
            public float3 origin;

            [ReadOnly] public NativeArray<Item> items;

            [NativeDisableParallelForRestriction] public NativeArray<CTSpriteInstance> computeInstances;
            [NativeDisableParallelForRestriction] public NativeArray<CTInstancedSpriteBatch.Instance> hardwareInstances;

            public void Execute(int i)
            {
                int index = start + i;
                Item item = items[index];
                float3 centre = origin + item.position;

                // Quantise once, here, and hand the same eight bit numbers to both paths: the
                // compute rasterizer packs its tint to RGBA8 and this is what it would unpack.
                uint4 quantised = (uint4)math.round(math.saturate(new float4(item.rgb, alpha)) * 255f);

                computeInstances[index] = new CTSpriteInstance
                {
                    center = new Vector4(centre.x, centre.y, centre.z, 0f),
                    axisX = new Vector4(0f, 0f, 0f, item.size.x * 0.5f),
                    axisY = new Vector4(0f, 0f, 0f, item.size.y * 0.5f),
                    uvRect = new Vector4(0f, 0f, 1f, 1f),
                    color = quantised.x | (quantised.y << 8) | (quantised.z << 16) | (quantised.w << 24),
                    slice = item.slice,
                    flags = CTSpriteInstance.FlagBillboard,
                    unused = 0u
                };

                hardwareInstances[index] = CTInstancedSpriteBatch.Instance.Create(
                    centre, item.size, (float4)quantised * (1f / 255f), item.slice);
            }
        }
    }
}
