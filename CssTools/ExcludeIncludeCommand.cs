using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;

namespace CssTools
{
    /// <summary>
    /// Handles "Exclude from CssTools" and "Include in CssTools" context menu commands.
    /// Gets the selected file/folder, converts it to a pattern, and adds it to .csstools.json.
    /// </summary>
    internal sealed class ExcludeIncludeCommand
    {
        // Command IDs (match VSCT)
        private const int ExcludeCommandId             = 0x0101;
        private const int IncludeCommandId             = 0x0102;
        private const int ExcludeFolderCommandId       = 0x0103;
        private const int IncludeFolderCommandId       = 0x0104;

        private readonly IServiceProvider _serviceProvider;

        private ExcludeIncludeCommand(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        /// <summary>
        /// Gets the singleton instance or creates one. Called from the package.
        /// </summary>
        public static ExcludeIncludeCommand? Instance { get; private set; }

        /// <summary>
        /// Initializes the singleton and attaches command handlers.
        /// Called from CssToolsPackage.InitializeAsync.
        /// </summary>
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
            if (commandService == null)
                return;

            Instance = new ExcludeIncludeCommand(package);

            // Hook up exclude command
            var excludeCmd = new CommandID(
                new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"),
                ExcludeCommandId);
            var excludeMenuCmd = new OleMenuCommand(Instance.OnExcludeCommand, excludeCmd);
            excludeMenuCmd.BeforeQueryStatus += Instance.OnBeforeQueryStatus;
            commandService.AddCommand(excludeMenuCmd);

            // Hook up include command
            var includeCmd = new CommandID(
                new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"),
                IncludeCommandId);
            var includeMenuCmd = new OleMenuCommand(Instance.OnIncludeCommand, includeCmd);
            includeMenuCmd.BeforeQueryStatus += Instance.OnBeforeQueryStatus;
            commandService.AddCommand(includeMenuCmd);

            // Folder context menu commands (always visible when folder is selected)
            var excludeFolderCmd = new CommandID(
                new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"),
                ExcludeFolderCommandId);
            commandService.AddCommand(new MenuCommand(Instance.OnExcludeFolderCommand, excludeFolderCmd));

            var includeFolderCmd = new CommandID(
                new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"),
                IncludeFolderCommandId);
            commandService.AddCommand(new MenuCommand(Instance.OnIncludeFolderCommand, includeFolderCmd));

            CssToolsLogger.Log("Exclude/Include commands initialized.");
        }

        private void OnExcludeCommand(object sender, EventArgs e)       => ExecuteCommand(isInclude: false, isFolder: false);
        private void OnIncludeCommand(object sender, EventArgs e)       => ExecuteCommand(isInclude: true,  isFolder: false);
        private void OnExcludeFolderCommand(object sender, EventArgs e) => ExecuteCommand(isInclude: false, isFolder: true);
        private void OnIncludeFolderCommand(object sender, EventArgs e) => ExecuteCommand(isInclude: true,  isFolder: true);

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand cmd)
            {
                string? path = GetSelectedPath();
                // Show only for .css files (not folders, not other file types)
                cmd.Visible = path != null &&
                              File.Exists(path) &&
                              path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ExecuteCommand(bool isInclude, bool isFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string? selectedPath = GetSelectedPath();
            if (selectedPath == null)
            {
                CssToolsLogger.Log("No file/folder selected.");
                return;
            }

            // For file commands: only allow .css files
            if (!isFolder && (!File.Exists(selectedPath) ||
                !selectedPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
            {
                CssToolsLogger.Log($"Skipped – not a CSS file: {selectedPath}");
                return;
            }

            string? solutionDir = GetSolutionDirectory();
            if (solutionDir == null)
            {
                CssToolsLogger.Log("No solution is open.");
                return;
            }

            string pattern = ConfigEditor.PathToPattern(selectedPath, solutionDir, isFolder);
            string configPath = Path.Combine(solutionDir, ".csstools.json");

            ConfigEditor.AddPattern(configPath, pattern, isInclude);

            CssToolsLogger.Log($"Added {(isInclude ? "include" : "exclude")} pattern: {pattern}");

            // Open .csstools.json so the user can see what changed.
            NavigationHelper.OpenFile(configPath);
        }

        private string? GetSelectedPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var selection = _serviceProvider.GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (selection == null)
                return null;

            selection.GetCurrentSelection(out IntPtr ppHier, out uint itemid, out IVsMultiItemSelect? multiSelect, out IntPtr ppSC);
            if (ppHier == IntPtr.Zero)
                return null;

            var hierarchy = Marshal.GetObjectForIUnknown(ppHier) as IVsHierarchy;
            if (hierarchy == null)
                return null;

            // 1. Try GetMkDocument (works for file items in most project types)
            if (hierarchy is IVsProject project &&
                project.GetMkDocument(itemid, out string? docPath) == VSConstants.S_OK &&
                !string.IsNullOrEmpty(docPath) &&
                Path.IsPathRooted(docPath) &&
                (File.Exists(docPath) || Directory.Exists(docPath)))
            {
                return docPath.TrimEnd('\\', '/');
            }

            // 2. Fallback for virtual folder nodes: traverse hierarchy upward to build the disk path.
            // Get project root directory from the root node.
            if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectDir, out object? rootDirObj) != VSConstants.S_OK ||
                !(rootDirObj is string rootDir) ||
                string.IsNullOrEmpty(rootDir))
                return null;

            // Walk from itemid up to root, collecting node names.
            var parts = new System.Collections.Generic.Stack<string>();
            uint current = itemid;
            while (current != VSConstants.VSITEMID_ROOT && current != VSConstants.VSITEMID_NIL)
            {
                if (hierarchy.GetProperty(current, (int)__VSHPROPID.VSHPROPID_Name, out object? nameObj) == VSConstants.S_OK &&
                    nameObj is string name)
                {
                    parts.Push(name);
                }

                if (hierarchy.GetProperty(current, (int)__VSHPROPID.VSHPROPID_Parent, out object? parentObj) != VSConstants.S_OK)
                    break;
                current = parentObj is int parentInt ? (uint)parentInt : VSConstants.VSITEMID_NIL;
            }

            if (parts.Count == 0)
                return null;

            // Combine: rootDir + each part
            string folderPath = rootDir.TrimEnd('\\', '/');
            foreach (string part in parts)
                folderPath = Path.Combine(folderPath, part);

            return Directory.Exists(folderPath) ? folderPath : null;
        }

        private string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_serviceProvider.GetService(typeof(SVsSolution)) is IVsSolution solution)
            {
                solution.GetSolutionInfo(out string? solutionDir, out _, out _);
                return solutionDir;
            }

            return null;
        }
    }
}
