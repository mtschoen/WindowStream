using System.Globalization;

namespace WindowStream.Server.Pages;

/// <summary>
/// Converts the server status string to a color for the status indicator dot.
/// </summary>
public sealed class StatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? "";
        return status switch
        {
            "Serving" => Colors.LimeGreen,
            "Starting…" => Colors.Orange,
            "Stopped" => Colors.Gray,
            _ when status.StartsWith("Error", StringComparison.Ordinal) => Colors.Red,
            _ => Colors.Gray
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
