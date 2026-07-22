using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.RestoreMainWindowBounds(this);
        FocusManager.SetFocusedElement(this, DevicesList);
        _ = DevicesList.Focus();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && DataContext is DashboardViewModel { IsReadOnlyQueryRunning: true } queryViewModel)
        {
            if (queryViewModel.CancelReadOnlyQueryCommand.CanExecute(null))
            {
                queryViewModel.CancelReadOnlyQueryCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        EventSearchBox.Focus();
        EventSearchBox.SelectAll();
        e.Handled = true;
    }

    private void ReadOnlyQueryTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null))
            {
                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        var direction = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0
        };
        if (direction != 0 && viewModel.MoveReadOnlyQueryHistory(direction))
        {
            ReadOnlyQueryTextBox.CaretIndex = ReadOnlyQueryTextBox.Text.Length;
            e.Handled = true;
        }
    }

    private void CopyReadOnlyQueryOutput_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel
            || string.IsNullOrEmpty(viewModel.ReadOnlyQueryOutput)) return;
        try
        {
            System.Windows.Clipboard.SetText(viewModel.ReadOnlyQueryOutput);
            viewModel.ReportOperation("장비 명령 결과를 클립보드에 복사했습니다.");
        }
        catch (Exception)
        {
            viewModel.ReportOperation("장비 명령 결과 복사 실패 · CLIPBOARD_UNAVAILABLE");
        }
    }

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
