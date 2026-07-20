using System.Windows;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ViewerSettings _original;

    public ConnectionSettingsWindow(ViewerSettings settings)
    {
        InitializeComponent();
        _original = settings;
        DemoModeCheckBox.IsChecked = settings.DemoMode;
        AgentUriTextBox.Text = settings.AgentUri;
        FingerprintTextBox.Text = string.Join(Environment.NewLine, settings.AcceptedCertificateFingerprints);
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
        UpdateLiveControls();
    }

    public ViewerSettings? Result { get; private set; }

    private void DemoMode_Changed(object sender, RoutedEventArgs e) => UpdateLiveControls();

    private void UpdateLiveControls()
    {
        var enabled = DemoModeCheckBox.IsChecked != true;
        AgentUriTextBox.IsEnabled = enabled;
        FingerprintTextBox.IsEnabled = enabled;
        TokenPasswordBox.IsEnabled = enabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> fingerprints = [];
        if (DemoModeCheckBox.IsChecked != true &&
            !ViewerSettingsSanitizer.TryParseFingerprintInput(FingerprintTextBox.Text, out fingerprints, out var fingerprintReason))
        {
            ValidationText.Text = fingerprintReason;
            return;
        }

        var candidate = new ViewerSettings
        {
            DemoMode = DemoModeCheckBox.IsChecked == true,
            AgentUri = AgentUriTextBox.Text,
            CertificateFingerprint = fingerprints.FirstOrDefault() ?? string.Empty,
            CertificateFingerprints = fingerprints.ToList(),
            BearerToken = string.IsNullOrEmpty(TokenPasswordBox.Password) ? _original.BearerToken : TokenPasswordBox.Password,
            ProtectedBearerToken = _original.ProtectedBearerToken,
            LastEventSequence = _original.LastEventSequence,
            EventCursors = new Dictionary<string, long>(_original.EventCursors, StringComparer.Ordinal),
            MiniTopmost = _original.MiniTopmost,
            MiniLeft = _original.MiniLeft,
            MiniTop = _original.MiniTop,
            MainLeft = _original.MainLeft,
            MainTop = _original.MainTop,
            MainWidth = _original.MainWidth,
            MainHeight = _original.MainHeight,
            StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true
        };

        var clean = ViewerSettingsSanitizer.Sanitize(candidate);
        clean.BearerToken = candidate.BearerToken;
        if (!clean.DemoMode && !ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason))
        {
            ValidationText.Text = reason;
            return;
        }

        Result = clean;
        DialogResult = true;
    }
}
