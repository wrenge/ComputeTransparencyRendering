using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
            bool uploaded = false;

            m_Array = new Texture2DArray(first.width, first.height, m_Textures.Count, first.format, first.mipmapCount > 1)
            {
                name = name + " (Array)",
                filterMode = first.filterMode,
                wrapMode = TextureWrapMode.Clamp
            };

            // Texture2D to Texture2DArray is a copy between different texture types, which not
            // every device allows. Where it is missing the raw bytes are uploaded instead, which
            // needs the source marked readable - so say which of the two is unavailable rather
            // than leaving a silently black atlas on a device nobody can attach a debugger to.
            bool canCopy = (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0;
            int copied = 0;

            for (int i = 0; i < m_Textures.Count; i++)
            {
                var t = m_Textures[i];
                if (t == null)
                    continue;

                if (t.width != first.width || t.height != first.height || t.format != first.format ||
                    t.mipmapCount != first.mipmapCount)
                {
                    Debug.LogError($"[ComputeTransparency] Atlas '{name}' slice {i} ('{t.name}') does not match " +
                                   $"the first slice ({first.width}x{first.height}, {first.format}, " +
                                   $"{first.mipmapCount} mips). It is {t.width}x{t.height}, {t.format}, " +
                                   $"{t.mipmapCount} mips. Skipped - that slice will sample black.", this);
                    continue;
                }

                if (canCopy)
                {
                    Graphics.CopyTexture(t, 0, m_Array, i);
                    copied++;
                }
                else if (t.isReadable)
                {
                    for (int mip = 0; mip < t.mipmapCount; mip++)
                        m_Array.SetPixelData(t.GetPixelData<byte>(mip), mip, i);
                    copied++;
                    uploaded = true;
                }
                else
                {
                    Debug.LogError($"[ComputeTransparency] Atlas '{name}' cannot be packed on this device: " +
                                   $"Graphics.CopyTexture does not support copying between texture types " +
                                   $"(copyTextureSupport = {SystemInfo.copyTextureSupport}) and slice {i} " +
                                   $"('{t.name}') is not readable. Enable Read/Write on the source textures.", this);
                }
            }

            // Only when the pixels came in through the CPU side: after Graphics.CopyTexture the
            // data is already on the GPU and there is nothing to upload, while the texture's CPU
            // side is still the zeroes it was allocated with.
            if (uploaded)
                m_Array.Apply(false, false);

            if (copied == 0)
                Debug.LogError($"[ComputeTransparency] Atlas '{name}' packed no slices; everything " +
                               $"sampling it will come out black.", this);
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
