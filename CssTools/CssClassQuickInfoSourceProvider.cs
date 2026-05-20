using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CssTools
{
    /// <summary>
    /// MEF provider that wires <see cref="CssClassQuickInfoSource"/> into the VS editor
    /// for CSS, C#, Razor/Blazor and HTML content buffers.
    /// Re-scans CSS buffers when the file is saved (FileActionOccurred), not on every keystroke.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("CSS Class QuickInfo Provider")]
    [ContentType("CSS")]
    [ContentType("text/x-css")]
    [ContentType("CSharp")]
    [ContentType("Razor")]
    [ContentType("RazorCSharp")]
    [ContentType("RazorCoreCSharp")]
    [ContentType("HTML")]
    [ContentType("htmlx")]
    [ContentType("HTMLX")]
    [Order(Before = "default")]
    internal sealed class CssClassQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource? TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            ScanBuffer(textBuffer);

            // Re-scan only when the file is actually saved – not on every keystroke.
            if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                doc.FileActionOccurred += (_, e) =>
                {
                    if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
                        ScanBuffer(textBuffer);
                };

            bool isCss = textBuffer.ContentType.IsOfType("CSS")
                      || textBuffer.ContentType.IsOfType("text/x-css");
            return new CssClassQuickInfoSource(textBuffer, isCss);
        }

        private static void ScanBuffer(ITextBuffer buffer)
        {
            if (!CssToolsConfig.Instance.IsInitialized) return;

            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return;

            string filePath = doc.FilePath ?? string.Empty;
            if (!filePath.EndsWith(".css", System.StringComparison.OrdinalIgnoreCase))
                return; // Only scan CSS buffers; non-CSS files don't define classes.

            // Only scan files that belong to a known project directory (whitelist).
            // This implicitly blocks all temp files regardless of their naming scheme.
            string projectName = CssClassStore.Instance.GetProjectName(filePath);
            if (string.IsNullOrEmpty(projectName))
                projectName = CssProjectResolver.Resolve(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(projectName)) return;

            string content = buffer.CurrentSnapshot.GetText();
            CssClassStore.Instance.ScanText(filePath, projectName, content);
        }
    }
}
