using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
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

            // Build a ContainerElement: header + one project group each containing its definitions.
            var rows = new List<object>();

            // Header: variable name (bold/keyword style)
            rows.Add(new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, varName)));

            // Group definitions by project, preserving the sort order from GetDefinitions().
            string? currentProject = null;
            foreach (CssVariableDefinition def in defs)
            {
                // Project header – only when the project changes
                if (!StringComparer.OrdinalIgnoreCase.Equals(def.ProjectName, currentProject))
                {
                    currentProject = def.ProjectName;
                    string label = string.IsNullOrEmpty(currentProject) ? "(unknown project)" : currentProject;

                    // Visually raised: two spaces of padding + PreprocessorKeyword gives a distinct colour
                    rows.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(
                            PredefinedClassificationTypeNames.PreprocessorKeyword,
                            $"  {label}")));
                }

                string fileName = Path.GetFileName(def.FilePath);
                string capturedFilePath = def.FilePath;
                int    capturedLine     = def.LineNumber;

                var valueRun = new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.String,
                    $"    {def.Value}");

                var locationRun = new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.Other,
                    $"  ↳ {fileName}:{def.LineNumber}",
                    () => ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        NavigationHelper.NavigateTo(capturedFilePath, capturedLine);
                    }),
                    tooltip: capturedFilePath);

                rows.Add(new ClassifiedTextElement(valueRun, locationRun));
            }

            // Footer: link to .csstools.json – separated by an empty line, left-aligned
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
