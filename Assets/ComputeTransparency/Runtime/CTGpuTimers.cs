using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace ComputeTransparency
{
    /// <summary>
    /// Per pass GPU timers for the compute rasterizer, and the registry the demo panel reads them
    /// from.
    /// </summary>
    /// <remarks>
    /// Every measurement in this project so far has been whole frame wall clock, which bundles the
    /// CPU gather, the GPU work and the editor's own overhead into one number: it says the frame
    /// cost 8.6 ms and nothing about where the 8.6 ms went. These timers ask the hardware instead.
    /// Each pass gets a <see cref="CustomSampler"/> created with GPU collection on, the dispatch is
    /// wrapped in it, and the driver stamps the time either side.
    ///
    /// Three things about the numbers that come back:
    ///
    /// - They are late. The CPU runs ahead of the GPU, so a recorder read during frame N describes
    ///   a frame that finished some time earlier. That is fine for a running average and useless
    ///   for correlating with a single frame's state.
    /// - They are noisy per frame, so each pass keeps an exponential moving average.
    /// - A pass that did not run reports zero nanoseconds, and so does a platform that cannot time
    ///   the GPU at all. Only <see cref="Recorder.gpuSampleBlockCount"/> separates the two, which is
    ///   what <see cref="Pass.active"/> is built on: the sort passes vanish from the list in Cpu
    ///   mode rather than sitting there reading 0.000.
    ///
    /// Timestamp queries are not free, and on a tile based GPU they can split work that would
    /// otherwise batch, so the whole thing is off unless something turns it on - the demo panel has
    /// a toggle, which writes the renderer feature's setting.
    ///
    /// Not every backend fills these in. Measured here: on Metal in the editor every sampler is
    /// entered - the CPU side reports its blocks - and the GPU side stays at zero, and so does
    /// Unity's own Camera.Render marker, so this is the backend and not the wiring. Where that
    /// happens an external capture (Xcode for Metal, Snapdragon Profiler or Android GPU Inspector
    /// for Vulkan) is the alternative, and <see cref="Silent"/> says so on screen rather than
    /// leaving a list of dashes.
    ///
    /// <see cref="Tick"/> is driven off <see cref="Time.frameCount"/>, which does not advance in
    /// edit mode. This is a play mode and player feature; a scene view will register the passes and
    /// then show them frozen.
    /// </remarks>
    public static class CTGpuTimers
    {
        /// <summary>One timed pass: the sampler the command buffer writes into, and the smoothed
        /// reading the UI displays.</summary>
        public sealed class Pass
        {
            // Slow enough that a single hitch does not move the number, fast enough that changing
            // the sprite count is visible within a second or so.
            const double k_Smoothing = 0.1;

            /// <summary>Which stage of the pipeline this belongs to, for grouping in a list.</summary>
            public readonly string group;

            /// <summary>The sampler's name, which is also the render graph pass's name.</summary>
            public readonly string name;

            /// <summary>Null if the platform refused to create the sampler; callers must skip
            /// wrapping in that case rather than crash.</summary>
            public readonly CustomSampler sampler;

            readonly Recorder m_Recorder;

            double m_AverageMs;
            bool m_HasData;
            bool m_Active;

            /// <summary>Smoothed GPU time in milliseconds. Only meaningful while <see cref="hasData"/>.</summary>
            public double averageMs => m_AverageMs;

            /// <summary>True once this pass has produced at least one GPU sample.</summary>
            public bool hasData => m_HasData;

            /// <summary>True if the pass actually ran on the frame the last reading came from.</summary>
            public bool active => m_Active;

            internal Pass(string group, string name)
            {
                this.group = group;
                this.name = name;

                var created = CustomSampler.Create(name, true);
                if (created == null || !created.isValid)
                    return;

                sampler = created;
                m_Recorder = created.GetRecorder();
                if (m_Recorder != null)
                    m_Recorder.enabled = true;
            }

            internal void Sample()
            {
                if (m_Recorder == null)
                {
                    m_Active = false;
                    return;
                }

                // The block count is the honest signal. A pass that did not run this frame and a
                // platform with no GPU timing both report zero nanoseconds; only this tells them
                // apart, and without it an idle pass would drag its own average towards zero.
                m_Active = m_Recorder.gpuSampleBlockCount > 0;
                if (!m_Active)
                    return;

                double ms = m_Recorder.gpuElapsedNanoseconds * 1e-6;
                m_AverageMs = m_HasData ? m_AverageMs + (ms - m_AverageMs) * k_Smoothing : ms;
                m_HasData = true;
            }

            internal void Reset()
            {
                m_AverageMs = 0.0;
                m_HasData = false;
                m_Active = false;
            }
        }

        static readonly List<Pass> s_Passes = new List<Pass>();
        static readonly Dictionary<string, Pass> s_ByName = new Dictionary<string, Pass>();

        // How long the timers may report nothing before that is treated as a platform answer
        // rather than as the readings still being in flight.
        const float k_SilenceGrace = 3f;

        static bool s_Enabled;
        static int s_Version;
        static int s_SampledFrame = -1;
        static float s_EnabledAt;
        static bool s_WarnedSilent;

        /// <summary>The timed passes in registration order, which is the order they dispatch in.</summary>
        public static IReadOnlyList<Pass> Passes => s_Passes;

        /// <summary>Bumped whenever a pass is registered, so a UI knows to rebuild its rows. Passes
        /// register the first time they are recorded, so the list fills in over the first frame or
        /// two after the timers are switched on, and grows again when the sort mode changes.</summary>
        public static int Version => s_Version;

        /// <summary>
        /// Whether passes should wrap their dispatches. The renderer feature writes this every
        /// frame from its own setting; nothing else should.
        /// </summary>
        public static bool Enabled
        {
            get => s_Enabled;
            set
            {
                if (s_Enabled == value)
                    return;

                s_Enabled = value;
                s_EnabledAt = Time.unscaledTime;
                s_WarnedSilent = false;

                // Averages from before the toggle would read as current data afterwards, and the
                // scene has usually changed in between, so drop them either way.
                for (int i = 0; i < s_Passes.Count; i++)
                    s_Passes[i].Reset();
            }
        }

        /// <summary>
        /// The sampler to wrap a dispatch in, or null while the timers are off. Registers the pass
        /// on first use.
        /// </summary>
        public static CustomSampler Sampler(string group, string name)
        {
            if (!s_Enabled)
                return null;

            if (!s_ByName.TryGetValue(name, out var pass))
            {
                pass = new Pass(group, name);
                s_ByName.Add(name, pass);
                s_Passes.Add(pass);
                s_Version++;
            }

            return pass.sampler;
        }

        /// <summary>
        /// Pulls one reading out of every recorder. Idempotent within a frame, so it is safe to
        /// call from both the render pass and whatever is displaying the results.
        /// </summary>
        public static void Tick()
        {
            int frame = Time.frameCount;
            if (frame == s_SampledFrame)
                return;

            s_SampledFrame = frame;
            if (!s_Enabled)
                return;

            for (int i = 0; i < s_Passes.Count; i++)
                s_Passes[i].Sample();

            WarnIfSilent();
        }

        /// <summary>
        /// True once the timers have been on long enough that reporting nothing is the platform's
        /// answer rather than the readings being in flight. The passes are still being sampled -
        /// the CPU side of every sampler counts its blocks - so this means the backend does not
        /// fill in the GPU side, not that the renderer is idle.
        /// </summary>
        public static bool Silent =>
            s_Enabled && s_Passes.Count > 0 && !AnyData && Time.unscaledTime - s_EnabledAt > k_SilenceGrace;

        static void WarnIfSilent()
        {
            if (s_WarnedSilent || !Silent)
                return;

            s_WarnedSilent = true;
            Debug.LogWarning(
                $"[CTGpuTimers] {s_Passes.Count} passes are being sampled but " +
                $"{SystemInfo.graphicsDeviceType} reports no GPU timings. Unity's GPU recorders are " +
                "not filled in on every backend - Metal in the editor is one, where Unity's own " +
                "Camera.Render marker comes back empty too. Use an external GPU capture there.");
        }

        /// <summary>Sum of the passes that reported on the last sampled frame.</summary>
        public static double TotalMs
        {
            get
            {
                double total = 0.0;
                for (int i = 0; i < s_Passes.Count; i++)
                {
                    var pass = s_Passes[i];
                    if (pass.active)
                        total += pass.averageMs;
                }
                return total;
            }
        }

        /// <summary>
        /// False while no pass has ever produced a sample. After a few frames with the timers on
        /// that means the platform does not report GPU timings, not that the renderer is idle.
        /// </summary>
        public static bool AnyData
        {
            get
            {
                for (int i = 0; i < s_Passes.Count; i++)
                {
                    if (s_Passes[i].hasData)
                        return true;
                }
                return false;
            }
        }
    }
}
