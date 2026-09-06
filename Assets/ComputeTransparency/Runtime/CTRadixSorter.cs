using System;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// GPU side storage for the radix sort: ping-pong key and value buffers, the per block
    /// digit histogram, and the scratch the histogram scan needs.
    /// </summary>
    /// <remarks>
    /// Four passes of eight bits, each reading one side of the ping-pong and writing the other.
    /// Four is even, so the sorted indices always end up back in <see cref="ValuesA"/>.
    /// </remarks>
    sealed class CTRadixSorter : IDisposable
    {
        public const int Radix = 256;
        public const int PassCount = 4;       // 32 bit keys, eight bits per pass
        public const int BitsPerPass = 8;
        public const int GroupSize = 256;     // threads per block, and elements per block
        public const int ScanGroupSize = 256;

        GraphicsBuffer m_KeysA, m_KeysB, m_ValuesA, m_ValuesB, m_Histogram, m_BlockSums;
        int m_Capacity;

        public GraphicsBuffer KeysA => m_KeysA;
        public GraphicsBuffer KeysB => m_KeysB;
        public GraphicsBuffer ValuesA => m_ValuesA;
        public GraphicsBuffer ValuesB => m_ValuesB;
        public GraphicsBuffer Histogram => m_Histogram;
        public GraphicsBuffer BlockSums => m_BlockSums;

        /// <summary>The buffer holding the sorted instance indices once all passes have run.</summary>
        public GraphicsBuffer SortedIndices => m_ValuesA;

        public int PaddedCount { get; private set; }
        public int BlockCount { get; private set; }
        public int HistogramSize { get; private set; }
        public int ScanGroupCount { get; private set; }

        /// <summary>Sizes the buffers for <paramref name="count"/> instances.</summary>
        public void Prepare(int count)
        {
            BlockCount = Mathf.Max(1, (count + GroupSize - 1) / GroupSize);
            PaddedCount = BlockCount * GroupSize;
            HistogramSize = Radix * BlockCount;
            ScanGroupCount = (HistogramSize + ScanGroupSize - 1) / ScanGroupSize;

            if (m_Capacity >= PaddedCount && m_KeysA != null)
                return;

            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(PaddedCount, GroupSize));
            int histogramCapacity = Radix * (capacity / GroupSize);
            int blockSumCapacity = (histogramCapacity + ScanGroupSize - 1) / ScanGroupSize;

            Dispose();
            m_KeysA = New(capacity, "CT Sort Keys A");
            m_KeysB = New(capacity, "CT Sort Keys B");
            m_ValuesA = New(capacity, "CT Sort Values A");
            m_ValuesB = New(capacity, "CT Sort Values B");
            m_Histogram = New(histogramCapacity, "CT Sort Histogram");
            m_BlockSums = New(blockSumCapacity, "CT Sort Block Sums");
            m_Capacity = capacity;
        }

        static GraphicsBuffer New(int count, string name)
        {
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint)) { name = name };
        }

        public void Dispose()
        {
            m_KeysA?.Dispose();
            m_KeysB?.Dispose();
            m_ValuesA?.Dispose();
            m_ValuesB?.Dispose();
            m_Histogram?.Dispose();
            m_BlockSums?.Dispose();
            m_KeysA = m_KeysB = m_ValuesA = m_ValuesB = m_Histogram = m_BlockSums = null;
            m_Capacity = 0;
        }
    }
}
