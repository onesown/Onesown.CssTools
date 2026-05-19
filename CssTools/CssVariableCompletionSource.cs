using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;

namespace CssTools
{
    /// <summary>
    /// Liefert CSS-Variablen-Vorschläge (aus dem <see cref="CssVariableStore"/>) sobald
    /// der User <c>--</c> in einem CSS-Buffer tippt.
    /// </summary>
    internal sealed class CssVariableCompletionSource : IAsyncCompletionSource
    {
        private readonly ITextView _textView;
        private readonly ICompletionBroker _completionBroker;

        // Icon-Tag, der im Tooltip den Variablenwert zeigt (ClassificationTag reicht hier)
        private static readonly ImageElement _varIcon =
            new ImageElement(new Microsoft.VisualStudio.Core.Imaging.ImageId(
                new System.Guid("ae27a6b0-e345-4288-96df-5eaf394ee369"), // KnownMonikers catalog GUID
                3196)); // Property icon

        public CssVariableCompletionSource(ITextView textView, ICompletionBroker completionBroker)
        {
            _textView = textView;
            _completionBroker = completionBroker;
        }

        /// <inheritdoc/>
        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            // Completion nur triggern wenn der Store bereits initialisiert ist
            if (!CssToolsConfig.Instance.IsInitialized)
                return CompletionStartData.DoesNotParticipateInCompletion;

            // Applicable span: rückwärts bis zum Beginn des aktuellen --identifier-Tokens
            var snapshot = triggerLocation.Snapshot;
            int pos = triggerLocation.Position;

            // Suche nach dem Beginn des --xxx Tokens (inkl. der beiden Bindestriche)
            int start = pos;
            while (start > 0 && IsVarNameChar(snapshot[start - 1]))
                start--;

            // Nur teilnehmen wenn das Token mit -- beginnt (erstes oder zweites Minus bereits getippt)
            string typed = snapshot.GetText(start, pos - start);
            if (!typed.StartsWith("--"))
                return CompletionStartData.DoesNotParticipateInCompletion;

            var applicableSpan = new SnapshotSpan(snapshot, new Span(start, pos - start));

            // Synchron auf dem UI-Thread – Legacy-Sessions (CSS-Language-Service) jetzt dismissieren,
            // bevor die async-Session geöffnet wird.
            DoDismiss();

            return new CompletionStartData(CompletionParticipation.ExclusivelyProvidesItems, applicableSpan);
        }

        /// <inheritdoc/>
        public Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            var names = CssVariableStore.Instance.GetAllVariableNames();
            var items = new List<CompletionItem>();

            foreach (string name in names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase))
            {
                // Hole den ersten Wert für den Insert-Filter-Text
                var defs = CssVariableStore.Instance.GetDefinitions(name);
                string firstValue = defs.Count > 0 ? defs[0].Value : string.Empty;

                var item = new CompletionItem(
                    displayText: name,
                    source: this,
                    icon: _varIcon,
                    filters: ImmutableArray<CompletionFilter>.Empty,
                    suffix: string.IsNullOrEmpty(firstValue) ? string.Empty : $"  {firstValue}",
                    insertText: $"var({name})",
                    sortText: name,
                    filterText: name,
                    automationText: name,
                    attributeIcons: ImmutableArray<ImageElement>.Empty);

                items.Add(item);
            }

            var context = new CompletionContext(items.ToImmutableArray());
            return Task.FromResult(context);
        }

        /// <inheritdoc/>
        public Task<object?> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            var defs = CssVariableStore.Instance.GetDefinitions(item.DisplayText);
            if (defs.Count == 0)
                return Task.FromResult<object?>(null);

            // Baue denselben ContainerElement-Stil wie der QuickInfo-Tooltip
            var lines = new List<object>();

            // Header: Variablenname
            lines.Add(new ClassifiedTextElement(
                new ClassifiedTextRun("keyword", item.DisplayText)));

            // Gruppen nach Projektname
            foreach (var group in defs.GroupBy(d => d.ProjectName).OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("string", group.Key)));

                foreach (var def in group.OrderBy(d => Path.GetFileName(d.FilePath), System.StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun("text", $"  {def.Value}   "),
                        new ClassifiedTextRun("comment", $"{Path.GetFileName(def.FilePath)}:{def.LineNumber}")));
                }
            }

            return Task.FromResult<object?>(new ContainerElement(ContainerElementStyle.Stacked, lines));
        }

        private static bool IsVarNameChar(char c) => c == '-' || char.IsLetterOrDigit(c) || c == '_';

        private void DoDismiss()
        {
            var sessions = _completionBroker.GetSessions(_textView);
            foreach (var s in sessions)
            {
                if (!s.IsDismissed)
                    s.Dismiss();
            }
        }
    }
}
