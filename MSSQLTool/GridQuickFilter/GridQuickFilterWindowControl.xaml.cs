using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using MSSQLTool.PivotGrid;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace MSSQLTool.GridQuickFilter
{
    public partial class GridQuickFilterWindowControl : UserControl, IDisposable
    {
        private readonly ToolWindowThemeController theme;
        private PivotSnapshot snapshot;
        private CancellationTokenSource operation;
        private DispatcherTimer filterDebounceTimer;
        private bool disposed;

        private sealed class ColumnChoice
        {
            public string Label { get; set; }
            public int Index { get; set; }
        }

        public GridQuickFilterWindowControl()
        {
            InitializeComponent();
            theme = new ToolWindowThemeController(this, () => ToolWindowThemeResources.ApplySharedTheme(this));

            filterDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            filterDebounceTimer.Tick += FilterDebounceTimer_Tick;
        }

        internal Task LoadGridAsync(IGridControl grid)
        {
            return RunAsync(async token =>
            {
                StatusText.Text = LocalizationManager.T("Capturing result grid...");
                snapshot = await PivotGridSnapshot.CaptureAsync(grid, token,
                    (done, total) => StatusText.Text = string.Format("Copying grid: {0:N0} / {1:N0} rows...", done, total));

                if (disposed || snapshot == null) return;

                // Populate column choices
                var choices = new List<ColumnChoice>
                {
                    new ColumnChoice { Label = LocalizationManager.T("(All Columns / 全部列)"), Index = -1 }
                };
                for (int i = 0; i < snapshot.Fields.Length; i++)
                {
                    choices.Add(new ColumnChoice { Label = snapshot.Fields[i].Label, Index = i });
                }
                ColumnCombo.ItemsSource = choices;
                ColumnCombo.SelectedIndex = 0;

                // Setup grid columns
                FilterDataGrid.Columns.Clear();
                for (int i = 0; i < snapshot.Fields.Length; i++)
                {
                    var col = new DataGridTextColumn
                    {
                        Header = snapshot.Fields[i].Label,
                        Binding = new Binding("[" + i + "]")
                    };
                    FilterDataGrid.Columns.Add(col);
                }

                // Initial display
                ApplyFilter();
            });
        }

        private void FilterDebounceTimer_Tick(object sender, EventArgs e)
        {
            filterDebounceTimer.Stop();
            ApplyFilter();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            filterDebounceTimer.Stop();
            filterDebounceTimer.Start();
        }

        private void ColumnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            MatchCaseCheckBox.IsChecked = false;
            ExactMatchCheckBox.IsChecked = false;
            if (ColumnCombo.Items.Count > 0) ColumnCombo.SelectedIndex = 0;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (snapshot == null || snapshot.Rows == null) return;

            string keyword = SearchBox.Text?.Trim() ?? string.Empty;
            bool matchCase = MatchCaseCheckBox.IsChecked == true;
            bool exactMatch = ExactMatchCheckBox.IsChecked == true;
            int colIndex = (ColumnCombo.SelectedItem as ColumnChoice)?.Index ?? -1;
            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var filteredRows = new List<string[]>();
            if (string.IsNullOrEmpty(keyword))
            {
                filteredRows.AddRange(snapshot.Rows);
            }
            else
            {
                foreach (var row in snapshot.Rows)
                {
                    bool matched = false;
                    if (colIndex >= 0 && colIndex < row.Length)
                    {
                        string val = row[colIndex] ?? string.Empty;
                        matched = exactMatch ? string.Equals(val, keyword, comparison) : val.IndexOf(keyword, comparison) >= 0;
                    }
                    else
                    {
                        for (int c = 0; c < row.Length; c++)
                        {
                            string val = row[c] ?? string.Empty;
                            if (exactMatch ? string.Equals(val, keyword, comparison) : val.IndexOf(keyword, comparison) >= 0)
                            {
                                matched = true;
                                break;
                            }
                        }
                    }
                    if (matched) filteredRows.Add(row);
                }
            }

            FilterDataGrid.ItemsSource = filteredRows;
            StatusText.Text = string.Format(LocalizationManager.T("Displaying {0:N0} / {1:N0} rows ({2} columns)"),
                filteredRows.Count, snapshot.Rows.Length, snapshot.Fields.Length);
        }

        private List<string[]> GetCurrentRows()
        {
            return (FilterDataGrid.ItemsSource as List<string[]>) ?? (snapshot?.Rows?.ToList() ?? new List<string[]>());
        }

        private void CopyCsv_Click(object sender, RoutedEventArgs e)
        {
            if (snapshot == null) return;
            var rows = GetCurrentRows();
            var sb = new StringBuilder();

            // Headers
            sb.AppendLine(string.Join(",", snapshot.Fields.Select(f => QuoteCsv(f.Label))));
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",", r.Select(QuoteCsv)));
            }
            SetClipboard(sb.ToString());
        }

        private void CopyTsv_Click(object sender, RoutedEventArgs e)
        {
            if (snapshot == null) return;
            var rows = GetCurrentRows();
            var sb = new StringBuilder();

            sb.AppendLine(string.Join("\t", snapshot.Fields.Select(f => f.Label)));
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join("\t", r.Select(v => (v ?? string.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " "))));
            }
            SetClipboard(sb.ToString());
        }

        private void CopyInsert_Click(object sender, RoutedEventArgs e)
        {
            if (snapshot == null) return;
            var rows = GetCurrentRows();
            if (rows.Count == 0) return;

            var sb = new StringBuilder();
            string cols = string.Join(", ", snapshot.Fields.Select(f => "[" + f.Label.Replace("]", "]]") + "]"));
            sb.AppendLine("INSERT INTO #TempResults (" + cols + ") VALUES");

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string vals = string.Join(", ", r.Select(v => v == null || v == "NULL" ? "NULL" : "N'" + v.Replace("'", "''") + "'"));
                sb.Append("  (" + vals + ")");
                sb.AppendLine(i == rows.Count - 1 ? ";" : ",");
            }
            SetClipboard(sb.ToString());
        }

        private void CopyJson_Click(object sender, RoutedEventArgs e)
        {
            if (snapshot == null) return;
            var rows = GetCurrentRows();
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append("  { ");
                var parts = new List<string>();
                for (int c = 0; c < snapshot.Fields.Length; c++)
                {
                    string key = snapshot.Fields[c].Label.Replace("\"", "\\\"");
                    string val = (rows[i][c] ?? string.Empty).Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
                    parts.Add("\"" + key + "\": \"" + val + "\"");
                }
                sb.Append(string.Join(", ", parts));
                sb.AppendLine(i == rows.Count - 1 ? " }" : " },");
            }
            sb.AppendLine("]");
            SetClipboard(sb.ToString());
        }

        private void ExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (snapshot == null) return;
            var rows = GetCurrentRows();
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV File (*.csv)|*.csv",
                    FileName = "QuickFilter_Results_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (sfd.ShowDialog() == true)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(",", snapshot.Fields.Select(f => QuoteCsv(f.Label))));
                    foreach (var r in rows)
                    {
                        sb.AppendLine(string.Join(",", r.Select(QuoteCsv)));
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show(LocalizationManager.Format("Successfully exported {0} rows to {1}", rows.Count, sfd.FileName),
                        LocalizationManager.T("Export Complete"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(LocalizationManager.Format("Export failed: {0}", ex.Message),
                    LocalizationManager.T("Export Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string QuoteCsv(string val)
        {
            if (val == null) return string.Empty;
            if (val.Contains(",") || val.Contains("\"") || val.Contains("\n") || val.Contains("\r"))
                return "\"" + val.Replace("\"", "\"\"") + "\"";
            return val;
        }

        private void SetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                StatusText.Text = LocalizationManager.T("Copied to clipboard!");
            }
            catch (Exception ex)
            {
                StatusText.Text = LocalizationManager.Format("Copy failed: {0}", ex.Message);
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task> work)
        {
            operation?.Cancel();
            operation?.Dispose();
            var cts = new CancellationTokenSource();
            operation = cts;
            try
            {
                await work(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "GridQuickFilter operation failed");
                StatusText.Text = ex.Message;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            filterDebounceTimer?.Stop();
            operation?.Cancel();
            operation?.Dispose();
        }
    }
}
