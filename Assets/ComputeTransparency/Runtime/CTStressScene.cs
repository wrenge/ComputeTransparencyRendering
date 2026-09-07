using Unity.Mathematics;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>Which of the two rendering paths the stress scene shows.</summary>
    public enum CTStressMode
    {
        Compute,
        Traditional,
        Both
    }

    /// <summary>
    /// Builds one set of sprites and renders it two ways: through the compute rasterizer and
    /// through URP's transparent queue. Both groups read the same generated positions, sizes,
    /// atlas slices and tints, so anything that differs between them is down to the renderer.
    /// </summary>
    [ExecuteAlways]
    public class CTStressScene : MonoBehaviour
    {
        [Header("Content")]
        [Tooltip("Atlas both paths sample. The compute feature must be pointed at the same asset.")]
        public CTAtlas atlas;

        [Tooltip("Material for the traditional group. Expects CTReferenceSprite.mat, which must " +
                 "have GPU instancing enabled so the build keeps the instanced shader variant.")]
        public Material referenceMaterial;

        [Min(0)] public int count = 4000;
        public Vector3 extents = new Vector3(8f, 5f, 20f);
        public Vector2 sizeRange = new Vector2(0.6f, 1.6f);

        [Tooltip("Sprite alpha. Values close to 1 are what the early-out is built for.")]
        [Range(0f, 1f)] public float alpha = 0.9f;

        public int sliceCount = 2;
        public int seed = 12345;

        [Header("Comparison")]
        public CTStressMode mode = CTStressMode.Compute;

        [Tooltip("Separation between the two groups while both are shown. Zero puts them in " +
                 "exactly the same place, which is what you want for A/B flipping.")]
        public Vector3 sideBySideOffset = Vector3.zero;

        [Tooltip("Tick to regenerate. Changing count, seed or extents needs a rebuild.")]
        public bool rebuild;

        CTStressItem[] m_Items = System.Array.Empty<CTStressItem>();
        int m_Count;

        CTSpriteBatch m_Batch;
        CTInstancedSpriteBatch m_Traditional;

        Vector3 m_ComputeOffset;

        public int ItemCount => m_Count;

        void OnEnable() => Rebuild();

        void OnDisable() => Clear();

        // Immediate mode instanced drawing has to be re-issued every frame, and the depth sort
        // inside it needs the camera where it will actually be when the frame renders.
        void LateUpdate() => m_Traditional?.Render(Camera.main);

        void OnValidate()
        {
            if (rebuild)
            {
                rebuild = false;
                Rebuild();
            }
            else
            {
                ApplyMode();
            }
        }

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            Clear();
            Generate();
            BuildComputeGroup();
            BuildTraditionalGroup();
            ApplyMode();
        }

        public void SetMode(CTStressMode value)
        {
            mode = value;
            ApplyMode();
        }

        void Generate()
        {
            var random = new System.Random(seed);
            float Next() => (float)random.NextDouble();

            if (m_Items.Length < count)
                m_Items = new CTStressItem[Mathf.Max(count, 64)];
            m_Count = count;

            for (int i = 0; i < count; i++)
            {
                float size = Mathf.Lerp(sizeRange.x, sizeRange.y, Next());
                // Saturated hues: averaging many random per channel values converges on white
                // and hides whether the blend is doing anything.
                var tint = Color.HSVToRGB(Next(), 0.85f, 1f);
                tint.a = alpha;

                m_Items[i] = new CTStressItem
                {
                    position = new Vector3(
                        (Next() * 2f - 1f) * extents.x,
                        (Next() * 2f - 1f) * extents.y,
                        Next() * extents.z),
                    halfSize = new Vector2(size * 0.5f, size * 0.5f),
                    slice = Mathf.Min((int)(Next() * sliceCount), Mathf.Max(sliceCount - 1, 0)),
                    color = tint
                };
            }
        }

        void BuildComputeGroup()
        {
            // One batch instead of a GameObject per sprite. Billboards are oriented in the
            // setup kernel, so this data is written once and never touched again, no matter
            // where the camera goes.
            m_Batch = new CTSpriteBatch(Mathf.Max(m_Count, 1), "CT Stress Scene");

            var instances = m_Batch.Instances;
            for (int i = 0; i < m_Count; i++)
            {
                var item = m_Items[i];
                instances[i] = CTSpriteInstance.Billboard(item.position, item.halfSize * 2f,
                                                          item.color, item.slice);
            }

            m_Batch.Count = m_Count;
            m_Batch.MarkChanged();
        }

        void BuildTraditionalGroup()
        {
            m_Traditional = new CTInstancedSpriteBatch(Mathf.Max(m_Count, 1), "CT Stress Traditional");
            m_Traditional.SetMaterial(referenceMaterial, atlas);

            var instances = m_Traditional.Instances;
            for (int i = 0; i < m_Count; i++)
                instances[i] = ToInstance(m_Items[i]);

            m_Traditional.Count = m_Count;
            m_Traditional.MarkChanged();
        }

        /// <summary>
        /// Builds the instance the hardware path draws. The transform carries the centre and the
        /// full size only; the quad is turned towards the camera in the vertex shader, exactly
        /// as the compute path's setup kernel does it.
        /// </summary>
        static CTInstancedSpriteBatch.Instance ToInstance(in CTStressItem item)
        {
            // Round trip the tint through 8 bits so it matches the compute path's packed RGBA8
            // and neither side gets a precision advantage.
            Color32 quantised = item.color;
            return CTInstancedSpriteBatch.Instance.Create(
                item.position,
                new float2(item.halfSize.x * 2f, item.halfSize.y * 2f),
                new float4(quantised.r, quantised.g, quantised.b, quantised.a) * (1f / 255f),
                item.slice);
        }

        void ApplyMode()
        {
            if (m_Batch == null || m_Traditional == null)
                return;

            bool showCompute = mode != CTStressMode.Traditional;
            bool showTraditional = mode != CTStressMode.Compute;

            m_Batch.Enabled = showCompute;
            m_Traditional.Enabled = showTraditional;

            // Only pull the groups apart when both are visible; otherwise each one sits where
            // the other one would, so flipping between the modes is a straight A/B.
            Vector3 offset = mode == CTStressMode.Both ? sideBySideOffset * 0.5f : Vector3.zero;
            ApplyComputeOffset(-offset);
            // The hardware path applies its offset while gathering the sorted copy, so nothing
            // has to be rewritten here.
            m_Traditional.Offset = offset;
        }

        void ApplyComputeOffset(Vector3 offset)
        {
            if (m_Batch == null || offset == m_ComputeOffset)
                return;

            var instances = m_Batch.Instances;
            for (int i = 0; i < m_Count; i++)
            {
                var instance = instances[i];
                Vector3 position = m_Items[i].position + offset;
                instance.center = new Vector4(position.x, position.y, position.z, instance.center.w);
                instances[i] = instance;
            }
            m_Batch.MarkChanged();
            m_ComputeOffset = offset;
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            m_Batch?.Dispose();
            m_Batch = null;

            m_Traditional?.Dispose();
            m_Traditional = null;

            m_ComputeOffset = Vector3.zero;
            m_Count = 0;
        }
    }
}
