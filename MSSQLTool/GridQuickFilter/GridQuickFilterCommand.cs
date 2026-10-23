using EnvDTE;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections;
using System.ComponentModel.Design;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace MSSQLTool.GridQuickFilter
{
    internal sealed class GridQuickFilterCommand
    {
        public const int CommandId = 4169;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");
        private static MSSQLToolPackage owner;

        public static async Task InitializeAsync(MSSQLToolPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            owner = package;
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
                ?? throw new InvalidOperationException("The menu command service is unavailable.");
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static void OpenQuickFilterForGrid(IGridControl grid = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var resultsControl = GridAccess.GetSQLResultsControl();
                if (grid == null && resultsControl != null)
                {
                    grid = FindSourceGrid(resultsControl);
                }

                if (grid == null || grid.GridStorage == null || grid.GridStorage.NumRows() == 0)
                {
                    MessageBox.Show(LocalizationManager.T("The active query window has no populated result grid. Run a query using Results to Grid, then try again."),
                        LocalizationManager.T("Grid Quick Filter"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string caption = GetSourceCaption(resultsControl);
                var window = owner.FindToolWindow(typeof(GridQuickFilterWindow), owner.GetNextToolWindowId(), true);
                if (!(window?.Frame is IVsWindowFrame frame))
                    throw new InvalidOperationException("Cannot create the Grid Quick Filter window.");

                window.Caption = caption;
                ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild));
                ErrorHandler.ThrowOnFailure(frame.Show());

                var view = (GridQuickFilterWindowControl)window.Content;
                owner.JoinableTaskFactory.RunAsync(() => view.LoadGridAsync(grid))
                    .FileAndForget("MSSQLTool/GridQuickFilter/Load");
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not open Grid Quick Filter");
                MessageBox.Show(LocalizationManager.T("Could not read results from the active query window. Wait for the query to finish and try again. If the problem continues, see the MSSQLTool log."),
                    LocalizationManager.T("Grid Quick Filter"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string GetSourceCaption(object resultsControl)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                object connection = GridAccess.GetNonPublicField(dte?.ActiveWindow?.Object, "m_connection");
                if (connection == null && resultsControl != null)
                {
                    object executor = GridAccess.GetNonPublicField(resultsControl, "m_sqlExec");
                    connection = GridAccess.GetNonPublicField(executor, "m_conn");
                }
                if (string.Equals(GridAccess.GetProperty(connection, "State")?.ToString(), "Open", StringComparison.Ordinal)
                    && int.TryParse(Convert.ToString(GridAccess.GetProperty(connection, "ServerProcessId"),
                        CultureInfo.InvariantCulture), out int sessionId) && sessionId > 0)
                    return "Quick Filter (" + sessionId.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch { }

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                var match = Regex.Match(dte?.ActiveWindow?.Caption ?? "", @"\(([1-9]\d*)\)\)*\s*\*?\s*$");
                if (match.Success) return "Quick Filter (" + match.Groups[1].Value + ")";
            }
            catch { }

            return "Grid Quick Filter";
        }

        private static IGridControl FindSourceGrid(object resultsControl)
        {
            var page = GridAccess.GetNonPublicField(resultsControl, "m_gridResultsPage");
            var containers = GridAccess.GetNonPublicField(page, "m_gridContainers") as IEnumerable;
            if (containers == null) return null;
            GridControl first = null;
            foreach (var container in containers)
            {
                var grid = GridAccess.GetNonPublicField(container, "m_grid") as GridControl;
                if (grid == null || grid.IsDisposed || grid.GridStorage == null) continue;
                if (grid.ContainsFocus) return grid;
                if (first == null && grid.GridStorage.NumRows() > 0) first = grid;
            }
            return first;
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenQuickFilterForGrid();
        }
    }
}
