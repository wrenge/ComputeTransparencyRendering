using System.Runtime.InteropServices;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// One sprite as the GPU consumes it. Write these into a <see cref="CTSpriteBatch"/> to draw
    /// sprites without a GameObject each.
    /// </summary>
    /// <remarks>
    /// Must stay byte identical to CTSpriteInstance in CTCommon.hlsl. Only float4 and uint4
    /// members: float2 and float3 carry backend specific alignment (Metal aligns float3 to 16),
    /// and a layout that disagrees with the shader reads garbage without any error.
    ///
    /// Billboarded instances leave the orientation to the setup kernel, which builds the axes
    /// from the camera. That is the whole point of the instanced path: the per sprite data does
    /// not depend on the camera, so a batch that does not move needs no CPU work at all between
    /// frames.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CTSpriteInstance
    {
        /// <summary>xyz = world space centre. w = roll in radians, billboards only.</summary>
        public Vector4 center;

        /// <summary>xyz = world half extent along local X (oriented only). w = half width (billboards only).</summary>
        public Vector4 axisX;

        /// <summary>xyz = world half extent along local Y (oriented only). w = half height (billboards only).</summary>
        public Vector4 axisY;

        /// <summary>xy = uv offset, zw = uv size.</summary>
        public Vector4 uvRect;

        /// <summary>Tint packed as RGBA8, multiplied into the sampled texel.</summary>
        public uint color;

        /// <summary>Texture2DArray slice.</summary>
        public uint slice;

        /// <summary>See <see cref="FlagBillboard"/>.</summary>
        public uint flags;

        public uint unused;

        public const int Stride = 80;

        /// <summary>Orient the quad to face the camera instead of using axisX/axisY.</summary>
        public const uint FlagBillboard = 1u;

        public static readonly Vector4 FullRect = new Vector4(0f, 0f, 1f, 1f);

        /// <summary>A camera facing quad. The axes are built on the GPU.</summary>
        public static CTSpriteInstance Billboard(Vector3 centre, Vector2 size, Color tint,
                                                 int slice = 0, float roll = 0f)
        {
            return Billboard(centre, size, tint, FullRect, slice, roll);
        }

        public static CTSpriteInstance Billboard(Vector3 centre, Vector2 size, Color tint,
                                                 Vector4 uvRect, int slice = 0, float roll = 0f)
        {
            return new CTSpriteInstance
            {
                center = new Vector4(centre.x, centre.y, centre.z, roll),
                axisX = new Vector4(0f, 0f, 0f, size.x * 0.5f),
                axisY = new Vector4(0f, 0f, 0f, size.y * 0.5f),
                uvRect = uvRect,
                color = CTFormats.PackColor(tint),
                slice = (uint)Mathf.Max(0, slice),
                flags = FlagBillboard,
                unused = 0u
            };
        }

        /// <summary>A quad with a fixed world orientation, given by its two half extent vectors.</summary>
        public static CTSpriteInstance Oriented(Vector3 centre, Vector3 halfAxisX, Vector3 halfAxisY,
                                                Color tint, int slice = 0)
        {
            return Oriented(centre, halfAxisX, halfAxisY, tint, FullRect, slice);
        }

        public static CTSpriteInstance Oriented(Vector3 centre, Vector3 halfAxisX, Vector3 halfAxisY,
                                                Color tint, Vector4 uvRect, int slice = 0)
        {
            return new CTSpriteInstance
            {
                center = new Vector4(centre.x, centre.y, centre.z, 0f),
                axisX = new Vector4(halfAxisX.x, halfAxisX.y, halfAxisX.z, 0f),
                axisY = new Vector4(halfAxisY.x, halfAxisY.y, halfAxisY.z, 0f),
                uvRect = uvRect,
                color = CTFormats.PackColor(tint),
                slice = (uint)Mathf.Max(0, slice),
                flags = 0u,
                unused = 0u
            };
        }
    }
}
