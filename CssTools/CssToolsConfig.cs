using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CssTools
{
    /// <summary>
    /// Reads and caches <c>.csstools.json</c> from the solution root.
    /// Supports <c>exclude</c> and <c>include</c> glob patterns;
    /// <c>include</c> always wins over <c>exclude</c>.
    /// </summary>
    /// <remarks>
    /// Minimal JSON parser via <see cref="JavaScriptSerializer"/> – no extra NuGet dependency.
    /// Example .csstools.json:
    /// <code>
    /// {
    ///   "exclude": [ "**/themes/**", "legacy.css" ],
    ///   "include": [ "**/themes/light.css" ]
    /// }
    /// </code>
    /// </remarks>
    internal sealed class CssToolsConfig
    {
        private static readonly Lazy<CssToolsConfig> _instance =
            new Lazy<CssToolsConfig>(() => new CssToolsConfig());

        public static CssToolsConfig Instance => _instance.Value;

        private string? _solutionDir;
        private List<Regex> _excludePatterns = new List<Regex>();
        private List<Regex> _includePatterns = new List<Regex>();
        private readonly object _lock = new object();
        private volatile bool _isInitialized;

        /// <summary>True once <see cref="Initialize"/> has been called.</summary>
        public bool IsInitialized => _isInitialized;

        private CssToolsConfig() { }

        /// <summary>Absolute path to the .csstools.json file (may not exist yet).</summary>
        public string? ConfigFilePath =>
            _solutionDir != null ? Path.Combine(_solutionDir, ".csstools.json") : null;

        /// <summary>
        /// Sets the solution root directory and loads the config file if it exists.
        /// Should be called once from the package on startup.
        /// </summary>
        public void Initialize(string solutionDir)
        {
            _solutionDir = solutionDir;
            _isInitialized = true;
            Reload();
        }

        /// <summary>Re-reads the config file from disk.</summary>
        public void Reload()
        {
            if (_solutionDir == null) return;

            string configPath = Path.Combine(_solutionDir, ".csstools.json");

            var excludes = new List<Regex>();
            var includes = new List<Regex>();

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var serializer = new JavaScriptSerializer();
                    var dict = serializer.Deserialize<Dictionary<string, object>>(json);

                    if (dict != null)
                    {
                        excludes.AddRange(ParsePatterns(dict, "exclude"));
                        includes.AddRange(ParsePatterns(dict, "include"));
                    }
                }
                catch (Exception)
                {
                    // Malformed config – treat as empty (no filtering).
                }
            }

            lock (_lock)
            {
                _excludePatterns = excludes;
                _includePatterns = includes;
            }

            CssToolsLogger.Log(
                $"Config reloaded: {configPath} — {excludes.Count} exclude pattern(s), {includes.Count} include pattern(s).");

            // Remove any already-scanned files that the new config now excludes.
            CssVariableStore.Instance.PurgeExcluded();
        }

        /// <summary>
        /// Returns <c>true</c> when the given file path should be skipped.
        /// A file is skipped when it matches at least one exclude pattern
        /// AND does NOT match any include pattern.
        /// </summary>
        public bool IsExcluded(string filePath)
        {
            // Normalise to forward slashes for consistent matching.
            string normalized = filePath.Replace('\\', '/');

            List<Regex> excludes, includes;
            lock (_lock)
            {
                excludes = _excludePatterns;
                includes = _includePatterns;
            }

            if (excludes.Count == 0) return false;

            bool excluded = false;
            foreach (var rx in excludes)
            {
                if (rx.IsMatch(normalized)) { excluded = true; break; }
            }

            if (!excluded) return false;

            // include overrides exclude
            foreach (var rx in includes)
            {
                if (rx.IsMatch(normalized)) return false;
            }

            return true;
        }

        // --- helpers ---

        private static IEnumerable<Regex> ParsePatterns(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out object? raw)) yield break;

            // JavaScriptSerializer deserializes JSON arrays as System.Collections.ArrayList
            var list = raw as System.Collections.ArrayList;
            if (list == null) yield break;

            foreach (object? item in list)
            {
                if (item is string pattern && !string.IsNullOrWhiteSpace(pattern))
                {
                    Regex? rx = GlobToRegex(pattern);
                    if (rx != null) yield return rx;
                }
            }
        }

        /// <summary>
        /// Converts a glob pattern to a <see cref="Regex"/>.
        /// Supports <c>**</c> (any path segment), <c>*</c> (any chars except /), <c>?</c>.
        /// </summary>
        private static Regex? GlobToRegex(string glob)
        {
            try
            {
                // Normalise separators
                string g = glob.Replace('\\', '/');

                var sb = new System.Text.StringBuilder("(?i)"); // case-insensitive
                int i = 0;
                while (i < g.Length)
                {
                    if (g[i] == '*' && i + 1 < g.Length && g[i + 1] == '*')
                    {
                        // ** – matches any number of path segments including separators
                        sb.Append(".*");
                        i += 2;
                        // consume trailing slash if present
                        if (i < g.Length && g[i] == '/') i++;
                    }
                    else if (g[i] == '*')
                    {
                        sb.Append("[^/]*");
                        i++;
                    }
                    else if (g[i] == '?')
                    {
                        sb.Append("[^/]");
                        i++;
                    }
                    else
                    {
                        sb.Append(Regex.Escape(g[i].ToString()));
                        i++;
                    }
                }

                // Pattern may match the full path or just the tail (e.g. bare "legacy.css")
                // We allow it to match anywhere in the normalised path.
                return new Regex(sb.ToString(), RegexOptions.Compiled);
            }
            catch
            {
                return null;
            }
        }
    }
}
