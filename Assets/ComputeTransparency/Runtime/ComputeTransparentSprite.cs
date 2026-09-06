using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// A quad drawn by the compute rasterizer instead of by the normal transparent queue.
    /// Registers itself with <see cref="CTSpriteRegistry"/> while enabled.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Rendering/Compute Transparent Sprite")]
    public class ComputeTransparentSprite : MonoBehaviour
    {
        [Tooltip("Slice of the atlas assigned to the Compute Transparency renderer feature.")]
        public int slice;

        [Tooltip("Sub rectangle of the slice to sample. xy = offset, zw = size.")]
        public Vector4 uvRect = new Vector4(0, 0, 1, 1);

        [Tooltip("Tint multiplied into the sampled texel. Alpha scales the sprite's opacity.")]
        public Color color = Color.white;

        [Tooltip("Quad size in world units before the transform's scale is applied.")]
        public Vector2 size = Vector2.one;

        [Tooltip("Face the camera instead of using the transform's own orientation.")]
        public bool billboard = true;

        void OnEnable() => CTSpriteRegistry.Register(this);
        void OnDisable() => CTSpriteRegistry.Unregister(this);

        /// <summary>
        /// Builds this sprite's GPU record. Billboarded sprites leave the orientation to the
        /// setup kernel, so nothing here depends on the camera.
        /// </summary>
        public CTSpriteInstance ToInstance()
        {
            // One native read of the matrix instead of separate position, right, up and
            // lossyScale property reads; that difference was most of the cost of building the
            // frame's sprite list.
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            Vector3 centre = new Vector3(localToWorld.m03, localToWorld.m13, localToWorld.m23);
            Vector3 axisX = new Vector3(localToWorld.m00, localToWorld.m10, localToWorld.m20);
            Vector3 axisY = new Vector3(localToWorld.m01, localToWorld.m11, localToWorld.m21);

            if (billboard)
            {
                // Keep the world scale, take the orientation from the camera on the GPU.
                var scaled = new Vector2(size.x * axisX.magnitude, size.y * axisY.magnitude);
                return CTSpriteInstance.Billboard(centre, scaled, color, uvRect, slice);
            }

            return CTSpriteInstance.Oriented(centre, axisX * (size.x * 0.5f), axisY * (size.y * 0.5f),
                                             color, uvRect, slice);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(color.r, color.g, color.b, 0.5f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, size.y, 0f));
        }
    }
}
