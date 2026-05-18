using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace CssTools
{
    /// <summary>
    /// Writes messages to a dedicated "CssTools" pane in the VS Output window.
    /// Call <see cref="Initialize"/> once from the package, then use <see cref="Log"/> anywhere.
    /// </summary>
    internal static class CssToolsLogger
    {
        private static IVsOutputWindowPane? _pane;
        private static Guid _paneGuid = new Guid("A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D");

        /// <summary>Creates (or reuses) the Output pane. Must be called on the UI thread.</summary>
        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (serviceProvider.GetService(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow)
            {
                // Create the pane if it doesn't exist yet; activate it so it's visible.
                outputWindow.CreatePane(ref _paneGuid, "CssTools", fInitVisible: 1, fClearWithSolution: 0);
                outputWindow.GetPane(ref _paneGuid, out _pane);
            }
        }

        /// <summary>Appends a line to the CssTools Output pane. Thread-safe (marshals to UI thread if needed).</summary>
        public static void Log(string message)
        {
            string line = $"[CssTools {DateTime.Now:HH:mm:ss}] {message}\n";

            if (ThreadHelper.CheckAccess())
            {
                WriteToPane(line);
            }
            else
            {
                // Fire-and-forget: logging must not block the caller.
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    WriteToPane(line);
                });
            }
        }

        private static void WriteToPane(string line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _pane?.OutputStringThreadSafe(line);
        }
    }
}
