using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CssTools
{
    /// <summary>One occurrence of a CSS class selector declaration.</summary>
    internal sealed class CssClassDefinition
    {
        /// <param name="filePath">Absolute path to the CSS file.</param>
        /// <param name="projectName">Name of the owning project (without extension).</param>
        /// <param name="lineNumber">1-based line number of the selector.</param>
        public CssClassDefinition(string filePath, string projectName, int lineNumber)
        {
            FilePath    = filePath;
            ProjectName = projectName;
            LineNumber  = lineNumber;
        }

        public string FilePath    { get; }
        public string ProjectName { get; }
        public int    LineNumber  { get; }
    }

    /// <summary>
    /// Thread-safe singleton. Stores every occurrence of every CSS class selector
    /// (.className) across all scanned files, including line numbers.
    /// Also stores element/tag selectors (e.g. h3, body, :root).
    /// </summary>
    internal sealed class CssClassStore
    {
        // Matches class selectors like  .foo  or  .foo-bar  in CSS.
        // Group 1 captures the class name without the leading dot.
        private static readonly Regex ClassSelectorRegex = new Regex(
            @"(?<![:\w])\.(?!\d)([\w-]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Matches element/tag selectors in a CSS selector list (the part before '{').
        // Captures identifiers that are NOT preceded by '.', '#', or another word char.
        // Also captures :root and similar pseudo-class-as-tag selectors.
        // Group 1: the tag name (without leading colon for pseudo-classes like :root).
        // Strategy: find every rule block, extract the selector part, then match tokens.
        private static readonly Regex RuleBlockRegex = new Regex(
            @"([^{}]+)\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Inside a selector list: matches bare element names (not preceded by . # : [ or word char)
        // and :root / :host etc. (pseudo-class element selectors used as type selectors)
        private static readonly Regex TagInSelectorRegex = new Regex(
            @"(?<![.#\[:\w])\b([a-zA-Z][a-zA-Z0-9-]*)(?![\w-])|(?<!:)(:root|:host(?:-context)?)\b",
            RegexOptions.Compiled);

        private static readonly Lazy<CssClassStore> _instance =
            new Lazy<CssClassStore>(() => new CssClassStore());

        public static CssClassStore Instance => _instance.Value;

        // filePath -> (className -> list of definitions in that file)
        private readonly ConcurrentDictionary<string, Dictionary<string, List<CssClassDefinition>>> _fileNameIndex =
            new ConcurrentDictionary<string, Dictionary<string, List<CssClassDefinition>>>(StringComparer.OrdinalIgnoreCase);

        // filePath -> (tagName -> list of definitions in that file)
        private readonly ConcurrentDictionary<string, Dictionary<string, List<CssClassDefinition>>> _tagFileNameIndex =
            new ConcurrentDictionary<string, Dictionary<string, List<CssClassDefinition>>>(StringComparer.OrdinalIgnoreCase);

        private CssClassStore() { }

        /// <summary>Reads the file from disk and scans it.</summary>
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
                CssToolsLogger.Log($"[ClassStore] Could not scan {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        /// <summary>Scans in-memory text for the given file identity.</summary>
        public void ScanText(string filePath, string projectName, string content)
        {
            if (CssToolsConfig.Instance.IsExcluded(filePath))
            {
                RemoveFile(filePath);
                return;
            }

            var key = filePath ?? string.Empty;

            // If the caller doesn't know the project name (e.g. MEF buffer scan before package init),
            // keep the name already stored by the startup scan, or resolve via directory mapping.
            if (string.IsNullOrEmpty(projectName))
            {
                projectName = GetProjectName(key);
                if (string.IsNullOrEmpty(projectName))
                    projectName = CssProjectResolver.Resolve(key) ?? string.Empty;
            }
            var lineStarts = BuildLineStartTable(content);

            // --- Class selectors ---
            var nameIndex = new Dictionary<string, List<CssClassDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ClassSelectorRegex.Matches(content))
            {
                string name = m.Groups[1].Value;
                int    line = PositionToLine(lineStarts, m.Index);
                var def = new CssClassDefinition(key, projectName, line);

                if (!nameIndex.TryGetValue(name, out var list))
                    nameIndex[name] = list = new List<CssClassDefinition>();

                if (list.Count == 0 || list[list.Count - 1].LineNumber != line)
                    list.Add(def);
            }
            _fileNameIndex[key] = nameIndex;

            // --- Tag/element selectors ---
            var tagIndex = new Dictionary<string, List<CssClassDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (Match ruleMatch in RuleBlockRegex.Matches(content))
            {
                string selectorPart = ruleMatch.Groups[1].Value;
                // Strip comments from selector part
                selectorPart = Regex.Replace(selectorPart, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
                // Use the position of '{' so ReadClassBodyLines starts on the correct line
                int bracePos = ruleMatch.Index + ruleMatch.Length - 1;
                int line = PositionToLine(lineStarts, bracePos);

                foreach (Match tagMatch in TagInSelectorRegex.Matches(selectorPart))
                {
                    // Group 1: bare element name; Group 2: pseudo-class tag like :root
                    string tagName = tagMatch.Groups[1].Success && tagMatch.Groups[1].Length > 0
                        ? tagMatch.Groups[1].Value
                        : tagMatch.Groups[2].Value.TrimStart(':');

                    if (string.IsNullOrEmpty(tagName)) continue;

                    // Skip CSS at-rule keywords and non-tag identifiers that appear as part of values
                    if (IsAtRuleKeyword(tagName)) continue;

                    var def = new CssClassDefinition(key, projectName, line);
                    if (!tagIndex.TryGetValue(tagName, out var list))
                        tagIndex[tagName] = list = new List<CssClassDefinition>();

                    if (list.Count == 0 || list[list.Count - 1].LineNumber != line)
                        list.Add(def);
                }
            }
            _tagFileNameIndex[key] = tagIndex;

            CssToolsLogger.Log($"[ClassStore] Scanned {Path.GetFileName(filePath)}: {nameIndex.Count} class(es), {tagIndex.Count} tag(s) found.");
        }

        /// <summary>Returns all definitions for the given class name, sorted by project then file.</summary>
        public IReadOnlyList<CssClassDefinition> GetDefinitions(string className)
        {
            var result = new List<CssClassDefinition>();
            foreach (var fileIndex in _fileNameIndex.Values)
            {
                if (fileIndex.TryGetValue(className, out var defs))
                    result.AddRange(defs);
            }
            result.Sort((a, b) =>
            {
                int c = StringComparer.OrdinalIgnoreCase.Compare(a.ProjectName, b.ProjectName);
                if (c != 0) return c;
                c = a.LineNumber.CompareTo(b.LineNumber);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(
                    Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath));
            });
            return result;
        }

        /// <summary>Returns all definitions for the given HTML tag/element selector, sorted by project then line.</summary>
        public IReadOnlyList<CssClassDefinition> GetTagDefinitions(string tagName)
        {
            var result = new List<CssClassDefinition>();
            foreach (var fileIndex in _tagFileNameIndex.Values)
            {
                if (fileIndex.TryGetValue(tagName, out var defs))
                    result.AddRange(defs);
            }
            result.Sort((a, b) =>
            {
                int c = StringComparer.OrdinalIgnoreCase.Compare(a.ProjectName, b.ProjectName);
                if (c != 0) return c;
                c = a.LineNumber.CompareTo(b.LineNumber);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(
                    Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath));
            });
            return result;
        }

        /// <summary>Returns the project name stored for the given file path.</summary>
        public string GetProjectName(string filePath)
        {
            if (_fileNameIndex.TryGetValue(filePath, out var idx) && idx.Count > 0)
            {
                foreach (var list in idx.Values)
                    if (list.Count > 0) return list[0].ProjectName;
            }
            return string.Empty;
        }

        /// <summary>Removes data previously scanned for a specific file.</summary>
        public void RemoveFile(string filePath)
        {
            _fileNameIndex.TryRemove(filePath, out _);
            _tagFileNameIndex.TryRemove(filePath, out _);
        }

        /// <summary>Removes all stored entries whose file paths are excluded by the current config.</summary>
        public void PurgeExcluded()
        {
            foreach (string path in _fileNameIndex.Keys.ToList())
            {
                if (CssToolsConfig.Instance.IsExcluded(path))
                    RemoveFile(path);
            }
            foreach (string path in _tagFileNameIndex.Keys.ToList())
            {
                if (CssToolsConfig.Instance.IsExcluded(path))
                    RemoveFile(path);
            }
        }

        // CSS at-rule keyword identifiers that should not be treated as tag selectors.
        private static readonly HashSet<string> _atRuleKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "from", "to", "and", "not", "only", "or",
            "screen", "print", "all", "speech",
            "color", "grid", "scan", "update", "overflow",
            "min", "max", "auto", "none", "normal",
            "initial", "inherit", "unset", "revert",
        };

        private static bool IsAtRuleKeyword(string name) => _atRuleKeywords.Contains(name);

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

        private static int PositionToLine(int[] lineStarts, int position)
        {
            int lo = 0, hi = lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lineStarts[mid] <= position) lo = mid;
                else hi = mid - 1;
            }
            return lo + 1;
        }
    }
}
