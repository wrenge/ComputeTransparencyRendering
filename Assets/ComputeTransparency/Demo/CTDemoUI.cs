using System.Collections.Generic;
using System.Globalization;
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
    ///
    /// The panel has two pages, picked by the tabs under the title. The GPU timer list is the second
    /// one: everything measured in this project until now has been whole frame wall clock, which
    /// cannot say which of the seventeen dispatches the frame went into, and the toggle there turns
    /// on per pass timestamp queries whose readings <see cref="CTGpuTimers"/> hands back. It is a
    /// page of its own because seventeen rows do not fit under the controls on a phone.
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

        // The averages move slowly by design, so redrawing them at the frame rate would only
        // churn text. Five times a second is well past what anyone can read.
        const float k_TimerRefresh = 0.2f;

        Label m_CountValue;
        Button m_Less;
        Button m_More;
        Slider m_Alpha;
        DropdownField m_Renderer;
        DropdownField m_Debug;

        Button m_TabControls;
        Button m_TabTimers;
        VisualElement m_PageControls;
        VisualElement m_PageTimers;

        Toggle m_Textures;
        Toggle m_Timers;
        VisualElement m_TimersList;
        Label m_TimersNote;

        readonly List<VisualElement> m_TimerRows = new List<VisualElement>();
        readonly List<Label> m_TimerValues = new List<Label>();
        VisualElement m_TimerTotalRow;
        Label m_TimerTotal;

        bool m_TimersPage;
        int m_TimerVersion = -1;
        float m_NextTimerRefresh;

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

            // The tabs and the timer controls are looked up separately and tolerated missing: they
            // came later than the rest of the panel, and a UXML without them should still work.
            m_TabControls = root.Q<Button>("tab-controls");
            m_TabTimers = root.Q<Button>("tab-timers");
            m_PageControls = root.Q<VisualElement>("page-controls");
            m_PageTimers = root.Q<VisualElement>("page-timers");

            if (m_TabControls != null)
                m_TabControls.clicked += () => SelectPage(false);
            if (m_TabTimers != null)
                m_TabTimers.clicked += () => SelectPage(true);
            SelectPage(false);

            // Both renderers sample the same atlas, and the setting switches both at once, so
            // this stays enabled whichever path is showing.
            m_Textures = root.Q<Toggle>("textures");
            if (m_Textures != null)
            {
                m_Textures.SetEnabled(feature != null);
                m_Textures.RegisterValueChangedCallback(evt =>
                {
                    if (m_Syncing || feature == null)
                        return;
                    feature.settings.textures = evt.newValue;
                });
            }

            m_Timers = root.Q<Toggle>("timers");
            m_TimersList = root.Q<VisualElement>("timers-list");
            m_TimersNote = root.Q<Label>("timers-note");

            if (m_Timers != null)
            {
                m_Timers.SetEnabled(feature != null);
                m_Timers.RegisterValueChangedCallback(evt =>
                {
                    if (m_Syncing || feature == null)
                        return;

                    feature.settings.gpuTimers = evt.newValue;
                    m_NextTimerRefresh = 0f;
                });
            }

            var reset = root.Q<Button>("reset");
            if (reset != null)
                reset.clicked += () =>
                {
                    cloud.ResetDefaults();
                    if (orbit != null)
                        orbit.ResetView();
                    if (feature != null)
                    {
                        feature.settings.debugMode = CTDebugMode.None;
                        feature.settings.textures = true;
                    }
                    ApplyDebugMode();
                    Sync();
                };

            var device = root.Q<Label>("device");
            if (device != null)
                device.text = CTDemoBootstrap.DeviceReport(cloud);

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

        /// <summary>
        /// Pumps the GPU timer recorders and redraws the list. The pass pumps them too, so this is
        /// only here to keep the panel live on a frame the renderer decided not to record - the
        /// tick itself is idempotent within a frame.
        /// </summary>
        void Update()
        {
            CTGpuTimers.Tick();
            RefreshTimers();
        }

        /// <summary>
        /// Shows one of the two pages. The timer list is seventeen rows and cannot share a panel
        /// with the controls on a phone, so it gets a page rather than a scrap of scroll area.
        /// </summary>
        void SelectPage(bool timers)
        {
            if (m_PageControls == null || m_PageTimers == null)
                return;

            m_TimersPage = timers;
            m_PageControls.style.display = timers ? DisplayStyle.None : DisplayStyle.Flex;
            m_PageTimers.style.display = timers ? DisplayStyle.Flex : DisplayStyle.None;

            if (m_TabControls != null)
                m_TabControls.EnableInClassList("ct-tab-active", !timers);
            if (m_TabTimers != null)
                m_TabTimers.EnableInClassList("ct-tab-active", timers);

            // Redraw on the way in rather than up to a fifth of a second later.
            m_NextTimerRefresh = 0f;
        }

        /// <summary>
        /// Redraws the per pass times. Rows are rebuilt only when the registry grows, which happens
        /// over the first frames after the timers are switched on and again when the sort mode
        /// changes and the radix passes appear.
        /// </summary>
        void RefreshTimers()
        {
            if (m_TimersList == null || m_TimersNote == null)
                return;

            // Nothing to redraw while the page is not showing. The averages keep updating either
            // way - that is CTGpuTimers.Tick, which runs whatever page is up.
            if (m_PageTimers != null && !m_TimersPage)
                return;

            bool on = feature != null && feature.settings.gpuTimers;
            var display = on ? DisplayStyle.Flex : DisplayStyle.None;
            m_TimersList.style.display = display;
            m_TimersNote.style.display = display;
            if (!on)
                return;

            if (Time.unscaledTime < m_NextTimerRefresh)
                return;
            m_NextTimerRefresh = Time.unscaledTime + k_TimerRefresh;

            if (m_TimerVersion != CTGpuTimers.Version)
            {
                m_TimerVersion = CTGpuTimers.Version;
                RebuildTimerRows();
            }

            var passes = CTGpuTimers.Passes;
            int rows = Mathf.Min(m_TimerValues.Count, passes.Count);
            for (int i = 0; i < rows; i++)
            {
                var pass = passes[i];
                // A pass that did not run reports nothing rather than a stale average: in Cpu sort
                // mode the radix rows stay registered but idle, and showing their last numbers
                // would read as if the GPU sort were still running.
                m_TimerValues[i].text = pass.active ? Milliseconds(pass.averageMs) : "\u2014";
                m_TimerRows[i].EnableInClassList("ct-timer-idle", !pass.active);
            }

            if (m_TimerTotal != null)
                m_TimerTotal.text = Milliseconds(CTGpuTimers.TotalMs);

            m_TimersNote.text = TimerNote(passes.Count);
        }

        /// <summary>
        /// What the line under the list says. Three states, and telling them apart is most of the
        /// value: the compute path is not running at all, the backend does not report GPU time, or
        /// the numbers above are real.
        /// </summary>
        static string TimerNote(int passCount)
        {
            if (passCount == 0)
                return "No compute passes recorded. The compute renderer is not running - there " +
                       "is nothing for it to draw, or the hardware path is showing.";

            // The passes are sampled either way - the CPU side of every sampler counts its blocks -
            // so silence means this backend does not fill in the GPU side, not that nothing ran.
            if (CTGpuTimers.Silent)
                return SystemInfo.graphicsDeviceType + " reports no GPU timings. Unity's GPU " +
                       "recorders are empty on some backends, Metal in the editor among them. Use " +
                       "an external capture there: Xcode for Metal, Snapdragon Profiler or AGI " +
                       "for Vulkan.";

            return "ms of GPU time per dispatch, moving average, a few frames behind. The queries " +
                   "cost a little themselves, so read this to find the expensive pass, not to " +
                   "quote the renderer's cost.";
        }

        /// <summary>
        /// Builds the list: the total first and outside the scroll area, then the per pass rows
        /// inside it. Seventeen rows is taller than a phone can spare next to everything else on
        /// this panel, and the total is the row worth keeping on screen.
        /// </summary>
        void RebuildTimerRows()
        {
            m_TimersList.Clear();
            m_TimerRows.Clear();
            m_TimerValues.Clear();
            m_TimerTotalRow = null;
            m_TimerTotal = null;

            var passes = CTGpuTimers.Passes;
            if (passes.Count == 0)
                return;

            m_TimerTotalRow = TimerRow("Total", out m_TimerTotal);
            m_TimerTotalRow.AddToClassList("ct-timer-total");
            m_TimersList.Add(m_TimerTotalRow);
            m_TimersList.Add(Separator());

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("ct-timer-scroll");
            m_TimersList.Add(scroll);

            string group = null;
            for (int i = 0; i < passes.Count; i++)
            {
                var pass = passes[i];
                if (group != null && pass.group != group)
                    scroll.Add(Separator());
                group = pass.group;

                var row = TimerRow(ShortName(pass.name), out var value);
                scroll.Add(row);
                m_TimerRows.Add(row);
                m_TimerValues.Add(value);
            }
        }

        static VisualElement TimerRow(string label, out Label value)
        {
            var row = new VisualElement();
            row.AddToClassList("ct-timer-row");

            var name = new Label(label);
            name.AddToClassList("ct-timer-name");
            row.Add(name);

            value = new Label("\u2014");
            value.AddToClassList("ct-timer-value");
            row.Add(value);

            return row;
        }

        static VisualElement Separator()
        {
            var separator = new VisualElement();
            separator.AddToClassList("ct-timer-separator");
            return separator;
        }

        /// <summary>The sampler names carry the CT prefix so they are findable in a profiler
        /// capture; the panel has a title and does not need it repeated on every row.</summary>
        static string ShortName(string passName)
        {
            return passName.StartsWith("CT ") ? passName.Substring(3) : passName;
        }

        /// <summary>Invariant culture on purpose: a device set to a comma decimal separator would
        /// otherwise print 0,412 next to numbers quoted as 0.412 everywhere else.</summary>
        static string Milliseconds(double ms)
        {
            return ms.ToString("0.000", CultureInfo.InvariantCulture);
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

            if (m_Textures != null)
                m_Textures.SetValueWithoutNotify(feature == null || feature.settings.textures);

            if (m_Timers != null)
                m_Timers.SetValueWithoutNotify(feature != null && feature.settings.gpuTimers);

            m_Syncing = false;
        }
    }
}
