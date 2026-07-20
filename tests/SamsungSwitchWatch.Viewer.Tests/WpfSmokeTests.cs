using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
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
