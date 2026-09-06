using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// One sprite in the stress scene, generated once and handed to both rendering paths so
    /// the comparison is over identical geometry, textures and tints.
    /// </summary>
    public struct CTStressItem
    {
        public Vector3 position;
        public Vector2 halfSize;
        public int slice;
        public Color color;
    }
}
