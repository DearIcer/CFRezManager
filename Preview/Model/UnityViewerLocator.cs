using System.IO;

namespace CFRezManager;

internal static class UnityViewerLocator
{
    private const string ViewerEnvironmentVariable = "CFREZ_UNITY_VIEWER";

    private static string BundledViewerPath => Path.Combine(
        AppContext.BaseDirectory,
        "tools",
        "UnityModelViewer",
        "CFRezModelViewer.exe");

    public static string? FindViewerExecutable()
    {
        string? overridePath = Environment.GetEnvironmentVariable(ViewerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        return File.Exists(BundledViewerPath) ? BundledViewerPath : null;
    }
}
