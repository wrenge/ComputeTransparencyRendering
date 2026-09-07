using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComputeTransparency.Demo
{
    /// <summary>
    /// The demo's on screen controls: instance count, sprite alpha, which of the two renderers
    /// is running, and which debug view the compute rasterizer writes instead of the image.
    /// </summary>
    /// <remarks>
    /// The layout lives in CTDemoUI.uxml and its look in CTDemoUI.uss, both assigned to the
    /// UIDocument; this class only looks the controls up by name and wires them to the cloud.
    ///
    /// Overdraw is the one view both renderers can produce, and it is routed to whichever one is
    /// running. The other three describe the compute rasterizer's tiles and its early-out and do
    /// nothing while the hardware path is showing.
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class CTDemoUI : MonoBehaviour
    {
        [Tooltip("The cloud the controls drive.")]
        public CTDemoCloud cloud;

        [Tooltip("Reset also puts the camera back to the start of its orbit.")]
        public CTDemoOrbitCamera orbit;

        [Tooltip("The renderer feature asset, so the debug dropdown can reach its settings. " +
                 "This is the sub asset of the URP renderer.")]
        public ComputeTransparencyRenderFeature feature;

        static readonly CTDebugMode[] k_DebugModes =
        {
            CTDebugMode.None,
            CTDebugMode.Overdraw,
            CTDebugMode.Transmittance,
            CTDebugMode.TileLoad,
            CTDebugMode.WalkLength
        };

        static readonly List<string> k_DebugLabels = new List<string>
        {
            "None", "Overdraw", "Transmittance", "Tile load", "Walk length"
        };

        static readonly List<string> k_PathLabels = new List<string> { "Compute", "Traditional" };

        Label m_CountValue;
        Button m_Less;
        Button m_More;
        Slider m_Alpha;
        DropdownField m_Renderer;
        DropdownField m_Debug;

        bool m_Syncing;

        void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            var root = document != null ? document.rootVisualElement : null;
            if (root == null || cloud == null)
                return;

            m_CountValue = root.Q<Label>("instances-value");
            m_Less = root.Q<Button>("instances-less");
            m_More = root.Q<Button>("instances-more");
            m_Alpha = root.Q<Slider>("alpha");
            m_Renderer = root.Q<DropdownField>("renderer");
            m_Debug = root.Q<DropdownField>("debug");

            if (m_CountValue == null || m_Less == null || m_More == null ||
                m_Alpha == null || m_Renderer == null || m_Debug == null)
            {
                Debug.LogWarning("[CTDemoUI] The UIDocument is not showing CTDemoUI.uxml, or its " +
                                 "controls have been renamed; the panel will not respond.", this);
                return;
            }

            m_Less.clicked += () => Step(-1);
            m_More.clicked += () => Step(1);

            m_Alpha.RegisterValueChangedCallback(evt =>
            {
                if (m_Syncing)
                    return;
                cloud.Alpha = evt.newValue;
            });

            // The choices are the enums' business, not the layout's, so they are filled here
            // rather than spelled out twice.
            m_Renderer.choices = k_PathLabels;
            m_Renderer.RegisterValueChangedCallback(evt =>
            {
                if (m_Syncing)
                    return;
                cloud.RenderPath = m_Renderer.index == 1 ? CTDemoPath.Traditional : CTDemoPath.Compute;
                ApplyDebugMode();
            });

            m_Debug.choices = k_DebugLabels;
            m_Debug.RegisterValueChangedCallback(evt =>
            {
                if (m_Syncing || feature == null)
                    return;
                int index = Mathf.Clamp(m_Debug.index, 0, k_DebugModes.Length - 1);
                feature.settings.debugMode = k_DebugModes[index];
                ApplyDebugMode();
            });

            var reset = root.Q<Button>("reset");
            if (reset != null)
                reset.clicked += () =>
                {
                    cloud.ResetDefaults();
                    if (orbit != null)
                        orbit.ResetView();
                    if (feature != null)
                        feature.settings.debugMode = CTDebugMode.None;
                    ApplyDebugMode();
                    Sync();
                };

            cloud.Changed += Sync;
            ApplyDebugMode();
            Sync();
        }

        void OnDisable()
        {
            if (cloud != null)
                cloud.Changed -= Sync;
        }

        /// <summary>
        /// Routes the chosen view to whichever renderer can produce it. Overdraw is the one view
        /// both paths have; the other three describe the compute rasterizer's tiles and its
        /// early-out, so they mean nothing while the hardware path is running.
        /// </summary>
        void ApplyDebugMode()
        {
            var mode = feature != null ? feature.settings.debugMode : CTDebugMode.None;
            cloud.Overdraw = cloud.RenderPath == CTDemoPath.Traditional && mode == CTDebugMode.Overdraw;
        }

        void Step(int direction)
        {
            cloud.StepInstances(direction);
            Sync();
        }

        /// <summary>
        /// Pushes the current state back into the controls. Guarded because writing a control's
        /// value fires its change callback, which would otherwise write straight back.
        /// </summary>
        void Sync()
        {
            if (m_CountValue == null || cloud == null)
                return;

            m_Syncing = true;

            m_CountValue.text = cloud.Count.ToString();
            m_Less.SetEnabled(cloud.Step > 0);
            m_More.SetEnabled(cloud.Step < cloud.StepCount - 1);
            m_Alpha.SetValueWithoutNotify(cloud.Alpha);
            m_Renderer.index = cloud.RenderPath == CTDemoPath.Traditional ? 1 : 0;

            m_Debug.SetEnabled(feature != null);
            if (feature != null)
            {
                int index = System.Array.IndexOf(k_DebugModes, feature.settings.debugMode);
                m_Debug.index = Mathf.Max(index, 0);
            }

            m_Syncing = false;
        }
    }
}
