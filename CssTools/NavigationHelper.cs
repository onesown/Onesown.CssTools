using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace CssTools
{
    /// <summary>
    /// Opens a file in the VS editor and moves the caret to a specific line.
    /// </summary>
    internal static class NavigationHelper
    {
        /// <summary>
        /// Opens <paramref name="filePath"/> in the editor and places the cursor at
        /// <paramref name="lineNumber"/> (1-based). Must be called on the UI thread.
        /// </summary>
        public static void NavigateTo(string filePath, int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var openDoc = (IVsUIShellOpenDocument?)Package.GetGlobalService(typeof(SVsUIShellOpenDocument));
            if (openDoc == null) return;

            Guid logicalView = VSConstants.LOGVIEWID.TextView_guid;
            openDoc.OpenDocumentViaProject(
                filePath,
                ref logicalView,
                out _,          // pSP
                out _,          // pHier
                out _,          // pItemID
                out IVsWindowFrame? frame);

            frame?.Show();

            // Move the caret to the target line (0-based inside IVsTextView).
            if (frame == null) return;
            frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object? docView);

            IVsTextView? textView = docView as IVsTextView;
            if (textView == null && docView is IVsCodeWindow codeWindow)
                codeWindow.GetPrimaryView(out textView);

            if (textView == null) return;

            int targetLine = Math.Max(0, lineNumber - 1); // convert to 0-based
            textView.SetCaretPos(targetLine, 0);
            textView.EnsureSpanVisible(new TextSpan
            {
                iStartLine = targetLine,
                iEndLine   = targetLine,
                iStartIndex = 0,
                iEndIndex   = 0,
            });
        }
    }
}
