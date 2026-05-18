using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CssTools
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(CssToolsPackage.PackageGuidString)]
    public sealed class CssToolsPackage : AsyncPackage
    {
        public const string PackageGuidString = "0fb472c4-bf6c-4b68-9722-3b655fb74c06";

        // Keep watchers alive for the lifetime of the package.
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var projectDirs = await ScanSolutionCssFilesAsync(cancellationToken);
            StartFileWatchers(projectDirs);
            await ShowVariablesCommand.InitializeAsync(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var w in _watchers)
                    w.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Enumerates all projects in the open solution, pre-scans every *.css file,
        /// and returns the set of project root directories for file watching.
        /// </summary>
        private async Task<HashSet<string>> ScanSolutionCssFilesAsync(CancellationToken cancellationToken)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (await GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution)
                return dirs;

            solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, Guid.Empty, out IEnumHierarchies? hierEnum);
            if (hierEnum == null) return dirs;

            var hierarchies = new IVsHierarchy[1];
            while (true)
            {
                hierEnum.Next(1, hierarchies, out uint fetched);
                if (fetched == 0) break;

                if (hierarchies[0] is IVsProject project)
                {
                    string? dir = ScanProjectCssFiles(project);
                    if (dir != null) dirs.Add(dir);
                }
            }
            return dirs;
        }

        // Returns the project root directory so we can watch it.
        private static string? ScanProjectCssFiles(IVsProject project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            project.GetMkDocument(VSConstants.VSITEMID_ROOT, out string? projectPath);
            string? projectDir = projectPath != null ? Path.GetDirectoryName(projectPath) : null;
            if (projectDir == null) return null;

            if (project is IVsHierarchy hier)
                ScanHierarchyItems(hier, project, VSConstants.VSITEMID_ROOT);

            return projectDir;
        }

        private static void ScanHierarchyItems(IVsHierarchy hier, IVsProject project, uint itemId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project.GetMkDocument(itemId, out string? path) == 0 &&
                path != null &&
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            {
                CssVariableStore.Instance.ScanFile(path);
            }

            hier.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_FirstChild, out object? childObj);
            uint childId = childObj is int i ? (uint)i : VSConstants.VSITEMID_NIL;

            while (childId != VSConstants.VSITEMID_NIL)
            {
                ScanHierarchyItems(hier, project, childId);
                hier.GetProperty(childId, (int)__VSHPROPID.VSHPROPID_NextSibling, out object? sibObj);
                childId = sibObj is int s ? (uint)s : VSConstants.VSITEMID_NIL;
            }
        }

        /// <summary>
        /// Starts one FileSystemWatcher per unique project directory so that CSS files
        /// edited outside the VS editor (external editor, build step, etc.) are re-scanned.
        /// </summary>
        private void StartFileWatchers(IEnumerable<string> directories)
        {
            foreach (string dir in directories)
            {
                if (!Directory.Exists(dir)) continue;

                var watcher = new FileSystemWatcher(dir, "*.css")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnCssFileChanged;
                watcher.Created += OnCssFileChanged;
                watcher.Renamed += (_, e) =>
                {
                    CssVariableStore.Instance.RemoveFile(e.OldFullPath);
                    CssVariableStore.Instance.ScanFile(e.FullPath);
                };
                watcher.Deleted += (_, e) => CssVariableStore.Instance.RemoveFile(e.FullPath);

                _watchers.Add(watcher);
            }
        }

        private static void OnCssFileChanged(object sender, FileSystemEventArgs e) =>
            CssVariableStore.Instance.ScanFile(e.FullPath);
    }
}

