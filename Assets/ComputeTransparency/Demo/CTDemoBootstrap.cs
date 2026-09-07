using System.Text;
using UnityEngine;

namespace ComputeTransparency.Demo
{
    /// <summary>
    /// Takes the frame rate off whatever the platform decided, and collects the handful of device
    /// facts that decide whether this demo can draw anything at all.
    /// </summary>
    /// <remarks>
    /// Android caps a player that never asks for anything else, and the quality level the demo
    /// ships on has vSync on top of that, so a build measures the display rather than the
    /// renderer. Both are overridden here rather than in project settings so the number is visible
    /// next to the code that depends on it.
    ///
    /// The device report exists because the first Android build failed in three ways at once and
    /// none of them are visible from the device without a cable. Every line in it is a fact that
    /// picks between the plausible causes.
    /// </remarks>
    [DefaultExecutionOrder(-200)]
    public class CTDemoBootstrap : MonoBehaviour
    {
        [Tooltip("0 leaves the platform default alone. Anything else is applied with vSync off, " +
                 "so the frame rate reports the renderer and not the display.")]
        public int targetFrameRate = 300;

        void OnEnable()
        {
            if (targetFrameRate <= 0)
                return;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;
        }

        /// <summary>
        /// One line per fact, short enough to read on a phone. Written for the three failures this
        /// demo has actually had: a locked frame rate, an atlas that packed nothing, and a shader
        /// variant that did not survive the build.
        /// </summary>
        public static string DeviceReport(CTDemoCloud cloud)
        {
            var report = new StringBuilder();

            report.Append(SystemInfo.graphicsDeviceType)
                  .Append("  sm").Append(SystemInfo.graphicsShaderLevel)
                  .Append("  ").Append(Screen.width).Append('x').Append(Screen.height)
                  .Append("  fps cap ").Append(Application.targetFrameRate)
                  .Append("  vSync ").Append(QualitySettings.vSyncCount)
                  .AppendLine();

            report.Append("compute ").Append(SystemInfo.supportsComputeShaders ? "yes" : "NO")
                  .Append("  instancing ").Append(SystemInfo.supportsInstancing ? "yes" : "NO")
                  .Append("  copy ").Append(SystemInfo.copyTextureSupport)
                  .AppendLine();

            if (cloud == null)
                return report.ToString();

            var array = cloud.atlas != null ? cloud.atlas.Texture : null;
            report.Append("atlas ");
            if (array == null)
                report.Append("MISSING");
            else
                report.Append(array.width).Append('x').Append(array.height)
                      .Append(" x").Append(array.depth).Append(' ').Append(array.graphicsFormat)
                      .Append(' ').Append(array.mipmapCount).Append(" mips");
            report.AppendLine();

            var material = cloud.instancedMaterial;
            report.Append("sprite shader ");
            if (material == null || material.shader == null)
                report.Append("MISSING");
            else
                report.Append(material.shader.isSupported ? "ok" : "UNSUPPORTED")
                      .Append(material.enableInstancing ? ", instanced" : ", NOT INSTANCED");

            return report.ToString();
        }
    }
}
