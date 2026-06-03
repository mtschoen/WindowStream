using System.Globalization;
using WindowStream.Server.Observability;

namespace WindowStream.Server.ViewModels;

public sealed record LogEntryViewModel(LogEntry Entry)
{
    public string Timestamp => Entry.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Severity => Entry.Severity.ToString().ToUpperInvariant();
    public string EventType => Entry.EventType;
    public int? StreamId => Entry.StreamId;
    public string Message => Entry.Message;
    public string SeverityColor => Entry.Severity switch
    {
        Core.Observability.Severity.Error => "#FF6060",
        Core.Observability.Severity.Warning => "#FFC040",
        _ => "#C0C0C0",
    };
}
