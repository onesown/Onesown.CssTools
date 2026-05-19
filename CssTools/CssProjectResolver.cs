using System;
using System.Collections.Generic;

namespace CssTools
{
    /// <summary>
    /// Lightweight singleton that maps project root directories to project names.
    /// Populated by <see cref="CssToolsPackage"/> at startup; consumed by the stores
    /// whenever a file path cannot be matched to an already-stored project name.
    /// </summary>
    internal static class CssProjectResolver
    {
        // dir (lowercase, trailing backslash) → project name
        private static readonly Dictionary<string, string> _dirMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        /// <summary>Registers a project root directory with its display name.</summary>
        public static void RegisterProject(string projectDir, string projectName)
        {
            if (string.IsNullOrEmpty(projectDir) || string.IsNullOrEmpty(projectName))
                return;

            // Normalise: lowercase + ensure trailing separator
            string key = projectDir.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;

            lock (_lock)
                _dirMap[key] = projectName;
        }

        /// <summary>
        /// Returns the project name for <paramref name="filePath"/> by finding the longest
        /// registered directory prefix. Returns <see langword="null"/> when no match is found.
        /// </summary>
        public static string? Resolve(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;

            string? bestDir = null;
            string? bestName = null;

            lock (_lock)
            {
                foreach (var kv in _dirMap)
                {
                    if (filePath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) &&
                        (bestDir == null || kv.Key.Length > bestDir.Length))
                    {
                        bestDir  = kv.Key;
                        bestName = kv.Value;
                    }
                }
            }

            return bestName;
        }
    }
}
