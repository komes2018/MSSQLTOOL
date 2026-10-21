using System;
using System.Linq;
using System.Windows;

namespace MSSQLTool.QuerySafety
{
    public partial class FatalActionWarningDialog : Microsoft.VisualStudio.PlatformUI.DialogWindow
    {
        private readonly ToolWindowThemeController themeController;

        internal bool AllowRepeats { get; private set; }

        internal FatalActionWarningDialog(FatalActionAnalysis analysis, string context, bool canRemember)
        {
            InitializeComponent();
            themeController = new ToolWindowThemeController(this, () => ToolWindowThemeResources.ApplySharedTheme(this));
            LocalizationManager.Apply(this);

            ContextText.Text = context;
            ActionText.Text = string.Join(Environment.NewLine, analysis.Actions.Select(a =>
                string.Format(LocalizationManager.T("Line {0}: {1} - {2}"), a.Line, a.DisplayOperation, a.DisplayTarget)));

            if (analysis.Limitation != null) ActionText.Text += Environment.NewLine + analysis.Limitation;

            RepeatButton.IsEnabled = canRemember;
            RepeatButton.IsDefault = canRemember;
            CancelButton.IsDefault = !canRemember;

            RepeatText.Text = canRemember
                ? LocalizationManager.T("Allow repeats applies only to this exact SQL in this query window and connection. Changed SQL, a different database, reconnecting or closing the window requires approval again.")
                : LocalizationManager.T("Repeat approval is unavailable until the query can be fully checked and the active connection session can be identified. You can still run once.");

            Loaded += (sender, args) =>
            {
                LocalizationManager.Apply(this);
                if (canRemember) RepeatButton.Focus();
                else CancelButton.Focus();
            };
        }

        private void RunOnce_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
        private void AllowRepeats_Click(object sender, RoutedEventArgs e)
        {
            if (!RepeatButton.IsEnabled) return;
            AllowRepeats = true;
            DialogResult = true;
        }
    }
}
