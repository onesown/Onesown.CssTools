using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CssTools
{
    /// <summary>One occurrence of a CSS custom property declaration.</summary>
    internal sealed class CssVariableDefinition
    {
        /// <param name="filePath">Absolute path to the CSS file.</param>
        /// <param name="projectName">Name of the owning project (without extension).</param>
        /// <param name="lineNumber">1-based line number of the declaration.</param>
        /// <param name="value">The raw value after the colon.</param>
        public CssVariableDefinition(string filePath, string projectName, int lineNumber, string value)
        {
            FilePath    = filePath;
            ProjectName = projectName;
            LineNumber  = lineNumber;
            Value       = value;
        }

        public string FilePath    { get; }
        public string ProjectName { get; }
        public int    LineNumber  { get; }
        public string Value       { get; }
    }

    /// <summary>
    /// Thread-safe singleton. Stores every occurrence of every CSS custom property
    /// (--name: value) across all scanned files, including line numbers.
    /// Multiple files may define the same variable name (e.g. theme files).
    /// </summary>
    internal sealed class CssVariableStore
    {
        // Captures  --var-name: value  – value ends before ; { }
        private static readonly Regex DeclarationRegex = new Regex(
            @"(--[\w-]+)\s*:\s*([^;}{]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Lazy<CssVariableStore> _instance =
            new Lazy<CssVariableStore>(() => new CssVariableStore());

        public static CssVariableStore Instance => _instance.Value;

        // filePath -> list of all definitions found in that file
        private readonly ConcurrentDictionary<string, List<CssVariableDefinition>> _fileDefinitions =
            new ConcurrentDictionary<string, List<CssVariableDefinition>>(StringComparer.OrdinalIgnoreCase);

        private CssVariableStore() { }

        /// <summary>Reads the file from disk and scans it.</summary>
        /// <param name="filePath">Absolute file path.</param>
        /// <param name="projectName">Owning project name, shown in the tooltip header.</param>
        public void ScanFile(string filePath, string projectName = "")
        {
            if (CssToolsConfig.Instance.IsExcluded(filePath))
            {
                RemoveFile(filePath);
                return;
            }

            try
            {
                string content = File.ReadAllText(filePath);
                ScanText(filePath, projectName, content);
            }
            catch (Exception ex)
            {
                CssToolsLogger.Log($"Could not scan {System.IO.Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        /// <summary>Scans in-memory text (e.g. unsaved editor buffer) for the given file identity.</summary>
        public void ScanText(string filePath, string projectName, string content)
        {
            if (CssToolsConfig.Instance.IsExcluded(filePath))
            {
                RemoveFile(filePath);
                return;
            }

            var key = filePath ?? string.Empty;
            var lineStarts = BuildLineStartTable(content);

            var nameIndex = new Dictionary<string, List<CssVariableDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in DeclarationRegex.Matches(content))
            {
                string name  = m.Groups[1].Value.Trim();
                string value = m.Groups[2].Value.Trim();
                int    line  = PositionToLine(lineStarts, m.Index);
                var def = new CssVariableDefinition(key, projectName, line, value);

                if (!nameIndex.TryGetValue(name, out var list))
                    nameIndex[name] = list = new List<CssVariableDefinition>();
                list.Add(def);
            }

            _fileDefinitions[key] = nameIndex.Values.SelectMany(l => l).ToList();
            _fileNameIndex[key]   = nameIndex;

            CssToolsLogger.Log($"Scanned {System.IO.Path.GetFileName(filePath)}: {nameIndex.Count} variable(s) found.");
        }

        // filePath -> (variableName -> list of definitions in that file)
        private readonly ConcurrentDictionary<string, Dictionary<string, List<CssVariableDefinition>>> _fileNameIndex =
            new ConcurrentDictionary<string, Dictionary<string, List<CssVariableDefinition>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns all definitions for the given variable name across all scanned files,
        /// grouped by project name then sorted alphabetically by file name.
        /// </summary>
        public IReadOnlyList<CssVariableDefinition> GetDefinitions(string variableName)
        {
            var result = new List<CssVariableDefinition>();
            foreach (var fileIndex in _fileNameIndex.Values)
            {
                if (fileIndex.TryGetValue(variableName, out var defs))
                    result.AddRange(defs);
            }
            result.Sort((a, b) =>
            {
                int c = StringComparer.OrdinalIgnoreCase.Compare(a.ProjectName, b.ProjectName);
                if (c != 0) return c;
                c = StringComparer.OrdinalIgnoreCase.Compare(
                    Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath));
                return c != 0 ? c : a.LineNumber.CompareTo(b.LineNumber);
            });
            return result;
        }

        /// <summary>
        /// Returns all known variable names across all scanned files.
        /// </summary>
        public IEnumerable<string> GetAllVariableNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileIndex in _fileNameIndex.Values)
                foreach (string name in fileIndex.Keys)
                    seen.Add(name);
            return seen;
        }

        /// <summary>
        /// Returns all variables grouped by variable name for display purposes.
        /// variableName -> list of all definitions across all files.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<CssVariableDefinition>> GetAllVariablesByName()
        {
            var result = new Dictionary<string, IReadOnlyList<CssVariableDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in GetAllVariableNames())
                result[name] = GetDefinitions(name);
            return result;
        }

        /// <summary>Removes data previously scanned for a specific file (e.g. on file close/delete).</summary>
        public void RemoveFile(string filePath)
        {
            _fileDefinitions.TryRemove(filePath, out _);
            _fileNameIndex.TryRemove(filePath, out _);
        }

        /// <summary>
        /// Returns the project name previously stored for the given file path,
        /// or an empty string if the file has not been scanned yet.
        /// </summary>
        public string GetProjectName(string filePath)
        {
            if (_fileNameIndex.TryGetValue(filePath, out var idx) && idx.Count > 0)
            {
                // All definitions in one file share the same project name; grab the first.
                foreach (var list in idx.Values)
                    if (list.Count > 0) return list[0].ProjectName;
            }
            return string.Empty;
        }

        /// <summary>
        /// Removes all stored entries whose file paths are now excluded by the current config.
        /// Called after the config is reloaded.
        /// </summary>
        public void PurgeExcluded()
        {
            foreach (string path in _fileNameIndex.Keys.ToList())
            {
                if (CssToolsConfig.Instance.IsExcluded(path))
                    RemoveFile(path);
            }
        }

        // --- helpers ---

        private static int[] BuildLineStartTable(string content)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n')
                    starts.Add(i + 1);
            }
            return starts.ToArray();
        }

        /// <summary>Converts a character offset to a 1-based line number.</summary>
        private static int PositionToLine(int[] lineStarts, int position)
        {
            // Binary search for the last line start <= position.
            int lo = 0, hi = lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lineStarts[mid] <= position) lo = mid;
                else hi = mid - 1;
            }
            return lo + 1; // 1-based
        }
    }
}
