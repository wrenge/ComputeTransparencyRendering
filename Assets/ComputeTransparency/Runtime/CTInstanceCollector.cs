using System;
using Unity.Collections;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// Gathers every registered batch into the two buffers the setup kernel reads: the instance
    /// data itself, and a list of instance indices in front to back order.
    /// </summary>
    /// <remarks>
    /// The instance buffer is only re-uploaded when some batch reports a new version, and the
    /// order is only recomputed when the camera has actually moved, so a static scene viewed
    /// from a static camera costs nothing per frame. Sorting indices rather than the instances
    /// themselves keeps the instance upload a straight block copy: no sprite is ever touched
    /// individually on the CPU.
    /// </remarks>
    sealed class CTInstanceCollector : IDisposable
    {
        NativeArray<CTSpriteInstance> m_Combined;
        float[] m_Keys = Array.Empty<float>();
        int[] m_Indices = Array.Empty<int>();

        GraphicsBuffer m_InstanceBuffer;
        GraphicsBuffer m_IndexBuffer;

        int m_ContentSignature = -1;
        Vector3 m_SortedFromPosition;
        Vector3 m_SortedAlongForward;
        bool m_HasSortedOnce;

        public GraphicsBuffer InstanceBuffer => m_InstanceBuffer;
        public GraphicsBuffer IndexBuffer => m_IndexBuffer;
        public int Count { get; private set; }

        /// <summary>
        /// True when the instance data or the camera moved since the last frame, so the depth
        /// order has to be rebuilt. The GPU sort path reads this too, so both sorts skip the
        /// same frames and a measurement of one against the other stays fair.
        /// </summary>
        public bool NeedsResort { get; private set; }

        /// <summary>Returns false when there is nothing to draw.</summary>
        /// <param name="sortOnCpu">
        /// When false the index buffer is left alone; the caller is expected to fill it with a
        /// GPU sort instead.
        /// </param>
        public bool Collect(Camera camera, bool sortOnCpu)
        {
            CTSpriteRegistry.SyncComponents();

            var batches = CTSpriteRegistry.Batches;
            int total = 0;
            int signature = 17;
            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                if (!batch.Enabled || batch.Count == 0)
                    continue;
                total += batch.Count;
                unchecked
                {
                    signature = signature * 31 + batch.Count;
                    signature = signature * 31 + batch.Version;
                }
            }

            Count = total;
            if (total == 0)
            {
                NeedsResort = false;
                return false;
            }

            EnsureCapacity(total);

            bool contentChanged = signature != m_ContentSignature;
            if (contentChanged)
            {
                int offset = 0;
                for (int i = 0; i < batches.Count; i++)
                {
                    var batch = batches[i];
                    if (!batch.Enabled || batch.Count == 0)
                        continue;
                    // Block copy per batch, not per sprite.
                    NativeArray<CTSpriteInstance>.Copy(batch.Instances, 0, m_Combined, offset, batch.Count);
                    offset += batch.Count;
                }

                m_InstanceBuffer.SetData(m_Combined, 0, 0, total);
                m_ContentSignature = signature;
            }

            var cameraTransform = camera.transform;
            Vector3 camPos = cameraTransform.position;
            Vector3 camFwd = cameraTransform.forward;

            bool cameraMoved = !m_HasSortedOnce
                            || m_SortedFromPosition != camPos
                            || m_SortedAlongForward != camFwd;

            NeedsResort = contentChanged || cameraMoved;

            if (NeedsResort && sortOnCpu)
            {
                for (int i = 0; i < total; i++)
                {
                    Vector4 centre = m_Combined[i].center;
                    // View space distance along the camera's forward axis: ascending is front to back.
                    m_Keys[i] = (centre.x - camPos.x) * camFwd.x
                              + (centre.y - camPos.y) * camFwd.y
                              + (centre.z - camPos.z) * camFwd.z;
                    m_Indices[i] = i;
                }

                Array.Sort(m_Keys, m_Indices, 0, total);
                m_IndexBuffer.SetData(m_Indices, 0, 0, total);

                m_SortedFromPosition = camPos;
                m_SortedAlongForward = camFwd;
                m_HasSortedOnce = true;
            }
            else if (NeedsResort)
            {
                // The GPU sort consumes the same camera basis, so record that this frame's
                // order has been dealt with either way.
                m_SortedFromPosition = camPos;
                m_SortedAlongForward = camFwd;
                m_HasSortedOnce = true;
            }

            return true;
        }

        void EnsureCapacity(int capacity)
        {
            if (m_Combined.IsCreated && m_Combined.Length >= capacity)
                return;

            int size = Mathf.NextPowerOfTwo(Mathf.Max(capacity, 256));

            if (m_Combined.IsCreated)
                m_Combined.Dispose();
            m_Combined = new NativeArray<CTSpriteInstance>(size, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            m_Keys = new float[size];
            m_Indices = new int[size];

            m_InstanceBuffer?.Dispose();
            m_InstanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, CTSpriteInstance.Stride)
            {
                name = "CT Instances"
            };

            m_IndexBuffer?.Dispose();
            m_IndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, sizeof(int))
            {
                name = "CT Sorted Instance Indices"
            };

            // The buffers were just replaced, so whatever was uploaded before is gone.
            m_ContentSignature = -1;
            m_HasSortedOnce = false;
        }

        public void Dispose()
        {
            if (m_Combined.IsCreated)
                m_Combined.Dispose();
            m_InstanceBuffer?.Dispose();
            m_InstanceBuffer = null;
            m_IndexBuffer?.Dispose();
            m_IndexBuffer = null;
        }
    }
}
