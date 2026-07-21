using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;
using SamsungSwitchWatch.Viewer.Models;
using System.IO;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class WpfSmokeTests
{
    [Fact]
    public void MainWindow_CanBeConstructedWithApplicationResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-WpfSmoke", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new App();
                app.InitializeComponent();
                var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
                var viewModel = new DashboardViewModel(new ViewerSettings(), store);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                var window = new MainWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                Assert.Equal(1280, window.MinWidth);
                Assert.Equal(720, window.MinHeight);
                Assert.True(window.IsVisible);
                var connection = new ConnectionSettingsWindow(
                    new ViewerSettings { DemoMode = false, AgentUri = "http://monitor-pc:18443" },
                    (_, _) => Task.CompletedTask);
                connection.Show();
                connection.UpdateLayout();
                Assert.Equal("monitor-pc", connection.AgentAddressTextBox.Text);
                Assert.Equal("18443", connection.AgentPortTextBox.Text);
                Assert.Equal("사내 관리망 전용 · 암호화/인증 없음", connection.TransportWarningText.Text);
                connection.Close();
                var mini = new MiniWindow(viewModel, true);
                mini.Show();
                mini.UpdateLayout();
                Assert.True(mini.IsVisible);
                mini.AllowClose();
                mini.Close();
                var popup = new AlertPopup(new EventViewModel(new SwitchEventDto(
                    1, "smoke-event", "SW-DEMO", "ACCESS-SW-DEMO", DateTimeOffset.UtcNow,
                    DeviceHealth.Critical, "상태 변경", "업링크 Down", "UP → DOWN")));
                popup.Show();
                popup.UpdateLayout();
                Assert.True(popup.IsVisible);
                popup.Close();
                window.AllowClose();
                window.Close();
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                app.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF smoke thread did not finish.");
        Assert.Null(failure);
    }
}
