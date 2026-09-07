using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CFRezManager;

public partial class ModelPreviewWindow : Window
{
    private const string AnimCommandFileName = "anim-command.txt";

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

        _viewerHost = new UnityViewerHost(viewerExePath, _previewExport.ObjPath, _previewExport.AnimPackagePath);
        _viewerHost.ViewerReady += ViewerHost_ViewerReady;
        _viewerHost.ViewerFailed += ViewerHost_ViewerFailed;
        ViewerContainer.Children.Insert(0, _viewerHost);

        SetupAnimationSelector(document);
    }

    private void SetupAnimationSelector(LithTechModelDocument document)
    {
        if (document.Animations.Count == 0 || _previewExport?.AnimPackagePath is null)
        {
            return;
        }

        AnimationLabelText.Text = LocalizedText.T("ModelPreviewAnimationLabel");
        AnimationComboBox.Items.Add(new ComboBoxItem { Content = LocalizedText.T("ModelPreviewBindPose") });

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LithTechModelAnimation animation in document.Animations)
        {
            string name = string.IsNullOrWhiteSpace(animation.Name) ? "animation" : animation.Name;
            string candidate = name;
            int suffix = 2;
            while (!usedNames.Add(candidate))
            {
                candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, suffix);
                suffix++;
            }

            AnimationComboBox.Items.Add(new ComboBoxItem { Content = candidate });
        }

        AnimationComboBox.SelectionChanged += AnimationComboBox_SelectionChanged;
        AnimationPanel.Visibility = Visibility.Visible;
        // Default to the first track; setting the index also writes the initial command file,
        // so a late-starting viewer process still picks up the selection.
        AnimationComboBox.SelectedIndex = 1;
    }

    private void AnimationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int trackIndex = AnimationComboBox.SelectedIndex - 1;
        WriteAnimationCommand(trackIndex < 0 ? "bind" : trackIndex.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteAnimationCommand(string command)
    {
        try
        {
            string? directoryPath = _previewExport?.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            string tempPath = Path.Combine(directoryPath, AnimCommandFileName + ".tmp");
            File.WriteAllText(tempPath, command);
            File.Move(tempPath, Path.Combine(directoryPath, AnimCommandFileName), overwrite: true);
        }
        catch
        {
            // Best-effort track switching; the viewer keeps its current animation on failure.
        }
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
