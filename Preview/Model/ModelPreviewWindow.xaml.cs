using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CFRezManager;

public partial class ModelPreviewWindow : Window
{
    private const string AnimCommandFileName = "anim-command.txt";
    private const string AnimStatusFileName = "anim-status.txt";

    private UnityPreviewExport? _previewExport;
    private UnityViewerHost? _viewerHost;
    private DispatcherTimer? _statusTimer;
    private bool _isDraggingSlider;
    private bool _isPlaying = true;

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

        UpdatePlayPauseButton();
        PlaybackPanel.Visibility = Visibility.Visible;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void AnimationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int trackIndex = AnimationComboBox.SelectedIndex - 1;
        if (trackIndex < 0)
        {
            WriteAnimationCommand("bind");
            return;
        }

        // Switching tracks restarts playback from the beginning.
        _isPlaying = true;
        UpdatePlayPauseButton();
        WriteAnimationCommand(string.Format(CultureInfo.InvariantCulture, "track {0}", trackIndex));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        UpdatePlayPauseButton();
        WriteAnimationCommand(_isPlaying ? "play" : "pause");
    }

    private void UpdatePlayPauseButton()
    {
        PlayPauseButton.Content = LocalizedText.T(_isPlaying ? "PreviewPause" : "PreviewPlay");
    }

    private void PlaybackSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void PlaybackSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        WriteAnimationCommand(string.Format(CultureInfo.InvariantCulture, "seek {0:F3}", PlaybackSlider.Value));
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            string? directoryPath = _previewExport?.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            string statusPath = Path.Combine(directoryPath, AnimStatusFileName);
            if (!File.Exists(statusPath))
            {
                return;
            }

            string[] parts = File.ReadAllText(statusPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !string.Equals(parts[0], "track", StringComparison.OrdinalIgnoreCase))
            {
                // "bind" mode (or an unreadable status): show an idle progress bar.
                if (!_isDraggingSlider)
                {
                    PlaybackSlider.Value = 0;
                }

                PlaybackTimeText.Text = "0.00 / 0.00";
                return;
            }

            bool playing = parts[2] == "1";
            if (playing != _isPlaying)
            {
                _isPlaying = playing;
                UpdatePlayPauseButton();
            }

            double time = double.Parse(parts[3], CultureInfo.InvariantCulture);
            double length = double.Parse(parts[4], CultureInfo.InvariantCulture);
            PlaybackSlider.Maximum = Math.Max(length, 0.001);
            if (!_isDraggingSlider)
            {
                PlaybackSlider.Value = Math.Min(time, PlaybackSlider.Maximum);
            }

            PlaybackTimeText.Text = string.Format(CultureInfo.InvariantCulture, "{0:F2} / {1:F2}", time, length);
        }
        catch
        {
            // The viewer writes the status atomically, but tolerate any transient read failure.
        }
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
        _statusTimer?.Stop();
        _statusTimer = null;

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
