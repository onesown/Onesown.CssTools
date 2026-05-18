using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CssTools
{
    /// <summary>
    /// Helper to read and modify .csstools.json directly on disk
    /// (beyond what CssToolsConfig caches).
    /// </summary>
    internal static class ConfigEditor
    {
        /// <summary>
        /// Adds a pattern to the exclude or include list in .csstools.json.
        /// Removes it from the opposite list first to avoid contradictions.
        /// Creates the file if it doesn't exist.
        /// </summary>
        public static void AddPattern(string configPath, string pattern, bool isInclude)
        {
            if (string.IsNullOrEmpty(pattern))
                return;

            try
            {
                var dict = ReadConfigFile(configPath);

                string targetKey  = isInclude ? "include" : "exclude";
                string oppositeKey = isInclude ? "exclude" : "include";

                // Remove from opposite list to avoid contradictions
                if (dict.ContainsKey(oppositeKey))
                {
                    var oppositeList = dict[oppositeKey] as List<string> ?? new List<string>();
                    oppositeList.Remove(pattern);
                    dict[oppositeKey] = oppositeList;
                }

                // Ensure target list exists
                if (!dict.ContainsKey(targetKey))
                    dict[targetKey] = new List<string>();

                var list = dict[targetKey] as List<string> ?? new List<string>();

                // Don't add duplicates
                if (!list.Contains(pattern))
                {
                    list.Add(pattern);
                    dict[targetKey] = list;
                }

                WriteConfigFile(configPath, dict);
                CssToolsLogger.Log($"Added {targetKey} pattern: {pattern}");
            }
            catch (Exception ex)
            {
                CssToolsLogger.Log($"Could not add pattern to config: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a pattern from exclude or include list.
        /// </summary>
        public static void RemovePattern(string configPath, string pattern, bool isInclude)
        {
            if (string.IsNullOrEmpty(pattern))
                return;

            try
            {
                var dict = ReadConfigFile(configPath);

                string key = isInclude ? "include" : "exclude";
                if (!dict.ContainsKey(key))
                    return;

                var list = dict[key] as List<string> ?? new List<string>();
                if (list.Remove(pattern))
                {
                    dict[key] = list;
                    WriteConfigFile(configPath, dict);
                    CssToolsLogger.Log($"Removed {key} pattern: {pattern}");
                }
            }
            catch (Exception ex)
            {
                CssToolsLogger.Log($"Could not remove pattern from config: {ex.Message}");
            }
        }

        /// <summary>
        /// Converts a file or folder path to a glob pattern relative to the solution root.
        /// E.g. "D:\Proj\Onesown.Blazor.Components\themes\theme8.css" → "**/themes/theme8.css"
        /// or "**/Onesown.Blazor.Components/themes/**" for folders.
        /// </summary>
        public static string PathToPattern(string fullPath, string solutionDir, bool isFolder)
        {
            // Normalize paths to forward slashes
            fullPath   = Path.GetFullPath(fullPath).Replace('\\', '/');
            solutionDir = Path.GetFullPath(solutionDir).Replace('\\', '/').TrimEnd('/') + "/";

            // Make relative to solution root
            string relative = fullPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(solutionDir.Length)
                : fullPath;

            if (isFolder)
            {
                // "**/Onesown.Blazor.Components/themes/**"
                relative = relative.TrimEnd('/');
                return $"**/{relative}/**";
            }
            else
            {
                // "**/Onesown.Blazor.Components/themes/theme8.css"
                return $"**/{relative}";
            }
        }

        // --- Helpers ---

        private static Dictionary<string, object> ReadConfigFile(string configPath)
        {
            var result = new Dictionary<string, object>
            {
                { "exclude", new List<string>() },
                { "include", new List<string>() }
            };

            if (!File.Exists(configPath))
                return result;

            try
            {
                string json = File.ReadAllText(configPath);
                var serializer = new JavaScriptSerializer();
                var dict = serializer.Deserialize<Dictionary<string, object>>(json);

                if (dict != null)
                {
                    foreach (string key in new[] { "exclude", "include" })
                    {
                        if (dict.TryGetValue(key, out object? raw))
                        {
                            var list = new List<string>();
                            if (raw is System.Collections.ArrayList arr)
                            {
                                foreach (object? item in arr)
                                {
                                    if (item is string s)
                                        list.Add(s);
                                }
                            }
                            result[key] = list;
                        }
                    }
                }
            }
            catch
            {
                // Malformed JSON – return defaults
            }

            return result;
        }

        private static void WriteConfigFile(string configPath, Dictionary<string, object> dict)
        {
            // Ensure directory exists
            string? dir = Path.GetDirectoryName(configPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Pretty-print JSON
            var exclude = new List<string>();
            dict.TryGetValue("exclude", out object? excludeObj);
            if (excludeObj is List<string> excList)
                exclude = excList;

            var include = new List<string>();
            dict.TryGetValue("include", out object? includeObj);
            if (includeObj is List<string> incList)
                include = incList;

            var lines = new List<string>
            {
                "{",
                "  \"exclude\": ["
            };

            foreach (var pattern in exclude ?? new List<string>())
                lines.Add($"    \"{EscapeJson(pattern)}\",");

            // Remove trailing comma from last item
            if (lines.Count > 2 && lines.Last().EndsWith(","))
                lines[lines.Count - 1] = lines.Last().TrimEnd(',');

            lines.Add("  ],");
            lines.Add("  \"include\": [");

            foreach (var pattern in include ?? new List<string>())
                lines.Add($"    \"{EscapeJson(pattern)}\",");

            // Remove trailing comma from last item
            if (lines.Count > 0 && lines.Last().EndsWith(","))
                lines[lines.Count - 1] = lines.Last().TrimEnd(',');

            lines.Add("  ]");
            lines.Add("}");

            File.WriteAllText(configPath, string.Join(Environment.NewLine, lines));
        }

        private static string EscapeJson(string s)
        {
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
