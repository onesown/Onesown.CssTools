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
    /// Also triggers a re-scan of <see cref="CssClassStore"/> whenever the buffer text changes.
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
            textBuffer.Changed += (_, e) => ScanBuffer(textBuffer);

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

            string projectName = CssClassStore.Instance.GetProjectName(filePath);
            string content     = buffer.CurrentSnapshot.GetText();
            CssClassStore.Instance.ScanText(filePath, projectName, content);
        }
    }
}
