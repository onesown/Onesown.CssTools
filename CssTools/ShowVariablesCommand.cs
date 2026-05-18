using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using Task = System.Threading.Tasks.Task;

namespace CssTools
{
    /// <summary>
    /// "Show CSS Variables" command (Tools menu).
    /// Writes all currently known CSS custom properties to a dedicated Output Window pane.
    /// </summary>
    internal sealed class ShowVariablesCommand
    {
        public static readonly Guid CommandSetGuid = new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
        public const int CommandId = 0x0100;

        private static readonly Guid OutputPaneGuid = new Guid("b7c8d9e0-f1a2-4b3c-9d5e-6f7a8b9c0d1e");
        private const string OutputPaneTitle = "CSS Variables";

        private readonly AsyncPackage _package;

        private ShowVariablesCommand(AsyncPackage package) => _package = package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is IMenuCommandService mcs)
            {
                var cmdId = new CommandID(CommandSetGuid, CommandId);
                var cmd = new MenuCommand(Execute, cmdId);
                mcs.AddCommand(cmd);
            }
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var byName = CssVariableStore.Instance.GetAllVariablesByName();

            var sb = new StringBuilder();
            sb.AppendLine("=== CSS Custom Properties ===");
            sb.AppendLine($"{byName.Count} variable(s) found — {DateTime.Now:HH:mm:ss}");
            sb.AppendLine();

            if (byName.Count == 0)
            {
                sb.AppendLine("No CSS variables found. Make sure CSS files are part of the solution.");
            }
            else
            {
                foreach (string varName in byName.Keys.OrderBy(k => k))
                {
                    var defs = byName[varName];
                    string header = defs.Count > 1
                        ? $"{varName}  ({defs.Count} definitions)"
                        : varName;
                    sb.AppendLine(header);

                    // Group by project (same order as tooltip)
                    string? currentProject = null;
                    foreach (var def in defs)
                    {
                        if (!StringComparer.OrdinalIgnoreCase.Equals(def.ProjectName, currentProject))
                        {
                            currentProject = def.ProjectName;
                            string label = string.IsNullOrEmpty(currentProject) ? "(unknown project)" : currentProject;
                            sb.AppendLine($"  [{label}]");
                        }
                        sb.AppendLine($"    {def.Value}   {System.IO.Path.GetFileName(def.FilePath)}:{def.LineNumber}   {def.FilePath}");
                    }

                    sb.AppendLine();
                }
            }

            WriteToOutputPane(sb.ToString());
        }

        private static void WriteToOutputPane(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var shell = (IVsOutputWindow?)Package.GetGlobalService(typeof(SVsOutputWindow));
            if (shell == null) return;

            // Local copy required because GetPane/CreatePane take ref Guid
            Guid paneGuid = OutputPaneGuid;

            shell.GetPane(ref paneGuid, out IVsOutputWindowPane? pane);
            if (pane == null)
            {
                shell.CreatePane(ref paneGuid, OutputPaneTitle, fInitVisible: 1, fClearWithSolution: 0);
                shell.GetPane(ref paneGuid, out pane);
            }

            pane?.Activate();
            pane?.Clear();
            pane?.OutputString(text);
        }
    }
}
