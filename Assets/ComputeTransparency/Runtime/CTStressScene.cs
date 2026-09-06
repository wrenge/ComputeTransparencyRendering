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

        [Tooltip("Shader for the traditional group. Expects ComputeTransparency/Reference Sprite.")]
        public Shader referenceShader;

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
        Transform m_TraditionalRoot;
        CTReferenceSpriteBatch m_Reference;

        Vector3 m_ComputeOffset;

        public int ItemCount => m_Count;

        void OnEnable() => Rebuild();

        void OnDisable() => Clear();

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
            var go = new GameObject("Traditional Group", typeof(MeshFilter), typeof(MeshRenderer));
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            m_TraditionalRoot = go.transform;

            m_Reference = go.AddComponent<CTReferenceSpriteBatch>();
            m_Reference.SetItems(m_Items, m_Count, atlas, referenceShader);
        }

        void ApplyMode()
        {
            if (m_Batch == null || m_TraditionalRoot == null)
                return;

            bool showCompute = mode != CTStressMode.Traditional;
            bool showTraditional = mode != CTStressMode.Compute;

            m_Batch.Enabled = showCompute;
            m_TraditionalRoot.gameObject.SetActive(showTraditional);

            // Only pull the groups apart when both are visible; otherwise each one sits where
            // the other one would, so flipping between the modes is a straight A/B.
            Vector3 offset = mode == CTStressMode.Both ? sideBySideOffset * 0.5f : Vector3.zero;
            ApplyComputeOffset(-offset);
            m_TraditionalRoot.localPosition = offset;

            // The reference mesh billboards against the current camera and its offset just
            // moved, so refresh it here rather than waiting for a tick that an editor driven
            // render never gets.
            if (showTraditional && m_Reference != null)
                m_Reference.RebuildNow();
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

            if (m_TraditionalRoot != null)
                DestroySafely(m_TraditionalRoot.gameObject);

            m_TraditionalRoot = null;
            m_Reference = null;
            m_ComputeOffset = Vector3.zero;
            m_Count = 0;
        }

        static void DestroySafely(Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }
    }
}
