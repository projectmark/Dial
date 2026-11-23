using System.Runtime.InteropServices;
using Chassis.Core.Interop;
using Chassis.Core.Versioning;

namespace Chassis.Core.Tray;

public sealed record TrayMenuItemDescriptor(string Id, string Label, Action Callback, bool IsSeparator = false);

/// <summary>
/// Opt-in Win32 Shell_NotifyIconW tray service hosted via <see cref="Win32MessageHost"/>.
/// </summary>
public interface ITrayService : IDisposable
{
    void Initialize(string showLabel, string exitLabel, Action onRestore, Action onExit);
    void Initialize(string showLabel, string exitLabel, Action onRestore, Action onExit, Action? onPrimaryClick)
        => Initialize(showLabel, exitLabel, onRestore, onExit);
    void UpdateLabels(string showLabel, string exitLabel);
    void SetCustomMenuItems(IReadOnlyList<TrayMenuItemDescriptor> items);
    void ShowNotification(string title, string message);
}

/// <summary>
/// Zero-XAML Win32 notification area icon and native context menu provider.
/// </summary>
public sealed class TrayService : ITrayService
{
    private const uint WmTrayCallback = 0x8000 + 100;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonotify = 0x0080;

    private const uint CmdShow = 1001;
    private const uint CmdExit = 1002;
    private const uint CmdCustomBase = 2000;

    private readonly Win32MessageHost _messageHost;
    private readonly Guid _handlerId;
    private IReadOnlyList<TrayMenuItemDescriptor> _customItems = Array.Empty<TrayMenuItemDescriptor>();
    private Action? _onRestore;
    private Action? _onPrimaryClick;
    private Action? _onExit;
    private string _showLabel = "Open";
    private string _exitLabel = "Exit";
    private IntPtr _ownedIcon;
    private bool _initialized;
    private bool _disposed;

    public TrayService(Win32MessageHost messageHost)
    {
        _messageHost = messageHost;
        _handlerId = _messageHost.AddMessageHandler(OnWin32Message);
    }

    /// <inheritdoc />
    public void Initialize(string showLabel, string exitLabel, Action onRestore, Action onExit)
        => Initialize(showLabel, exitLabel, onRestore, onExit, onPrimaryClick: null);

    /// <inheritdoc />
    public void Initialize(string showLabel, string exitLabel, Action onRestore, Action onExit, Action? onPrimaryClick)
    {
        _showLabel = showLabel;
        _exitLabel = exitLabel;
        _onRestore = onRestore;
        _onPrimaryClick = onPrimaryClick;
        _onExit = onExit;

        if (_initialized || !OperatingSystem.IsWindows() || _messageHost.Handle == IntPtr.Zero)
        {
            return;
        }

        _messageHost.Invoke(hwnd =>
        {
            var data = CreateNotifyIconData(hwnd);
            data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
            data.uCallbackMessage = WmTrayCallback;
            data.hIcon = ResolveAppIcon();
            data.szTip = Truncate(AppVersion.ProductTitle, 127);
            Shell_NotifyIconW(NimAdd, ref data);
        });

        _initialized = true;
    }

    /// <inheritdoc />
    public void UpdateLabels(string showLabel, string exitLabel)
    {
        _showLabel = showLabel;
        _exitLabel = exitLabel;
    }

    /// <inheritdoc />
    public void SetCustomMenuItems(IReadOnlyList<TrayMenuItemDescriptor> items)
        => _customItems = items ?? Array.Empty<TrayMenuItemDescriptor>();

    /// <inheritdoc />
    public void ShowNotification(string title, string message)
    {
        if (!_initialized || _disposed || !OperatingSystem.IsWindows() || _messageHost.Handle == IntPtr.Zero)
        {
            return;
        }

        _messageHost.Invoke(hwnd =>
        {
            var data = CreateNotifyIconData(hwnd);
            data.uFlags = NifInfo | NifShowTip;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = 0x00000001; // NIIF_INFO
            Shell_NotifyIconW(NimModify, ref data);
        });
    }

    internal IntPtr HandleTrayMessageForTest(uint msg, IntPtr lParam, out bool handled)
        => OnWin32Message(IntPtr.Zero, msg, IntPtr.Zero, lParam, out handled);

    private IntPtr OnWin32Message(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        if (msg == WmTrayCallback)
        {
            var eventId = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (eventId == WmLButtonUp)
            {
                GrantForegroundPermission();
                (_onPrimaryClick ?? _onRestore)?.Invoke();
                handled = true;
                return IntPtr.Zero;
            }

            if (eventId == WmLButtonDblClk)
            {
                // Ignore double-click notifications so a double-click doesn't toggle twice alongside WM_LBUTTONUP.
                handled = true;
                return IntPtr.Zero;
            }

            if (eventId == WmRButtonUp)
            {
                ShowContextMenu(hwnd);
                handled = true;
                return IntPtr.Zero;
            }
        }

        handled = false;
        return IntPtr.Zero;
    }

    private static void GrantForegroundPermission()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AllowSetForegroundWindow((uint)Environment.ProcessId);
        }
    }

    private void ShowContextMenu(IntPtr hwnd)
    {
        var customItems = _customItems;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, (UIntPtr)CmdShow, _showLabel);

            if (customItems.Count > 0)
            {
                AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
                for (var i = 0; i < customItems.Count; i++)
                {
                    var item = customItems[i];
                    if (item.IsSeparator)
                    {
                        AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
                    }
                    else
                    {
                        AppendMenuW(menu, MfString, (UIntPtr)(CmdCustomBase + (uint)i), item.Label);
                    }
                }
            }

            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)CmdExit, _exitLabel);

            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNonotify, pt.X, pt.Y, hwnd, IntPtr.Zero);

            if (selected == CmdShow)
            {
                GrantForegroundPermission();
                _onRestore?.Invoke();
            }
            else if (selected == CmdExit)
            {
                _onExit?.Invoke();
            }
            else if (selected >= CmdCustomBase)
            {
                var idx = (int)(selected - CmdCustomBase);
                if (idx >= 0 && idx < customItems.Count)
                {
                    customItems[idx].Callback();
                }
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static NOTIFYICONDATAW CreateNotifyIconData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr ResolveAppIcon()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            ushort iconIdx = 0;
            var icon = ExtractAssociatedIconW(IntPtr.Zero, exePath, ref iconIdx);
            if (icon != IntPtr.Zero)
            {
                _ownedIcon = icon;
                return icon;
            }
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private static string Truncate(string value, int maxLen)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLen ? value : value[..maxLen];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && OperatingSystem.IsWindows() && _messageHost.Handle != IntPtr.Zero)
        {
            _messageHost.Invoke(hwnd =>
            {
                var data = CreateNotifyIconData(hwnd);
                Shell_NotifyIconW(NimDelete, ref data);
            });
        }

        if (_ownedIcon != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
        }

        _messageHost.RemoveMessageHandler(_handlerId);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
