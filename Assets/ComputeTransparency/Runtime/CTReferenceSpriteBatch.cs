using System.Collections.Generic;
using UnityEngine;

namespace ComputeTransparency
{
    /// <summary>
    /// Draws <see cref="CTStressItem"/>s the traditional way: billboarded quads in one mesh,
    /// triangles ordered back to front, blended by the hardware through URP's transparent
    /// queue.
    /// </summary>
    /// <remarks>
    /// One mesh rather than one renderer per sprite on purpose. Per-object draw call overhead
    /// would dominate the measurement and the question here is what overdraw costs, so the
    /// baseline is given every advantage except the early-out itself: a single draw call,
    /// no per-object culling or sorting work, and blending done by the ROP.
    /// </remarks>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class CTReferenceSpriteBatch : MonoBehaviour
    {
        CTStressItem[] m_Items;
        int m_Count;

        Mesh m_Mesh;
        Material m_Material;

        readonly List<Vector3> m_Vertices = new List<Vector3>();
        readonly List<Vector3> m_Uvs = new List<Vector3>();
        readonly List<Color32> m_Colors = new List<Color32>();
        readonly List<int> m_Indices = new List<int>();

        float[] m_Keys = System.Array.Empty<float>();
        int[] m_Order = System.Array.Empty<int>();

        public void SetItems(CTStressItem[] items, int count, CTAtlas atlas, Shader shader)
        {
            m_Items = items;
            m_Count = count;

            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "CT Reference Sprites" };
                m_Mesh.MarkDynamic();
                m_Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                GetComponent<MeshFilter>().sharedMesh = m_Mesh;
            }

            if (m_Material == null && shader != null)
            {
                m_Material = new Material(shader) { name = "CT Reference Sprites", hideFlags = HideFlags.DontSave };
                GetComponent<MeshRenderer>().sharedMaterial = m_Material;
            }

            if (m_Material != null && atlas != null)
                m_Material.SetTexture("_BaseMap", atlas.Texture);

            var renderer = GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            if (m_Keys.Length < count)
            {
                m_Keys = new float[count];
                m_Order = new int[count];
            }

            RebuildNow();
        }

        void LateUpdate() => RebuildNow();

        void OnEnable() => RebuildNow();

        /// <summary>
        /// Rebuilds the mesh immediately. Needed because nothing ticks between enabling this
        /// object and an editor driven <c>Camera.Render</c>, so waiting for LateUpdate would
        /// draw an empty mesh.
        /// </summary>
        public void RebuildNow()
        {
            if (m_Mesh == null || m_Items == null || m_Count == 0)
                return;

            var camera = Camera.main;
            if (camera == null)
                return;

            var cameraTransform = camera.transform;
            Vector3 camPos = cameraTransform.position;
            Vector3 camForward = cameraTransform.forward;
            Vector3 camRight = cameraTransform.right;
            Vector3 camUp = cameraTransform.up;
            Vector3 origin = transform.position;

            // Back to front: the transparent queue needs the far sprites drawn first, which is
            // the exact reverse of the order the compute path walks its tiles in.
            for (int i = 0; i < m_Count; i++)
            {
                m_Keys[i] = -Vector3.Dot(origin + m_Items[i].position - camPos, camForward);
                m_Order[i] = i;
            }
            System.Array.Sort(m_Keys, m_Order, 0, m_Count);

            m_Vertices.Clear();
            m_Uvs.Clear();
            m_Colors.Clear();
            m_Indices.Clear();

            for (int i = 0; i < m_Count; i++)
            {
                var item = m_Items[m_Order[i]];
                Vector3 right = camRight * item.halfSize.x;
                Vector3 up = camUp * item.halfSize.y;
                Vector3 centre = item.position;

                int baseIndex = m_Vertices.Count;
                m_Vertices.Add(centre - right - up);
                m_Vertices.Add(centre + right - up);
                m_Vertices.Add(centre + right + up);
                m_Vertices.Add(centre - right + up);

                float slice = item.slice;
                m_Uvs.Add(new Vector3(0f, 0f, slice));
                m_Uvs.Add(new Vector3(1f, 0f, slice));
                m_Uvs.Add(new Vector3(1f, 1f, slice));
                m_Uvs.Add(new Vector3(0f, 1f, slice));

                // Color32 quantises the tint to 8 bits per channel, the same as the compute
                // path's packed RGBA8, so neither side gets a precision advantage.
                Color32 tint = item.color;
                m_Colors.Add(tint);
                m_Colors.Add(tint);
                m_Colors.Add(tint);
                m_Colors.Add(tint);

                m_Indices.Add(baseIndex + 0);
                m_Indices.Add(baseIndex + 1);
                m_Indices.Add(baseIndex + 2);
                m_Indices.Add(baseIndex + 0);
                m_Indices.Add(baseIndex + 2);
                m_Indices.Add(baseIndex + 3);
            }

            m_Mesh.Clear();
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.SetUVs(0, m_Uvs);
            m_Mesh.SetColors(m_Colors);
            m_Mesh.SetTriangles(m_Indices, 0, false);
            // The quads are rebuilt around the camera every frame, so a fixed generous bound
            // is cheaper and safer than recomputing one.
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }
    }
}
