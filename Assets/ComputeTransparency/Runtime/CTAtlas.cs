using System.Collections.Generic;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// Source textures for the compute rasterizer, packed into a Texture2DArray at load time.
    /// An array rather than bindless textures because bindless is not available on the mobile
    /// targets this renderer is aimed at; every sprite carries a slice index instead.
    /// All entries have to share size and format.
    /// </summary>
    [CreateAssetMenu(menuName = "Compute Transparency/Atlas", fileName = "CTAtlas")]
    public class CTAtlas : ScriptableObject
    {
        [SerializeField] List<Texture2D> m_Textures = new List<Texture2D>();

        Texture2DArray m_Array;

        public int SliceCount => m_Textures.Count;

        public Texture2DArray Texture
        {
            get
            {
                if (m_Array == null)
                    Build();
                return m_Array;
            }
        }

        public Vector2 TexelSize
        {
            get
            {
                var tex = Texture;
                return tex != null ? new Vector2(tex.width, tex.height) : Vector2.one;
            }
        }

        public int IndexOf(Texture2D texture) => m_Textures.IndexOf(texture);

        /// <summary>Drops the built array so the next access repacks it from the source textures.</summary>
        public void Invalidate()
        {
            if (m_Array != null)
            {
                DestroyImmediate(m_Array);
                m_Array = null;
            }
        }

        void Build()
        {
            if (m_Textures.Count == 0)
                return;

            Texture2D first = null;
            foreach (var t in m_Textures)
            {
                if (t != null)
                {
                    first = t;
                    break;
                }
            }

            if (first == null)
                return;

            // Mip count has to match the slices exactly for Graphics.CopyTexture to accept them.
            m_Array = new Texture2DArray(first.width, first.height, m_Textures.Count, first.format, first.mipmapCount > 1)
            {
                name = name + " (Array)",
                filterMode = first.filterMode,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int i = 0; i < m_Textures.Count; i++)
            {
                var t = m_Textures[i];
                if (t == null)
                    continue;

                if (t.width != first.width || t.height != first.height || t.format != first.format ||
                    t.mipmapCount != first.mipmapCount)
                {
                    Debug.LogError($"[ComputeTransparency] Atlas '{name}' slice {i} ('{t.name}') does not match " +
                                   $"the first slice ({first.width}x{first.height}, {first.format}). Skipped.", this);
                    continue;
                }

                Graphics.CopyTexture(t, 0, m_Array, i);
            }

            m_Array.Apply(false, false);
        }

        void OnDisable()
        {
            if (m_Array != null)
            {
                DestroyImmediate(m_Array);
                m_Array = null;
            }
        }
    }
}
