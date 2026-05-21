using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;

namespace CssTools
{
    /// <summary>
    /// Provides QuickInfo (hover tooltip) content for CSS custom properties.
    /// Shows every definition of the variable (value + file + line), each clickable.
    /// </summary>
    internal sealed class CssVariableQuickInfoSource : IAsyncQuickInfoSource
    {
        // Matches  var(--name)  – captures the variable name in group 1
        private static readonly Regex VarUsageRegex = new Regex(
            @"var\(\s*(--[\w-]+)\s*\)",
            RegexOptions.Compiled);

        // Matches a bare  --name  token
        private static readonly Regex BareVarRegex = new Regex(
            @"--[\w-]+",
            RegexOptions.Compiled);

        private readonly ITextBuffer _buffer;
        private bool _disposed;

        public CssVariableQuickInfoSource(ITextBuffer buffer) => _buffer = buffer;

        public Task<QuickInfoItem?> GetQuickInfoItemAsync(
            IAsyncQuickInfoSession session,
            CancellationToken cancellationToken)
        {
            if (_disposed)
                return Task.FromResult<QuickInfoItem?>(null);

            SnapshotPoint? triggerPoint = session.GetTriggerPoint(_buffer.CurrentSnapshot);
            if (triggerPoint == null)
                return Task.FromResult<QuickInfoItem?>(null);

            ITextSnapshotLine line = triggerPoint.Value.GetContainingLine();
            string lineText = line.GetText();
            int posOnLine = triggerPoint.Value.Position - line.Start.Position;

            // 1. Try  var(--name)
            string? varName = FindVarName(lineText, posOnLine, VarUsageRegex, out ITrackingSpan? span, line, _buffer.CurrentSnapshot);

            // 2. Fallback: bare  --name
            if (varName == null)
                varName = FindVarName(lineText, posOnLine, BareVarRegex, out span, line, _buffer.CurrentSnapshot);

            if (varName == null || span == null)
                return Task.FromResult<QuickInfoItem?>(null);

            IReadOnlyList<CssVariableDefinition> defs = CssVariableStore.Instance.GetDefinitions(varName);
            if (defs.Count == 0)
                return Task.FromResult<QuickInfoItem?>(null);

            // Determine the active file path so its definitions appear first.
            string? activeFilePath = null;
            if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument activeDoc))
                activeFilePath = activeDoc.FilePath;

            // Re-sort: entries from the active file first, then original order.
            var sortedDefs = new List<CssVariableDefinition>(defs);
            if (!string.IsNullOrEmpty(activeFilePath))
            {
                sortedDefs.Sort((a, b) =>
                {
                    bool aActive = StringComparer.OrdinalIgnoreCase.Equals(a.FilePath, activeFilePath);
                    bool bActive = StringComparer.OrdinalIgnoreCase.Equals(b.FilePath, activeFilePath);
                    if (aActive != bActive) return aActive ? -1 : 1;
                    int c = StringComparer.OrdinalIgnoreCase.Compare(a.ProjectName, b.ProjectName);
                    if (c != 0) return c;
                    return a.LineNumber.CompareTo(b.LineNumber);
                });
            }

            // Build a ContainerElement: header + one project group each containing its definitions.
            var rows = new List<object>();

            // Limit the number of definitions shown to avoid oversized tooltips.
            const int MaxDefinitions = 5; // kept as fallback
            int totalDefs    = sortedDefs.Count;
            int shownDefs    = Math.Min(totalDefs, CssToolsConfig.Instance.MaxDefinitions > 0 ? CssToolsConfig.Instance.MaxDefinitions : MaxDefinitions);
            int hiddenDefs   = totalDefs - shownDefs;

            // Group definitions by project, preserving the sort order from sortedDefs.
            string? currentProject = null;
            bool firstProject = true;
            for (int di = 0; di < shownDefs; di++)
            {
                CssVariableDefinition def = sortedDefs[di];
                // Project header – only when the project changes
                if (!StringComparer.OrdinalIgnoreCase.Equals(def.ProjectName, currentProject))
                {
                    currentProject = def.ProjectName;
                    string label = string.IsNullOrEmpty(currentProject) ? "(unknown project)" : currentProject;
                    if (!firstProject)
                        rows.Add(new ClassifiedTextElement(
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, " ")));
                    firstProject = false;
                    rows.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(
                            PredefinedClassificationTypeNames.PreprocessorKeyword,
                            label)));
                }

                string fileName = Path.GetFileName(def.FilePath);
                string capturedFilePath = def.FilePath;
                int    capturedLine     = def.LineNumber;

                var locationRun = new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.SymbolDefinition,
                    $"{fileName}:{def.LineNumber}",
                    () => ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        NavigationHelper.NavigateTo(capturedFilePath, capturedLine);
                    }),
                    tooltip: capturedFilePath,
                    style: ClassifiedTextRunStyle.Bold | ClassifiedTextRunStyle.Italic);

                rows.Add(new ContainerElement(
                    ContainerElementStyle.Wrapped,
                    new ImageElement(new ImageId(KnownMonikers.StyleSheet.Guid, KnownMonikers.StyleSheet.Id)),
                    new ClassifiedTextElement(locationRun)));

                rows.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.MarkupAttribute, $"    {varName}"),
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, ": "),
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, def.Value, ClassifiedTextRunStyle.Bold)));
            }

            if (hiddenDefs > 0)
            {
                rows.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, $"    … (+{hiddenDefs} weitere)")));
            }

            // Footer: link to .csstools.json
            string? configPath = CssToolsConfig.Instance.ConfigFilePath;
            if (configPath != null)
            {
                // Empty spacer line
                rows.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, " ")));

                rows.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(
                        PredefinedClassificationTypeNames.Comment,
                        "⚙ .csstools.json",
                        () => ThreadHelper.JoinableTaskFactory.Run(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            NavigationHelper.OpenFile(configPath);
                        }),
                        tooltip: configPath)));
            }

            var container = new ContainerElement(ContainerElementStyle.Stacked, rows);
            var item = new QuickInfoItem(span, container);
            return Task.FromResult<QuickInfoItem?>(item);
        }

        private static string? FindVarName(
            string lineText,
            int posOnLine,
            Regex regex,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;

            foreach (Match m in regex.Matches(lineText))
            {
                if (posOnLine < m.Index || posOnLine > m.Index + m.Length)
                    continue;

                string name = m.Groups.Count > 1 && m.Groups[1].Success
                    ? m.Groups[1].Value
                    : m.Value;

                int spanStart = line.Start.Position + m.Index;
                trackingSpan = snapshot.CreateTrackingSpan(
                    new Span(spanStart, m.Length),
                    SpanTrackingMode.EdgeInclusive);

                return name;
            }

            return null;
        }

        public void Dispose() => _disposed = true;
    }
}
