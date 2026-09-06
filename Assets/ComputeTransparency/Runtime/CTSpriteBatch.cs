using System;
using Unity.Collections;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// A block of sprites drawn by the compute rasterizer without a GameObject each. Write
    /// <see cref="CTSpriteInstance"/>s into <see cref="Instances"/>, set <see cref="Count"/>,
    /// and call <see cref="MarkChanged"/> whenever the contents change.
    /// </summary>
    /// <remarks>
    /// The renderer only re-uploads a batch whose version has moved, so a batch that is written
    /// once and left alone costs nothing per frame beyond the depth sort. Billboards are
    /// oriented on the GPU, so even a moving camera does not make the data stale.
    ///
    /// Dispose the batch when done; it holds a native allocation and a registration with
    /// <see cref="CTSpriteRegistry"/>.
    /// </remarks>
    public sealed class CTSpriteBatch : IDisposable
    {
        NativeArray<CTSpriteInstance> m_Instances;
        int m_Count;
        int m_Version;
        bool m_Enabled = true;

        public string Name { get; }

        public CTSpriteBatch(int capacity, string name = null)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "A batch needs room for at least one sprite.");

            Name = name ?? "CT Sprite Batch";
            m_Instances = new NativeArray<CTSpriteInstance>(capacity, Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            CTSpriteRegistry.AddBatch(this);
        }

        /// <summary>Writable instance storage. Only the first <see cref="Count"/> entries are drawn.</summary>
        public NativeArray<CTSpriteInstance> Instances => m_Instances;

        public int Capacity => m_Instances.IsCreated ? m_Instances.Length : 0;

        /// <summary>How many entries of <see cref="Instances"/> to draw.</summary>
        public int Count
        {
            get => m_Count;
            set
            {
                int clamped = Mathf.Clamp(value, 0, Capacity);
                if (clamped == m_Count)
                    return;
                m_Count = clamped;
                MarkChanged();
            }
        }

        /// <summary>Skip this batch entirely without disposing it.</summary>
        public bool Enabled
        {
            get => m_Enabled && m_Instances.IsCreated;
            set
            {
                if (m_Enabled == value)
                    return;
                m_Enabled = value;
                MarkChanged();
            }
        }

        /// <summary>Bumped whenever the contents change; the renderer re-uploads on a change.</summary>
        public int Version => m_Version;

        /// <summary>Call after writing to <see cref="Instances"/> so the renderer re-uploads.</summary>
        public void MarkChanged() => m_Version++;

        /// <summary>Grows the storage, preserving the existing entries.</summary>
        public void EnsureCapacity(int capacity)
        {
            if (Capacity >= capacity)
                return;

            var grown = new NativeArray<CTSpriteInstance>(Mathf.NextPowerOfTwo(capacity),
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (m_Instances.IsCreated)
            {
                NativeArray<CTSpriteInstance>.Copy(m_Instances, grown, m_Count);
                m_Instances.Dispose();
            }
            m_Instances = grown;
            MarkChanged();
        }

        public void Dispose()
        {
            CTSpriteRegistry.RemoveBatch(this);
            if (m_Instances.IsCreated)
                m_Instances.Dispose();
            m_Count = 0;
        }
    }
}
