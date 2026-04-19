using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

internal static class WindowEnumerationFilters
{
    public static readonly IReadOnlySet<string> ExcludedClassNames =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Progman",
            "Shell_TrayWnd",
            "WorkerW",
            "Windows.UI.Core.CoreWindow",
            "ApplicationFrameWindow",
        };

    public static bool PassesFilters(
        bool isVisible, string title, string className, int widthPixels, int heightPixels)
    {
        if (!isVisible) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (ExcludedClassNames.Contains(className)) return false;
        if (widthPixels <= 0 || heightPixels <= 0) return false;
        return true;
    }
}
