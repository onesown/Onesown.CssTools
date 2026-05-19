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

        // Maps each watched project root directory → project name (for re-scan after config reload).
        private readonly Dictionary<string, string> _dirToProject =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Debounce timer for config file changes (VS saves via temp+rename, fires multiple events).
        private System.Timers.Timer? _configReloadTimer;
        private readonly object _configTimerLock = new object();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            CssToolsLogger.Initialize(this);
            CssToolsLogger.Log("Package initializing…");
            var (projectDirs, solutionDir) = await ScanSolutionCssFilesAsync(cancellationToken);
            StartFileWatchers(projectDirs, solutionDir);
            await ShowVariablesCommand.InitializeAsync(this);
            await ExcludeIncludeCommand.InitializeAsync(this);
            CssToolsLogger.Log("Package ready.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var w in _watchers)
                    w.Dispose();
                lock (_configTimerLock)
                {
                    _configReloadTimer?.Dispose();
                    _configReloadTimer = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Enumerates all projects in the open solution, pre-scans every *.css file,
        /// and returns the set of project root directories for file watching.
        /// </summary>
        private async Task<(HashSet<string> ProjectDirs, string? SolutionDir)> ScanSolutionCssFilesAsync(CancellationToken cancellationToken)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (await GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution)
                return (dirs, null);

            solution.GetSolutionInfo(out string? solutionDir, out _, out _);
            if (solutionDir != null)
            {
                CssToolsConfig.Instance.Initialize(solutionDir);
                EnsureConfigFileExists(solutionDir);
            }

            solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, Guid.Empty, out IEnumHierarchies? hierEnum);
            if (hierEnum == null) return (dirs, solutionDir);

            var hierarchies = new IVsHierarchy[1];
            while (true)
            {
                hierEnum.Next(1, hierarchies, out uint fetched);
                if (fetched == 0) break;

                if (hierarchies[0] is IVsProject project)
                {
                    string? dir = ScanProjectCssFiles(project, out string? projectName);
                    if (dir != null)
                    {
                        dirs.Add(dir);
                        if (projectName != null)
                            _dirToProject[dir] = projectName;
                    }
                }
            }
            return (dirs, solutionDir);
        }

        // Returns the project root directory so we can watch it.
        private static string? ScanProjectCssFiles(IVsProject project, out string? projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            project.GetMkDocument(VSConstants.VSITEMID_ROOT, out string? projectPath);
            string? projectDir = projectPath != null ? Path.GetDirectoryName(projectPath) : null;
            if (projectDir == null) { projectName = null; return null; }

            projectName = projectPath != null
                ? Path.GetFileNameWithoutExtension(projectPath)
                : projectDir;

            CssProjectResolver.RegisterProject(projectDir, projectName);
            CssToolsLogger.Log($"Scanning project: {projectName}");

            if (project is IVsHierarchy hier)
                ScanHierarchyItems(hier, project, VSConstants.VSITEMID_ROOT, projectName);

            return projectDir;
        }

        private static void ScanHierarchyItems(IVsHierarchy hier, IVsProject project, uint itemId, string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project.GetMkDocument(itemId, out string? path) == 0 &&
                path != null &&
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            {
                CssVariableStore.Instance.ScanFile(path, projectName);
                CssClassStore.Instance.ScanFile(path, projectName);
            }
            hier.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_FirstChild, out object? childObj);
            uint childId = childObj is int i ? (uint)i : VSConstants.VSITEMID_NIL;

            while (childId != VSConstants.VSITEMID_NIL)
            {
                ScanHierarchyItems(hier, project, childId, projectName);
                hier.GetProperty(childId, (int)__VSHPROPID.VSHPROPID_NextSibling, out object? sibObj);
                childId = sibObj is int s ? (uint)s : VSConstants.VSITEMID_NIL;
            }
        }

        /// <summary>
        /// Starts one FileSystemWatcher per unique project directory so that CSS files
        /// edited outside the VS editor (external editor, build step, etc.) are re-scanned.
        /// </summary>
        private void StartFileWatchers(IEnumerable<string> directories, string? solutionDir)
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
                    CssClassStore.Instance.RemoveFile(e.OldFullPath);
                    if (e.FullPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    {
                        CssToolsLogger.Log($"CSS renamed: {e.OldName} → {e.Name}");
                        CssVariableStore.Instance.ScanFile(e.FullPath);
                        CssClassStore.Instance.ScanFile(e.FullPath);
                    }
                };
                watcher.Deleted += (_, e) =>
                {
                    CssToolsLogger.Log($"CSS deleted: {e.Name}");
                    CssVariableStore.Instance.RemoveFile(e.FullPath);
                    CssClassStore.Instance.RemoveFile(e.FullPath);
                };

                _watchers.Add(watcher);
            }

            // Watch .csstools.json in the solution root.
            // VS saves via atomic rename (temp file → final name), so we watch both
            // LastWrite and FileName (Renamed) events and debounce to avoid double-reload.
            if (solutionDir != null && Directory.Exists(solutionDir))
            {
                var cfgWatcher = new FileSystemWatcher(solutionDir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                cfgWatcher.Changed += OnConfigFileEvent;
                cfgWatcher.Created += OnConfigFileEvent;
                cfgWatcher.Renamed += OnConfigFileEvent;
                _watchers.Add(cfgWatcher);
            }
        }

        private void OnConfigFileEvent(object sender, FileSystemEventArgs e)
        {
            // Accept .csstools.json directly or a temp-file rename whose destination is .csstools.json
            string target = e is RenamedEventArgs re ? re.FullPath : e.FullPath;
            if (!target.EndsWith(".csstools.json", StringComparison.OrdinalIgnoreCase))
                return;

            // Debounce: reset a 300 ms timer on every incoming event.
            lock (_configTimerLock)
            {
                if (_configReloadTimer == null)
                {
                    _configReloadTimer = new System.Timers.Timer(300) { AutoReset = false };
                    _configReloadTimer.Elapsed += (_, __) =>
                    {
                        CssToolsLogger.Log("Config changed – reloading .csstools.json…");
                        CssToolsConfig.Instance.Reload();
                        CssVariableStore.Instance.PurgeExcluded();
                        CssClassStore.Instance.PurgeExcluded();
                        // Re-scan all project CSS files so previously-excluded files get picked up again.
                        RescanAllProjectCssFiles();
                    };
                }
                else
                {
                    _configReloadTimer.Stop();
                }
                _configReloadTimer.Start();
            }
        }

        private void OnCssFileChanged(object sender, FileSystemEventArgs e)
        {
            string projectName = ResolveProjectName(e.FullPath);
            CssToolsLogger.Log($"CSS changed: {e.Name}");
            CssVariableStore.Instance.ScanFile(e.FullPath, projectName);
            CssClassStore.Instance.ScanFile(e.FullPath, projectName);
        }

        /// <summary>Looks up the project name for a file path using the dir-to-project mapping.</summary>
        private string ResolveProjectName(string filePath)
        {
            // Try already-stored name first (file was scanned before).
            string stored = CssVariableStore.Instance.GetProjectName(filePath);
            if (!string.IsNullOrEmpty(stored)) return stored;

            // Find the longest matching project root directory.
            string? bestDir = null;
            foreach (string dir in _dirToProject.Keys)
            {
                if (filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase) &&
                    (bestDir == null || dir.Length > bestDir.Length))
                    bestDir = dir;
            }

            return bestDir != null ? _dirToProject[bestDir] : string.Empty;
        }

        /// <summary>
        /// Re-scans all *.css files in every known project directory.
        /// Called after config reload so previously-excluded files get picked up again.
        /// </summary>
        private void RescanAllProjectCssFiles()
        {
            CssToolsLogger.Log("Rescanning all project CSS files after config change…");
            foreach (var kv in _dirToProject)
            {
                string dir = kv.Key;
                string projectName = kv.Value;
                if (!Directory.Exists(dir)) continue;

                foreach (string cssFile in Directory.EnumerateFiles(dir, "*.css", SearchOption.AllDirectories))
                {
                    CssVariableStore.Instance.ScanFile(cssFile, projectName);
                    CssClassStore.Instance.ScanFile(cssFile, projectName);
                }
            }
            CssToolsLogger.Log("Rescan complete.");
        }

        /// <summary>Creates a default .csstools.json if none exists yet in the solution directory.</summary>
        private static void EnsureConfigFileExists(string solutionDir)
        {
            string configPath = Path.Combine(solutionDir, ".csstools.json");
            if (File.Exists(configPath)) return;

            try
            {
                const string defaultContent =
                    "{\n" +
                    "  \"exclude\": [],\n" +
                    "  \"include\": []\n" +
                    "}\n";
                File.WriteAllText(configPath, defaultContent);
                CssToolsLogger.Log($"Created default config: {configPath}");
            }
            catch (Exception ex)
            {
                CssToolsLogger.Log($"Could not create .csstools.json: {ex.Message}");
            }
        }
    }
}

