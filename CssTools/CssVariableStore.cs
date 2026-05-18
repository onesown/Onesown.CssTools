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
        /// <param name="lineNumber">1-based line number of the declaration.</param>
        /// <param name="value">The raw value after the colon.</param>
        public CssVariableDefinition(string filePath, int lineNumber, string value)
        {
            FilePath = filePath;
            LineNumber = lineNumber;
            Value = value;
        }

        public string FilePath   { get; }
        public int    LineNumber { get; }
        public string Value      { get; }
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
        public void ScanFile(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath);
                ScanText(filePath, content);
            }
            catch (Exception)
            {
                // File may be locked or deleted – ignore silently.
            }
        }

        /// <summary>Scans in-memory text (e.g. unsaved editor buffer) for the given file identity.</summary>
        public void ScanText(string filePath, string content)
        {
            var defs = new List<CssVariableDefinition>();

            // Build a line-start offset table so we can convert match position -> line number.
            var lineStarts = BuildLineStartTable(content);

            foreach (Match m in DeclarationRegex.Matches(content))
            {
                string name  = m.Groups[1].Value.Trim();
                string value = m.Groups[2].Value.Trim();
                int    line  = PositionToLine(lineStarts, m.Index); // 1-based
                defs.Add(new CssVariableDefinition(filePath ?? string.Empty, line, value));

                // Store under the variable name for quick lookup.
                // We keep a separate name-indexed structure built lazily via GetDefinitions().
            }

            // Also tag each definition with its name for the lookup path.
            // Re-parse names from the match list by index.
            var namedDefs = new List<(string Name, CssVariableDefinition Def)>();
            foreach (Match m in DeclarationRegex.Matches(content))
            {
                string name  = m.Groups[1].Value.Trim();
                string value = m.Groups[2].Value.Trim();
                int    line  = PositionToLine(lineStarts, m.Index);
                namedDefs.Add((name, new CssVariableDefinition(filePath ?? string.Empty, line, value)));
            }

            _fileDefinitions[filePath ?? string.Empty] = namedDefs.Select(t => t.Def).ToList();

            // Rebuild the name index for this file.
            var nameIndex = new Dictionary<string, List<CssVariableDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, def) in namedDefs)
            {
                if (!nameIndex.TryGetValue(name, out var list))
                    nameIndex[name] = list = new List<CssVariableDefinition>();
                list.Add(def);
            }
            _fileNameIndex[filePath ?? string.Empty] = nameIndex;
        }

        // filePath -> (variableName -> list of definitions in that file)
        private readonly ConcurrentDictionary<string, Dictionary<string, List<CssVariableDefinition>>> _fileNameIndex =
            new ConcurrentDictionary<string, Dictionary<string, List<CssVariableDefinition>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns all definitions for the given variable name across all scanned files,
        /// ordered by file path then line number.
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
                int c = StringComparer.OrdinalIgnoreCase.Compare(a.FilePath, b.FilePath);
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
