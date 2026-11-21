using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Chassis.Core.Interop;

/// <summary>
/// Delegate invoked when the hidden Win32 message-only window receives a Windows message.
/// </summary>
public delegate IntPtr Win32MessageHandler(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out bool handled);

/// <summary>
/// Opt-in background STA thread hosting a Win32 HWND_MESSAGE (-3) message loop for hotkeys and tray icons.
/// </summary>
public sealed partial class Win32MessageHost : IDisposable
{
    private const uint WmAppExecute = 0x8000 + 42;
    private const uint WmQuit = 0x0012;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private readonly ConcurrentQueue<Action<IntPtr>> _pendingActions = new();
    private readonly ConcurrentDictionary<Guid, Win32MessageHandler> _handlers = new();
    private WndProcDelegate? _wndProcDelegate;
    private string? _windowClassName;
    private uint _threadId;
    private bool _disposed;

    public Win32MessageHost()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Chassis.Win32MessageHost"
        };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }
        _thread.Start();
        _readyEvent.Wait(TimeSpan.FromSeconds(5));
    }

    public IntPtr Handle { get; private set; }

    public Guid AddMessageHandler(Win32MessageHandler handler)
    {
        var id = Guid.NewGuid();
        _handlers[id] = handler;
        return id;
    }

    public void RemoveMessageHandler(Guid id) => _handlers.TryRemove(id, out _);

    /// <summary>
    /// Marshals an operation synchronously onto the STA Win32 message thread.
    /// </summary>
    public T Invoke<T>(Func<IntPtr, T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Win32MessageHost window handle is unavailable.");
        }

        if (GetCurrentThreadId() == _threadId)
        {
            return func(Handle);
        }

        T? result = default;
        Exception? captured = null;
        using var done = new ManualResetEventSlim(false);

        _pendingActions.Enqueue(hwnd =>
        {
            try
            {
                result = func(hwnd);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                done.Set();
            }
        });

        PostMessageW(Handle, WmAppExecute, IntPtr.Zero, IntPtr.Zero);
        done.Wait();

        if (captured is not null)
        {
            throw new InvalidOperationException("Error executing Win32MessageHost action.", captured);
        }

        return result!;
    }

    public void Invoke(Action<IntPtr> action) => Invoke(hwnd =>
    {
        action(hwnd);
        return true;
    });

    private void ThreadMain()
    {
        if (!OperatingSystem.IsWindows())
        {
            _readyEvent.Set();
            return;
        }

        _threadId = GetCurrentThreadId();
        _windowClassName = $"ChassisMsgHost_{Guid.NewGuid():N}";
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _windowClassName
        };

        RegisterClassExW(ref wc);

        Handle = CreateWindowExW(
            0,
            _windowClassName,
            _windowClassName,
            0,
            0, 0, 0, 0,
            HwndMessage,
            IntPtr.Zero,
            wc.hInstance,
            IntPtr.Zero);

        _readyEvent.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        if (!string.IsNullOrEmpty(_windowClassName))
        {
            UnregisterClassW(_windowClassName, GetModuleHandleW(null));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppExecute)
        {
            while (_pendingActions.TryDequeue(out var action))
            {
                action(hwnd);
            }
            return IntPtr.Zero;
        }

        foreach (var handler in _handlers.Values)
        {
            var result = handler(hwnd, msg, wParam, lParam, out var handled);
            if (handled)
            {
                return result;
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            PostMessageW(Handle, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _handlers.Clear();
        _readyEvent.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
