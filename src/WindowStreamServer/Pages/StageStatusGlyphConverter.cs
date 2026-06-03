using System.Globalization;
using WindowStream.Server.Observability;

namespace WindowStream.Server.Pages;

public sealed class StageStatusGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StageStatus status
            ? status switch
            {
                StageStatus.Ok => "✓",
                StageStatus.Warning => "⚠",
                StageStatus.Error => "✗",
                StageStatus.InProgress => "…",
                _ => "—",
            }
            : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
