using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace CssTools
{
    /// <summary>
    /// MEF-Provider, der <see cref="CssVariableCompletionSource"/> für CSS-Buffer registriert.
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("CSS Variable Completion Provider")]
    [Order(Before = "default")]
    [ContentType("CSS")]
    [ContentType("text/x-css")]
    [ContentType("CSharp")]
    [ContentType("Razor")]
    [ContentType("RazorCSharp")]
    [ContentType("RazorCoreCSharp")]
    [ContentType("HTML")]
    [ContentType("htmlx")]
    [ContentType("HTMLX")]
    internal sealed class CssVariableCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import]
        internal ICompletionBroker CompletionBroker { get; set; } = null!;

        public IAsyncCompletionSource? GetOrCreate(ITextView textView)
            => textView.Properties.GetOrCreateSingletonProperty(
                typeof(CssVariableCompletionSource),
                () => new CssVariableCompletionSource(textView, CompletionBroker));
    }
}
