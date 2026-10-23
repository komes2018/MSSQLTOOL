using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace MSSQLTool.GridQuickFilter
{
    [Guid("5b7194f8-1123-4e89-a2e6-993c8340d829")]
    public sealed class GridQuickFilterWindow : ToolWindowPane
    {
        public GridQuickFilterWindow() : base(null)
        {
            Caption = LocalizationManager.T("Grid Quick Filter");
            Content = new GridQuickFilterWindowControl();
        }

        protected override void OnClose()
        {
            (Content as GridQuickFilterWindowControl)?.Dispose();
            base.OnClose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) (Content as GridQuickFilterWindowControl)?.Dispose();
            base.Dispose(disposing);
        }
    }
}
