using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Infrastructure;

public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var health = value is DeviceHealth typed ? typed : DeviceHealth.Empty;
        var background = string.Equals(parameter?.ToString(), "Background", StringComparison.OrdinalIgnoreCase);
        var hex = (health, background) switch
        {
            (DeviceHealth.Normal, false) => "#16A34A",
            (DeviceHealth.Normal, true) => "#DCFCE7",
            (DeviceHealth.Warning, false) => "#D97706",
            (DeviceHealth.Warning, true) => "#FEF3C7",
            (DeviceHealth.Critical, false) => "#DC2626",
            (DeviceHealth.Critical, true) => "#FEE2E2",
            (DeviceHealth.Disconnected, false) => "#64748B",
            (DeviceHealth.Disconnected, true) => "#E2E8F0",
            (DeviceHealth.Loading, false) => "#2563EB",
            (DeviceHealth.Loading, true) => "#DBEAFE",
            (_, false) => "#94A3B8",
            _ => "#F1F5F9"
        };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HealthToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DeviceHealth.Normal => "정상",
        DeviceHealth.Warning => "경고",
        DeviceHealth.Critical => "장애",
        DeviceHealth.Disconnected => "연결 끊김",
        DeviceHealth.Loading => "확인 중",
        _ => "데이터 없음"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 0.52 : 1.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
