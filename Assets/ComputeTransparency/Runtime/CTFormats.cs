using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>Shared layout constants and packing helpers for the GPU side.</summary>
    public static class CTFormats
    {
        /// <summary>
        /// Size of the screen space triangle the setup kernel emits: five float4s
        /// (p01, p2uv0, uv12, zLod, payload). Must match CTTri in CTCommon.hlsl.
        /// </summary>
        public const int TriangleStride = 80;

        /// <summary>
        /// Packs a colour to RGBA8. The shader unpacks the same bits without any colour space
        /// conversion, so the value reaches the blend exactly as written here.
        /// </summary>
        public static uint PackColor(Color c)
        {
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            uint a = (uint)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
            return r | (g << 8) | (b << 16) | (a << 24);
        }
    }
}
