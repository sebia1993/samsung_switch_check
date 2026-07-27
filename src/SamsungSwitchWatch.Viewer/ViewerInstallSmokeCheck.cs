using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Infrastructure;

namespace SamsungSwitchWatch.Viewer;

internal static class ViewerInstallSmokeCheck
{
    internal const string Argument = "--install-smoke-check";
    internal const int SuccessExitCode = 0;
    internal const int ApplicationResourceFailureExitCode = 20;
    internal const int ScreenResourceFailureExitCode = 21;
    internal const int UnexpectedFailureExitCode = 22;

    private static readonly (object Key, Type ExpectedType)[] RequiredApplicationResources =
    [
        ("HealthBrush", typeof(HealthToBrushConverter)),
        ("HealthText", typeof(HealthToTextConverter)),
        ("BoolOpacity", typeof(BoolToOpacityConverter)),
        ("BoolVisibility", typeof(BooleanToVisibilityConverter)),
        ("CanvasBrush", typeof(SolidColorBrush)),
        ("SurfaceBrush", typeof(SolidColorBrush)),
        ("TextBrush", typeof(SolidColorBrush)),
        ("MutedTextBrush", typeof(SolidColorBrush)),
        ("BorderBrush", typeof(SolidColorBrush)),
        ("PrimaryBrush", typeof(SolidColorBrush)),
        ("PrimaryHoverBrush", typeof(SolidColorBrush)),
        ("EmptyStateText", typeof(Style)),
        ("CardStyle", typeof(Style)),
        ("PrimaryButton", typeof(Style)),
        ("SecondaryButton", typeof(Style)),
        (typeof(Window), typeof(Style)),
        (typeof(TextBlock), typeof(Style))
    ];

    private static readonly Uri[] RequiredScreenResources =
    [
        new("/SamsungSwitchWatch.Viewer;component/MainWindow.xaml", UriKind.Relative),
        new("/SamsungSwitchWatch.Viewer;component/Views/ConnectionSettingsWindow.xaml", UriKind.Relative),
        new("/SamsungSwitchWatch.Viewer;component/Views/DeviceManagementWindow.xaml", UriKind.Relative),
        new("/SamsungSwitchWatch.Viewer;component/Views/MiniWindow.xaml", UriKind.Relative),
        new("/SamsungSwitchWatch.Viewer;component/Views/AlertPopup.xaml", UriKind.Relative)
    ];

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count == 1
        && string.Equals(arguments[0], Argument, StringComparison.Ordinal);

    internal static int Run(
        ResourceDictionary applicationResources,
        Func<Uri, Stream?>? openScreenResource = null)
    {
        try
        {
            foreach (var (key, expectedType) in RequiredApplicationResources)
            {
                if (!applicationResources.Contains(key)
                    || !expectedType.IsInstanceOfType(applicationResources[key]))
                {
                    return ApplicationResourceFailureExitCode;
                }
            }

            openScreenResource ??= OpenApplicationResource;
            foreach (var resourceUri in RequiredScreenResources)
            {
                using var stream = openScreenResource(resourceUri);
                if (stream is null || !stream.CanRead || stream.ReadByte() < 0)
                {
                    return ScreenResourceFailureExitCode;
                }
            }

            return SuccessExitCode;
        }
        catch
        {
            return UnexpectedFailureExitCode;
        }
    }

    private static Stream? OpenApplicationResource(Uri resourceUri) =>
        Application.GetResourceStream(resourceUri)?.Stream;
}
