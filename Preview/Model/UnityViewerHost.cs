using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CFRezManager;

internal sealed class UnityViewerHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmSize = 0x0005;
    private const int WmSetFocus = 0x0007;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ViewerWindowTimeout = TimeSpan.FromSeconds(15);

    private readonly string _viewerExePath;
    private readonly string _modelPath;
    private readonly string? _animPackagePath;
    private readonly DispatcherTimer _pollTimer;
    private Process? _process;
    private IntPtr _hostHandle;
    private IntPtr _unityWindowHandle;
    private DateTime _pollStartTime;
    private bool _viewerReady;
    private bool _disposing;

    public event EventHandler? ViewerReady;

    public event EventHandler<string>? ViewerFailed;

    public UnityViewerHost(string viewerExePath, string modelPath, string? animPackagePath = null)
    {
        _viewerExePath = viewerExePath;
        _modelPath = modelPath;
        _animPackagePath = animPackagePath;
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += PollTimer_Tick;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        GetClientRect(hwndParent.Handle, out NativeRect parentRect);
        int width = Math.Max(parentRect.Right - parentRect.Left, 640);
        int height = Math.Max(parentRect.Bottom - parentRect.Top, 480);

        _hostHandle = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible | WsClipChildren,
            0,
            0,
            width,
            height,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_hostHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        StartViewerProcess(width, height);
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposing = true;
        _pollTimer.Stop();
        StopViewerProcess();
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hostHandle = IntPtr.Zero;
        _unityWindowHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSize)
        {
            // The embedded Unity window does not track the parent size on its own.
            ResizeUnityWindow();
        }
        else if (msg == WmSetFocus && _unityWindowHandle != IntPtr.Zero)
        {
            SetFocus(_unityWindowHandle);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void StartViewerProcess(int width, int height)
    {
        try
        {
            string arguments = $"-parentHWND {_hostHandle} -screen-width {width} -screen-height {height} --cfrez-model \"{_modelPath}\"";
            if (!string.IsNullOrWhiteSpace(_animPackagePath))
            {
                arguments += $" --cfrez-anim \"{_animPackagePath}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _viewerExePath,
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_viewerExePath) ?? string.Empty
            };
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                RaiseViewerFailed("viewer process did not start");
                return;
            }

            _process.EnableRaisingEvents = true;
            _process.Exited += Process_Exited;
            _pollStartTime = DateTime.UtcNow;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            RaiseViewerFailed(ex.Message);
        }
    }

    private void StopViewerProcess()
    {
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= Process_Exited;
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(1000))
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
        }
        catch
        {
            // Best-effort shutdown of the external viewer process.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_unityWindowHandle != IntPtr.Zero)
        {
            _pollTimer.Stop();
            return;
        }

        IntPtr unityWindow = FindUnityChildWindow();
        if (unityWindow != IntPtr.Zero)
        {
            _unityWindowHandle = unityWindow;
            _pollTimer.Stop();
            _viewerReady = true;
            ResizeUnityWindow();
            ViewerReady?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_process is not null && _process.HasExited)
        {
            _pollTimer.Stop();
            RaiseViewerFailed($"viewer exited with code {_process.ExitCode}");
            return;
        }

        if (DateTime.UtcNow - _pollStartTime > ViewerWindowTimeout)
        {
            _pollTimer.Stop();
            RaiseViewerFailed("timed out waiting for the Unity window");
        }
    }

    private IntPtr FindUnityChildWindow()
    {
        if (_process is null || _hostHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr found = IntPtr.Zero;
        int processId = _process.Id;
        EnumChildWindows(_hostHandle, (childHandle, _) =>
        {
            GetWindowThreadProcessId(childHandle, out int childProcessId);
            if (childProcessId == processId)
            {
                found = childHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposing && !_viewerReady)
            {
                _pollTimer.Stop();
                RaiseViewerFailed("viewer exited before it was ready");
            }
        });
    }

    private void ResizeUnityWindow()
    {
        if (_unityWindowHandle == IntPtr.Zero || _hostHandle == IntPtr.Zero)
        {
            return;
        }

        GetClientRect(_hostHandle, out NativeRect rect);
        SetWindowPos(
            _unityWindowHandle,
            IntPtr.Zero,
            0,
            0,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            SwpNoZOrder | SwpNoActivate);
    }

    private void RaiseViewerFailed(string reason)
    {
        ViewerFailed?.Invoke(this, reason);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentHandle,
        IntPtr menuHandle,
        IntPtr instanceHandle,
        IntPtr createParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumChildWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}
