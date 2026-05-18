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
    /// Also triggers a re-scan whenever the buffer text changes.
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

            // Re-scan on every text change so variable values stay up-to-date.
            textBuffer.Changed += (_, e) => ScanBuffer(textBuffer);

            return new CssVariableQuickInfoSource(textBuffer);
        }

        private static void ScanBuffer(ITextBuffer buffer)
        {
            // Skip re-scan until the package has initialised the config (solution dir known).
            if (!CssToolsConfig.Instance.IsInitialized) return;

            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return;

            string filePath = doc.FilePath ?? string.Empty;

            // Preserve the project name that the startup scan already resolved;
            // the MEF provider has no project context so we never overwrite with empty.
            string projectName = CssVariableStore.Instance.GetProjectName(filePath);

            string content = buffer.CurrentSnapshot.GetText();
            CssVariableStore.Instance.ScanText(filePath, projectName, content);
        }
    }
}
