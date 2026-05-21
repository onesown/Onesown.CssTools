using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Provides QuickInfo (hover tooltip) content for CSS class names used in
    /// HTML, Razor and C# buffers. Shows every definition (file + line), each clickable.
    /// </summary>
    internal sealed class CssClassQuickInfoSource : IAsyncQuickInfoSource
    {
        // Matches a CSS identifier token (word under cursor).
        private static readonly Regex ClassTokenRegex = new Regex(
            @"[\w-]+",
            RegexOptions.Compiled);

        // Matches  class=  with optional whitespace (no quote yet – we scan the value manually).
        private static readonly Regex ClassAttrStartRegex = new Regex(
            @"\bclass\s*=\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches HTML/XML tag names in opening <tagname or closing </tagname
        private static readonly Regex HtmlTagRegex = new Regex(
            @"</?([a-zA-Z][a-zA-Z0-9-]*)",
            RegexOptions.Compiled);

        private readonly ITextBuffer _buffer;
        private readonly bool _isCssBuffer;
        private bool _disposed;

        public CssClassQuickInfoSource(ITextBuffer buffer, bool isCssBuffer)
        {
            _buffer      = buffer;
            _isCssBuffer = isCssBuffer;
        }

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
            int posOnLine   = triggerPoint.Value.Position - line.Start.Position;

            ITrackingSpan? span = null;
            string? className;
            bool isTag = false;

            if (_isCssBuffer)
            {
                // Only show tooltip when cursor is on a selector (outside declaration blocks).
                if (IsInsideDeclarationBlock(_buffer.CurrentSnapshot, triggerPoint.Value.Position))
                    return Task.FromResult<QuickInfoItem?>(null);

                className = FindTokenUnderCursor(lineText, posOnLine, out span, line, _buffer.CurrentSnapshot);
                // Token is not a known class → check tag selectors
                if (className != null && CssClassStore.Instance.GetDefinitions(className).Count == 0)
                {
                    if (CssClassStore.Instance.GetTagDefinitions(className).Count > 0)
                        isTag = true;
                    else
                        className = null;
                }
            }
            else
            {
                className = FindClassNameInAttribute(lineText, posOnLine, out span, line, _buffer.CurrentSnapshot);
                if (className == null)
                    className = FindClassNameInStringLiteral(lineText, posOnLine, out span, line, _buffer.CurrentSnapshot);
                // Fallback: cursor on an HTML tag name
                if (className == null)
                {
                    string? tagName = FindTagNameInHtml(lineText, posOnLine, out span, line, _buffer.CurrentSnapshot);
                    if (tagName != null && CssClassStore.Instance.GetTagDefinitions(tagName).Count > 0)
                    {
                        className = tagName;
                        isTag = true;
                    }
                }
            }

            if (className == null || span == null)
                return Task.FromResult<QuickInfoItem?>(null);

            IReadOnlyList<CssClassDefinition> defs = isTag
                ? CssClassStore.Instance.GetTagDefinitions(className)
                : CssClassStore.Instance.GetDefinitions(className);
            if (defs.Count == 0)
                return Task.FromResult<QuickInfoItem?>(null);

            // Determine the active file path so its definitions appear first.
            string? activeFilePath = null;
            if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument activeDoc))
                activeFilePath = activeDoc.FilePath;

            // Re-sort: entries from the active file first, then original order.
            var sortedDefs = new List<CssClassDefinition>(defs);
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

            // Build tooltip: header + project groups with file:line links.
            var rows = new List<object>();

            string headerText = isTag ? className : $".{className}";

            // Gesamtzeilenbudget für alle CSS-Blöcke über alle Definitionen hinweg
            const int totalLineBudget  = 8;  // kept as fallback floor
            int effectiveBudget = Math.Max(totalLineBudget, CssToolsConfig.Instance.MaxCssLines);
            const int MaxDefinitions   = 5;   // kept as fallback
            int remainingLines = CssToolsConfig.Instance.MaxCssLines > 0 ? CssToolsConfig.Instance.MaxCssLines : totalLineBudget;
            int totalDefs      = sortedDefs.Count;
            int shownDefs      = Math.Min(totalDefs, CssToolsConfig.Instance.MaxDefinitions > 0 ? CssToolsConfig.Instance.MaxDefinitions : MaxDefinitions);
            int hiddenDefs     = totalDefs - shownDefs;

            string? currentProject = null;
            bool firstProject = true;
            for (int di = 0; di < shownDefs; di++)
            {
                CssClassDefinition def = sortedDefs[di];
                if (!StringComparer.OrdinalIgnoreCase.Equals(def.ProjectName, currentProject))
                {
                    currentProject = def.ProjectName;
                    string label = string.IsNullOrEmpty(currentProject) ? "(unknown project)" : currentProject;
                    if (!firstProject)
                        rows.Add(new ClassifiedTextElement(
                            new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, " ")));
                    firstProject = false;
                    rows.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun(PredefinedClassificationTypeNames.PreprocessorKeyword, label)));
                }

                string fileName         = Path.GetFileName(def.FilePath);
                string capturedFilePath = def.FilePath;
                int    capturedLine     = def.LineNumber;

                // Lese CSS-Block; schneide auf verbleibendes Gesamtbudget
                string[] cssLines = ReadClassBodyLines(capturedFilePath, capturedLine);
                string? overflowTooltip = null;

                if (cssLines.Length > remainingLines)
                {
                    var inlineLines = new List<string>(cssLines.Take(remainingLines));
                    inlineLines.Add("… (+" + (cssLines.Length - remainingLines) + " weitere)");
                    overflowTooltip = string.Join(Environment.NewLine, cssLines);
                    cssLines = inlineLines.ToArray();
                }

                remainingLines -= cssLines.Length; // Budget verbrauchen

                // File-Link: schwarz
                var locationRun = new ClassifiedTextRun(
                    PredefinedClassificationTypeNames.SymbolDefinition,
                    $"{fileName}:{def.LineNumber}",
                    () => ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        NavigationHelper.NavigateTo(capturedFilePath, capturedLine);
                    }),
                    tooltip: overflowTooltip ?? capturedFilePath,
                    style: ClassifiedTextRunStyle.Bold | ClassifiedTextRunStyle.Italic);

                rows.Add(new ContainerElement(
                    ContainerElementStyle.Wrapped,
                    new ImageElement(new ImageId(KnownMonikers.StyleSheet.Guid, KnownMonikers.StyleSheet.Id)),
                    new ClassifiedTextElement(locationRun)));

                // Render Inline-CSS-Block (erste Zeilen, eingerückt)
                foreach (string cssLine in cssLines)
                    rows.Add(BuildColoredCssLine(cssLine, indented: true));

                // Wenn Budget aufgebraucht, restliche Definitionen nur noch als Link ohne Block
                if (remainingLines <= 0)
                    remainingLines = 0;
            }

            if (hiddenDefs > 0)
            {
                rows.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, $"    … (+{hiddenDefs} weitere)")));
            }

            var container = new ContainerElement(ContainerElementStyle.Stacked, rows);
            var item = new QuickInfoItem(span, container);
            return Task.FromResult<QuickInfoItem?>(item);
        }

        /// <summary>
        /// For CSS buffers: returns the identifier token directly under the cursor.
        /// </summary>
        private static string? FindTokenUnderCursor(
            string lineText,
            int posOnLine,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;

            foreach (Match m in ClassTokenRegex.Matches(lineText))
            {
                if (posOnLine < m.Index || posOnLine > m.Index + m.Length)
                    continue;

                trackingSpan = snapshot.CreateTrackingSpan(
                    new Span(line.Start.Position + m.Index, m.Length),
                    SpanTrackingMode.EdgeInclusive);

                return m.Value;
            }

            return null;
        }

        /// <summary>
        /// For HTML/Razor/C# buffers: only returns a class name if the cursor is inside
        /// a <c>class="..."</c> attribute value on this line – either in a static segment
        /// or inside a string literal within a Razor <c>@(...)</c> expression.
        /// </summary>
        private static string? FindClassNameInAttribute(
            string lineText,
            int posOnLine,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;
            int len = lineText.Length;

            foreach (Match attrMatch in ClassAttrStartRegex.Matches(lineText))
            {
                int i = attrMatch.Index + attrMatch.Length;
                if (i >= len) continue;

                // Opening quote.
                char quote = lineText[i];
                if (quote != '"' && quote != '\'') continue;
                int valueStart = i + 1;
                i = valueStart;

                // Walk until the closing quote.
                // Collect static segments AND Razor-expression ranges.
                var staticSegments = new List<(int Start, int Length)>();
                var razorRanges    = new List<(int Start, int Length)>();
                int segStart = i;

                while (i < len)
                {
                    char c = lineText[i];

                    if (c == quote)
                    {
                        if (i > segStart)
                            staticSegments.Add((segStart, i - segStart));
                        int valueEnd = i;

                        if (posOnLine < valueStart || posOnLine >= valueEnd)
                            break;

                        // 1. Check static segments first.
                        foreach (var (sStart, sLen) in staticSegments)
                        {
                            if (posOnLine < sStart || posOnLine >= sStart + sLen)
                                continue;

                            string seg = lineText.Substring(sStart, sLen);
                            int offsetInSeg = posOnLine - sStart;

                            foreach (Match tok in ClassTokenRegex.Matches(seg))
                            {
                                if (offsetInSeg < tok.Index || offsetInSeg > tok.Index + tok.Length)
                                    continue;

                                int spanStart2 = line.Start.Position + sStart + tok.Index;
                                trackingSpan = snapshot.CreateTrackingSpan(
                                    new Span(spanStart2, tok.Length),
                                    SpanTrackingMode.EdgeInclusive);
                                return tok.Value;
                            }
                        }

                        // 2. Check Razor expression ranges – look for string literals containing CSS classes.
                        foreach (var (rStart, rLen) in razorRanges)
                        {
                            if (posOnLine < rStart || posOnLine >= rStart + rLen)
                                continue;

                            // Scan string literals within this Razor expression range.
                            string result = FindClassInRazorExpression(
                                lineText, rStart, rLen, posOnLine, out trackingSpan, line, snapshot);
                            if (result != null)
                                return result;
                        }
                        break;
                    }

                    if (c == '@')
                    {
                        if (i > segStart)
                            staticSegments.Add((segStart, i - segStart));

                        int razorStart = i;
                        i++; // skip '@'

                        if (i < len && lineText[i] == '(')
                        {
                            i = SkipBalancedParens(lineText, i);
                        }
                        else
                        {
                            while (i < len && (char.IsLetterOrDigit(lineText[i]) || lineText[i] == '_' || lineText[i] == '.'))
                                i++;
                        }

                        razorRanges.Add((razorStart, i - razorStart));
                        segStart = i;
                        continue;
                    }

                    i++;
                }
            }

            return null;
        }

        /// <summary>
        /// Within a Razor expression range, finds string literals and checks if the cursor
        /// is on a CSS class name token inside one of them.
        /// </summary>
        private static string? FindClassInRazorExpression(
            string lineText,
            int rangeStart,
            int rangeLength,
            int posOnLine,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;
            int rangeEnd = rangeStart + rangeLength;
            int i = rangeStart;

            while (i < rangeEnd)
            {
                if (lineText[i] == '"')
                {
                    int litStart = i + 1;
                    i = SkipStringLiteral(lineText, i, '"');
                    int litEnd = i - 1; // closing quote index

                    if (litEnd <= litStart) continue;
                    if (posOnLine < litStart || posOnLine >= litEnd) continue;

                    string content = lineText.Substring(litStart, litEnd - litStart);
                    int offsetInContent = posOnLine - litStart;

                    foreach (Match tok in ClassTokenRegex.Matches(content))
                    {
                        if (offsetInContent < tok.Index || offsetInContent > tok.Index + tok.Length)
                            continue;

                        if (CssClassStore.Instance.GetDefinitions(tok.Value).Count == 0)
                            return null;

                        int spanStart = line.Start.Position + litStart + tok.Index;
                        trackingSpan = snapshot.CreateTrackingSpan(
                            new Span(spanStart, tok.Length),
                            SpanTrackingMode.EdgeInclusive);
                        return tok.Value;
                    }
                }
                else
                {
                    i++;
                }
            }

            return null;
        }

        /// <summary>
        /// Fallback for Razor C#-projection buffers: if the cursor sits directly on a token
        /// inside a string literal (e.g. <c>"float-label"</c> inside an @(...) expression),
        /// return that token so we can still show the CSS tooltip.
        /// Only fires when the token is actually a known CSS class.
        /// </summary>
        private static string? FindClassNameInStringLiteral(
            string lineText,
            int posOnLine,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;
            int len = lineText.Length;

            // Walk every double-quoted string literal on the line.
            int i = 0;
            while (i < len)
            {
                if (lineText[i] != '"') { i++; continue; }

                int literalStart = i + 1;
                i = SkipStringLiteral(lineText, i, '"');
                int literalEnd = i - 1; // index of closing quote

                // literalEnd can be <= literalStart for empty strings ""
                if (literalEnd <= literalStart)
                    continue;

                if (posOnLine < literalStart || posOnLine >= literalEnd)
                    continue;

                // Cursor is inside this string literal – find the token under the cursor.
                string content = lineText.Substring(literalStart, literalEnd - literalStart);
                int offsetInContent = posOnLine - literalStart;

                foreach (Match tok in ClassTokenRegex.Matches(content))
                {
                    if (offsetInContent < tok.Index || offsetInContent > tok.Index + tok.Length)
                        continue;

                    string candidate = tok.Value;

                    // Only return something if the Store actually knows this class.
                    if (CssClassStore.Instance.GetDefinitions(candidate).Count == 0)
                        return null;

                    int spanStart = line.Start.Position + literalStart + tok.Index;
                    trackingSpan = snapshot.CreateTrackingSpan(
                        new Span(spanStart, tok.Length),
                        SpanTrackingMode.EdgeInclusive);
                    return candidate;
                }

                // Don't break – continue checking other literals on this line.
            }

            return null;
        }

        /// <summary>
        /// For HTML/Razor buffers: if the cursor is on a tag name (e.g. &lt;h3&gt; or &lt;/h3&gt;),
        /// returns that tag name. Ignores component names (PascalCase starting with uppercase).
        /// </summary>
        private static string? FindTagNameInHtml(
            string lineText,
            int posOnLine,
            out ITrackingSpan? trackingSpan,
            ITextSnapshotLine line,
            ITextSnapshot snapshot)
        {
            trackingSpan = null;
            foreach (Match m in HtmlTagRegex.Matches(lineText))
            {
                // Group 0 is the full match (<h3 or </h3), group 1 is the tag name
                int nameStart = m.Index + m.Length - m.Groups[1].Length;
                int nameLen   = m.Groups[1].Length;
                if (posOnLine < nameStart || posOnLine > nameStart + nameLen)
                    continue;

                string tagName = m.Groups[1].Value;
                // Skip Razor/Blazor components (PascalCase) and XML namespaces
                if (char.IsUpper(tagName[0]))
                    continue;

                trackingSpan = snapshot.CreateTrackingSpan(
                    new Span(line.Start.Position + nameStart, nameLen),
                    SpanTrackingMode.EdgeInclusive);
                return tagName;
            }
            return null;
        }

        /// <summary>
        /// Starting at an opening '(' at <paramref name="pos"/>, returns the index
        /// just past the matching ')'.  Handles nested parens and string literals.
        /// </summary>
        private static int SkipBalancedParens(string text, int pos)
        {
            int depth = 0;
            int len   = text.Length;
            while (pos < len)
            {
                char c = text[pos];
                if (c == '(') { depth++; pos++; }
                else if (c == ')') { depth--; pos++; if (depth == 0) break; }
                else if (c == '"' || c == '\'') { pos = SkipStringLiteral(text, pos, c); }
                else pos++;
            }
            return pos;
        }

        /// <summary>Skips a C#/JS string literal starting at <paramref name="pos"/> (the opening quote).</summary>
        private static int SkipStringLiteral(string text, int pos, char quote)
        {
            pos++; // skip opening quote
            int len = text.Length;
            while (pos < len)
            {
                char c = text[pos++];
                if (c == '\\') { pos++; continue; } // escape
                if (c == quote) break;
            }
            return pos;
        }

        /// <summary>
        /// Reads the CSS rule block at <paramref name="lineNumber"/> and returns it as individual lines.
        /// Falls back to an empty array on error.
        /// </summary>
        private static string[] ReadClassBodyLines(string filePath, int lineNumber)
        {
            try
            {
                string[] lines = File.ReadAllLines(filePath);

                int startIndex = Math.Max(0, lineNumber - 1);

                // If the starting line itself is minified (very long), skip body rendering.
                if (startIndex < lines.Length && lines[startIndex].Length > 500)
                    return Array.Empty<string>();

                var body = new List<string>();
                int depth = 0;
                bool started = false;

                for (int i = startIndex; i < lines.Length; i++)
                {
                    string l = lines[i];

                    // If any line inside the block is minified, abort.
                    if (started && l.Length > 500)
                        return Array.Empty<string>();

                    // Count braces to track when the block actually opens and closes.
                    foreach (char ch in l)
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }

                    // Only add lines once the opening '{' has been seen.
                    if (started)
                        body.Add(l);

                    if (started && depth <= 0)
                        break;
                }

                return body.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // CSS property Regex:  "  property-name: value;"
        private static readonly Regex CssPropRegex = new Regex(
            @"^(\s*)([\w-]+)(\s*:\s*)(.+?)(\s*;?\s*)$",
            RegexOptions.Compiled);

        /// <summary>
        /// Renders one CSS line as a colored <see cref="ClassifiedTextElement"/>:
        /// selector lines → keyword (cyan/blue), property name → identifier (light blue),
        /// colon → plain, value → string (orange/yellow), comment → comment (green).
        /// Inline-only rendering; Sub-Tooltip wird über overflowTooltip beim File-Link angezeigt.
        /// </summary>
        private static ClassifiedTextElement BuildColoredCssLine(string line, bool indented = false, string? tooltip = null)
        {
            string trimmed = line.TrimEnd();
            string displayText = indented ? "    " + trimmed : trimmed;

            // Comment line → green
            if (trimmed.TrimStart().StartsWith("/*"))
            {
                return new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, displayText));
            }

            // Closing brace → dark red
            if (trimmed.TrimStart() == "}")
            {
                return new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.MarkupAttributeValue, displayText));
            }

            // Selector line (contains '{') → red bold italic
            if (trimmed.Contains("{"))
            {
                return new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.String, displayText, ClassifiedTextRunStyle.Bold));
            }

            // "… (+X weitere)" Hinweis
            if (trimmed.StartsWith("…"))
            {
                return new ClassifiedTextElement(
                    new ClassifiedTextRun(PredefinedClassificationTypeNames.PreprocessorKeyword, displayText));
            }

            // Property line: name → light red, colon → plain, value → blue, semicolon → plain
            Match m = CssPropRegex.Match(trimmed);
            if (m.Success)
            {
                string indent   = m.Groups[1].Value;
                string propName = m.Groups[2].Value;
                string colon    = m.Groups[3].Value;
                string value    = m.Groups[4].Value;
                string semi     = m.Groups[5].Value;

                int commentIdx = value.IndexOf("/*", StringComparison.Ordinal);
                string mainValue     = commentIdx >= 0 ? value.Substring(0, commentIdx) : value;
                string inlineComment = commentIdx >= 0 ? value.Substring(commentIdx) : string.Empty;

                var runs = new List<ClassifiedTextRun>();

                string displayIndent = indented ? "      " : (indent.Length > 0 ? indent : "");
                if (displayIndent.Length > 0)
                    runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, displayIndent));

                // property name → light red
                runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.MarkupAttribute, propName));
                runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, colon));
                // value → blue
                runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, mainValue));

                if (semi.Length > 0)
                    runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, semi));
                if (inlineComment.Length > 0)
                    runs.Add(new ClassifiedTextRun(PredefinedClassificationTypeNames.Comment, " " + inlineComment.Trim()));

                return new ClassifiedTextElement(runs);
            }

            // Fallback: plain text
            return new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Other, displayText));
        }

        /// <summary>
        /// Determines whether the given position in the snapshot is inside a CSS declaration block
        /// (between { and }) by counting unbalanced braces from the start of the document.
        /// Ignores braces inside comments.
        /// </summary>
        private static bool IsInsideDeclarationBlock(ITextSnapshot snapshot, int position)
        {
            string text = snapshot.GetText(0, position);
            int depth = 0;
            bool inComment = false;

            for (int i = 0; i < text.Length; i++)
            {
                if (inComment)
                {
                    if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                    {
                        inComment = false;
                        i++;
                    }
                    continue;
                }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    inComment = true;
                    i++;
                    continue;
                }
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
            }

            return depth > 0;
        }

        public void Dispose() => _disposed = true;
    }
}

