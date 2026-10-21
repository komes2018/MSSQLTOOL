using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace MSSQLTool.JobQuickView
{
    internal sealed class JobQuickViewCommand : IDisposable
    {
        public const int CommandId = 4160;
        public static readonly Guid CommandSet = ScriptSelectedObject.CommandSet;
        private readonly AsyncPackage package;
        private readonly OleMenuCommandService commands;
        private readonly OleMenuCommand command;
        private readonly DispatcherTimer attachTimer;
        private readonly Image settingsIcon;
        private readonly List<JobQuickViewPane> jobTabs = new List<JobQuickViewPane>();
        private int nextTabId;
        private IObjectExplorerService explorer;
        private TreeView tree;
        private ContextMenuStrip menu;
        private ToolStripMenuItem quickViewItem;
        private ToolStripSeparator separator;
        private bool disposed;
        private bool reportedIntegrationError;

        private JobQuickViewCommand(AsyncPackage package, OleMenuCommandService commands)
        {
            this.package = package;
            this.commands = commands;
            settingsIcon = LoadSettingsIcon();
            command = new OleMenuCommand(OpenSelectedJob, new CommandID(CommandSet, CommandId));
            command.BeforeQueryStatus += QueryStatus;
            commands.AddCommand(command);

            // Object Explorer can be created after the package. Stop polling as soon as
            // its tree exists; reattach only if SSMS disposes and recreates that tree.
            attachTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            attachTimer.Tick += AttachTimerTick;
            if (!TryAttach()) attachTimer.Start();
        }

        public static async Task<JobQuickViewCommand> InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commands = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            return commands == null ? null : new JobQuickViewCommand(package, commands);
        }

        private void AttachTimerTick(object sender, EventArgs args)
        {
            if (disposed || package.DisposalToken.IsCancellationRequested || TryAttach()) attachTimer.Stop();
        }

        private bool TryAttach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                explorer = ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
                if (explorer == null) return false;
                var candidate = ObjectExplorerReflection.Read(explorer, "Tree") as TreeView;
                if (candidate == null || candidate.IsDisposed) return false;
                tree = candidate;
                tree.ContextMenuStripChanged += ContextMenuChanged;
                tree.Disposed += TreeDisposed;
                AttachMenu();
                return true;
            }
            catch (Exception ex)
            {
                DetachMenu();
                if (tree != null)
                {
                    tree.ContextMenuStripChanged -= ContextMenuChanged;
                    tree.Disposed -= TreeDisposed;
                    tree = null;
                }
                if (!reportedIntegrationError)
                {
                    MSSQLToolPackage._logger?.Warn("Quick Manage could not attach to Object Explorer ({0}).", ex.GetType().Name);
                    reportedIntegrationError = true;
                }
                return false;
            }
        }

        private void TreeDisposed(object sender, EventArgs args)
        {
            DetachMenu();
            if (tree != null)
            {
                tree.ContextMenuStripChanged -= ContextMenuChanged;
                tree.Disposed -= TreeDisposed;
                tree = null;
            }
            if (!disposed) attachTimer.Start();
        }

        private void ContextMenuChanged(object sender, EventArgs args) { AttachMenu(); }

        private void AttachMenu()
        {
            if (tree == null || tree.IsDisposed) return;
            var current = tree.ContextMenuStrip;
            if (current == null || ReferenceEquals(current, menu)) return;
            DetachMenu();
            menu = current;
            menu.Opening += MenuOpening;
        }

        private void MenuOpening(object sender, CancelEventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var selection = JobQuickViewSelection.Capture(SelectedJob());
                if (quickViewItem == null)
                {
                    quickViewItem = new ToolStripMenuItem(LocalizationManager.T("Quick Manage"), settingsIcon, OpenContextJob);
                    separator = new ToolStripSeparator();
                }
                quickViewItem.Tag = selection;
                int index = InsertionIndex(menu);
                if (!menu.Items.Contains(separator)) menu.Items.Insert(index, separator);
                if (!menu.Items.Contains(quickViewItem)) menu.Items.Insert(index + 1, quickViewItem);
            }
            catch
            {
                if (quickViewItem != null) menu.Items.Remove(quickViewItem);
                if (separator != null) menu.Items.Remove(separator);
            }
        }

        private static int InsertionIndex(ContextMenuStrip strip)
        {
            for (int i = 0; i < strip.Items.Count; i++)
            {
                string text = strip.Items[i].Text?.Replace("&", string.Empty);
                if (string.Equals(text, "Properties", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "属性", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return strip.Items.Count;
        }

        private INodeInformation SelectedJob()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var service = explorer ?? ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
            if (service == null) return null;
            service.GetSelectedNodes(out int count, out INodeInformation[] selected);
            if (count != 1 || selected == null || selected.Length == 0) return null;
            return JobQuickViewSelection.IsJob(selected[0]) ? selected[0] : null;
        }

        private void QueryStatus(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { command.Enabled = !disposed && SelectedJob() != null; }
            catch { command.Enabled = false; }
        }

        private void OpenSelectedJob(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { Open(JobQuickViewSelection.Capture(SelectedJob())); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void OpenContextJob(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var selection = (sender as ToolStripMenuItem)?.Tag as JobQuickViewSelection;
                if (selection != null) Open(selection);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void Open(JobQuickViewSelection selection)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || package.DisposalToken.IsCancellationRequested) return;
            var existing = jobTabs.FirstOrDefault(tab => tab.Matches(selection));
            if (existing != null)
            {
                ToolWindowDisplay.ShowAsDocument(existing);
                return;
            }
            var pane = package.FindToolWindow(typeof(JobQuickViewPane), ++nextTabId, true) as JobQuickViewPane;
            if (pane == null) throw new InvalidOperationException(LocalizationManager.T("Cannot create the Quick Manage tab."));
            try
            {
                pane.Initialize(selection);
                pane.Closed += JobTabClosed;
                jobTabs.Add(pane);
                ToolWindowDisplay.ShowAsDocument(pane);
            }
            catch
            {
                pane.Dispose();
                throw;
            }
        }

        private void JobTabClosed(object sender, EventArgs e)
        {
            var pane = (JobQuickViewPane)sender;
            pane.Closed -= JobTabClosed;
            jobTabs.Remove(pane);
        }

        private void ShowError(Exception error)
        {
            VsShellUtilities.ShowMessageBox(package, error.Message, LocalizationManager.T("Quick Manage"),
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private static Image LoadSettingsIcon()
        {
            using (var stream = typeof(JobQuickViewCommand).Assembly.GetManifestResourceStream("MSSQLTool.Resources.settings.png"))
            {
                if (stream == null) return null;
                using (var image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }

        private void DetachMenu()
        {
            if (menu != null)
            {
                menu.Opening -= MenuOpening;
                if (!menu.IsDisposed)
                {
                    if (quickViewItem != null) menu.Items.Remove(quickViewItem);
                    if (separator != null) menu.Items.Remove(separator);
                }
            }
            quickViewItem?.Dispose();
            separator?.Dispose();
            quickViewItem = null;
            separator = null;
            menu = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                disposed = true;
                attachTimer.Stop();
                attachTimer.Tick -= AttachTimerTick;
                DetachMenu();
                if (tree != null)
                {
                    tree.ContextMenuStripChanged -= ContextMenuChanged;
                    tree.Disposed -= TreeDisposed;
                    tree = null;
                }
                commands.RemoveCommand(command);
                settingsIcon?.Dispose();
                foreach (var pane in jobTabs) pane.Closed -= JobTabClosed;
                jobTabs.Clear();
            });
        }
    }
}
