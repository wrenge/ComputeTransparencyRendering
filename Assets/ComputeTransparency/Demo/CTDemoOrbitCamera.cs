using UnityEngine;

namespace ComputeTransparency.Demo
{
    /// <summary>
    /// Drives the demo camera on a slow orbit that also breathes in and out, so the sprite cloud
    /// is seen from every side and at a range of on screen sizes.
    /// </summary>
    /// <remarks>
    /// The dolly is the point, not the decoration: the compute path's cost scales with covered
    /// pixels rather than with sprite count, so watching the frame time while the cloud fills and
    /// empties the screen shows the difference between the two paths better than any static shot.
    ///
    /// Runs early in LateUpdate because the hardware path sorts its sprites for wherever the
    /// camera ends up this frame.
    /// </remarks>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public class CTDemoOrbitCamera : MonoBehaviour
    {
        public Vector3 target = Vector3.zero;

        [Tooltip("Degrees per second around the cloud.")]
        public float yawSpeed = 14f;

        public float basePitch = 10f;
        public float pitchAmplitude = 16f;
        public float pitchPeriod = 27f;

        [Tooltip("Closest approach. Small enough that the cloud fills the frame and overdraw peaks.")]
        public float nearDistance = 7f;

        public float farDistance = 34f;
        public float dollyPeriod = 21f;

        public bool paused;

        float m_Time;

        void OnEnable() => Apply();

        void LateUpdate()
        {
            if (!paused)
                m_Time += Application.isPlaying ? Time.deltaTime : 0f;
            Apply();
        }

        public void ResetView()
        {
            m_Time = 0f;
            Apply();
        }

        void Apply()
        {
            float yaw = m_Time * yawSpeed;
            float pitch = basePitch + pitchAmplitude * Mathf.Sin(m_Time * (2f * Mathf.PI / Mathf.Max(pitchPeriod, 0.01f)));

            // Cosine rather than a sawtooth so the camera eases at both ends instead of snapping
            // from the closest point back to the farthest.
            float t = 0.5f - 0.5f * Mathf.Cos(m_Time * (2f * Mathf.PI / Mathf.Max(dollyPeriod, 0.01f)));
            float distance = Mathf.Lerp(nearDistance, farDistance, t);

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }
    }
}
