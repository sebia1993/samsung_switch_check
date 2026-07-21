using System.ComponentModel;
using System.Windows;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private readonly ViewerExportService _exportService = new();

    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void AllowClose() => _allowClose = true;

    public void FocusSelectedEvent()
    {
        if (DataContext is not DashboardViewModel { SelectedEvent: { } selected }) return;
        Dispatcher.BeginInvoke(() =>
        {
            RecentEventsList.ScrollIntoView(selected);
            RecentEventsList.UpdateLayout();
            if (RecentEventsList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem container)
            {
                container.Focus();
            }
            else
            {
                RecentEventsList.Focus();
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => (Application.Current as App)?.RestoreMainWindowBounds(this);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        (Application.Current as App)?.ShowTrayHint();
    }

    private void ShowMiniWindow_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowMiniWindow();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenConnectionSettings();

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(ViewerExportFormat.Csv);

    private async void ExportJson_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(ViewerExportFormat.Json);

    private async Task ExportAsync(ViewerExportFormat format)
    {
        if (DataContext is not DashboardViewModel viewModel) return;
        var extension = format == ViewerExportFormat.Csv ? "csv" : "json";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "필터된 이벤트 내보내기",
            FileName = $"switchwatch-events-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            DefaultExt = "." + extension,
            AddExtension = true,
            Filter = format == ViewerExportFormat.Csv
                ? "CSV 파일 (*.csv)|*.csv"
                : "JSON 파일 (*.json)|*.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var result = await _exportService.ExportAsync(dialog.FileName, format, viewModel.FilteredEvents);
        viewModel.ReportOperation(result.Success
            ? $"필터된 이벤트 {result.ExportedCount}건을 안전하게 내보냈습니다. · {result.Code}"
            : $"이벤트 내보내기 실패 · {result.Code}");
        if (!result.Success)
        {
            MessageBox.Show(this, $"내보내기를 완료하지 못했습니다.\n오류 코드: {result.Code}",
                "Samsung Switch Watch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
