using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CssTools
{
    /// <summary>
    /// MEF provider that wires <see cref="CssVariableQuickInfoSource"/> into the VS editor
    /// for CSS, C#, Razor/Blazor and HTML content buffers.
    /// Re-scans CSS buffers when the file is saved (FileActionOccurred), not on every keystroke.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("CSS Variable QuickInfo Provider")]
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
    internal sealed class CssVariableQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource? TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            // Perform an initial scan of the buffer content.
            ScanBuffer(textBuffer);

            // Re-scan only when the file is actually saved – not on every keystroke.
            // The FileSystemWatcher in CssToolsPackage handles on-disk changes from external tools.
            if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                doc.FileActionOccurred += (_, e) =>
                {
                    if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
                        ScanBuffer(textBuffer);
                };

            return new CssVariableQuickInfoSource(textBuffer);
        }

        private static void ScanBuffer(ITextBuffer buffer)
        {
            // Skip re-scan until the package has initialised the config (solution dir known).
            if (!CssToolsConfig.Instance.IsInitialized) return;

            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return;

            string filePath = doc.FilePath ?? string.Empty;

            // Only scan files that belong to a known project directory (whitelist).
            // This implicitly blocks all temp files regardless of their naming scheme.
            string projectName = CssVariableStore.Instance.GetProjectName(filePath);
            if (string.IsNullOrEmpty(projectName))
                projectName = CssProjectResolver.Resolve(filePath) ?? string.Empty;
            if (string.IsNullOrEmpty(projectName)) return;

            string content = buffer.CurrentSnapshot.GetText();
            CssVariableStore.Instance.ScanText(filePath, projectName, content);
        }
    }
}
