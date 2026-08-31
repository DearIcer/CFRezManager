using System.Windows;
using System.Windows.Media;

namespace CFRezManager;

public partial class ModelPreviewWindow : Window
{
    private UnityPreviewExport? _previewExport;
    private UnityViewerHost? _viewerHost;

    public ModelPreviewWindow(
        string fileName,
        LithTechModelDocument document,
        string? modelInfo = null,
        Func<string, ImageSource?>? textureResolver = null)
    {
        InitializeComponent();
        WindowThemeHelper.Apply(this, ThemeManager.Parse(UserSettings.Load().Theme));

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        PreviewInfoText.Text = modelInfo ?? FormatDocumentInfo(document);
        ShortcutHintText.Text = LocalizedText.T("ModelPreviewShortcutHint");
        ViewerStatusText.Text = LocalizedText.T("UnityViewerLoading");
        Title = $"{fileName} - {document.Name}";

        string? viewerExePath = UnityViewerLocator.FindViewerExecutable();
        if (viewerExePath is null)
        {
            throw new InvalidOperationException(LocalizedText.T("UnityViewerMissing"));
        }

        _previewExport = UnityPreviewExporter.Export(fileName, document, textureResolver);
        if (_previewExport is null)
        {
            throw new InvalidOperationException(LocalizedText.T("UnityViewerExportFailed"));
        }

        _viewerHost = new UnityViewerHost(viewerExePath, _previewExport.ObjPath);
        _viewerHost.ViewerReady += ViewerHost_ViewerReady;
        _viewerHost.ViewerFailed += ViewerHost_ViewerFailed;
        ViewerContainer.Children.Insert(0, _viewerHost);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewerHost is not null)
        {
            ViewerContainer.Children.Remove(_viewerHost);
            _viewerHost.Dispose();
            _viewerHost = null;
        }

        UnityPreviewExporter.Cleanup(_previewExport);
        _previewExport = null;
        base.OnClosed(e);
    }

    private void ViewerHost_ViewerReady(object? sender, EventArgs e)
    {
        ViewerStatusText.Visibility = Visibility.Collapsed;
    }

    private void ViewerHost_ViewerFailed(object? sender, string reason)
    {
        // The host HWND has airspace over WPF content; hide it so the error text stays visible.
        if (_viewerHost is not null)
        {
            _viewerHost.Visibility = Visibility.Collapsed;
        }

        ViewerStatusText.Text = LocalizedText.Format("UnityViewerFailed", reason);
    }

    private static string FormatDocumentInfo(LithTechModelDocument document)
    {
        return $"{document.StorageDescription} | {document.Meshes.Count:N0} mesh | {document.VertexCount:N0} vertices | {document.TriangleCount:N0} triangles";
    }
}
