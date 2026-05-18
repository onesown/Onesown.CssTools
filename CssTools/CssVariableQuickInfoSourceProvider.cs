using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CssTools
{
    /// <summary>
    /// MEF provider that wires <see cref="CssVariableQuickInfoSource"/> into the VS editor
    /// for all CSS content buffers. Also triggers a re-scan whenever the buffer text changes.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("CSS Variable QuickInfo Provider")]
    [ContentType("CSS")]
    [ContentType("text/x-css")]   // some VS versions register under this name
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
            // Retrieve the file path associated with this buffer (may be null for unsaved buffers).
            if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return;

            string filePath = doc.FilePath ?? string.Empty;
            string content = buffer.CurrentSnapshot.GetText();
            CssVariableStore.Instance.ScanText(filePath, content);
        }
    }
}
