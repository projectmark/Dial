using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Chassis.Core.Interop;

namespace Chassis.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}

public sealed record HotkeyBinding(
    string Id,
    int AtomId,
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    Action Callback,
    string DisplayText);

/// <summary>
/// Opt-in system-wide hotkey registry backed by RegisterHotKey / WM_HOTKEY on <see cref="Win32MessageHost"/>.
/// </summary>
public interface IHotkeyService : IDisposable
{
    bool Register(string id, HotkeyModifiers modifiers, uint virtualKey, Action callback);
    bool Unregister(string id);
    void UnregisterAll();
    IReadOnlyDictionary<string, HotkeyBinding> GetActiveBindings();
}

/// <summary>
/// Win32 RegisterHotKey manager supporting clean unregistration and human-readable chord formatting.
/// </summary>
public sealed partial class HotkeyService : IHotkeyService
{
    private const uint WmHotkey = 0x0312;

    public static IReadOnlyList<uint> SelectableVirtualKeys { get; } =
        Enumerable.Range(0x41, 26).Select(static c => (uint)c)
            .Concat(Enumerable.Range(0x30, 10).Select(static d => (uint)d))
            .Append(0x20u)
            .ToArray();

    private readonly Win32MessageHost _messageHost;
    private readonly ConcurrentDictionary<string, HotkeyBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _handlerId;
    private int _nextAtomId = 0x2000;
    private bool _disposed;

    public HotkeyService(Win32MessageHost messageHost)
    {
        _messageHost = messageHost;
    }

    public static string FormatDisplayText(HotkeyModifiers modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(FormatVirtualKey(virtualKey));
        return string.Join("+", parts);
    }

    public static string FormatKeyName(uint virtualKey) => FormatVirtualKey(virtualKey);

    public static bool TryFormatNormalizedHotkey(
        HotkeyModifiers modifiers,
        uint virtualKey,
        bool requireModifier,
        out string normalized)
    {
        var masked = modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win);
        if (virtualKey == 0 ||
            (requireModifier && (masked & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)) == 0))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = FormatDisplayText(masked, virtualKey);
        return true;
    }

    public static bool TryParseHotkeyString(string? input, out HotkeyModifiers modifiers, out uint virtualKey)
    {
        modifiers = HotkeyModifiers.None;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var tokens = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                case "SPACE":
                    virtualKey = 0x20;
                    break;
                default:
                    if (token.Length == 1 && token[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                    {
                        virtualKey = char.ToUpperInvariant(token[0]);
                    }
                    else if (token.StartsWith('F') && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
                    {
                        virtualKey = (uint)(0x6F + fn);
                    }
                    break;
            }
        }

        return virtualKey != 0;
    }

    /// <inheritdoc />
    public bool Register(string id, HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(callback);

        if (_disposed || !OperatingSystem.IsWindows())
        {
            return false;
        }

        _handlerId ??= _messageHost.AddMessageHandler(OnWin32Message);
        if (_messageHost.Handle == IntPtr.Zero)
        {
            return false;
        }

        Unregister(id);

        var atomId = Interlocked.Increment(ref _nextAtomId);
        var modsWithNoRepeat = modifiers | HotkeyModifiers.NoRepeat;
        var registered = _messageHost.Invoke(hwnd => RegisterHotKey(hwnd, atomId, (uint)modsWithNoRepeat, virtualKey));
        if (!registered)
        {
            return false;
        }

        var binding = new HotkeyBinding(
            id,
            atomId,
            modifiers,
            virtualKey,
            callback,
            FormatDisplayText(modifiers, virtualKey));
        _bindings[id] = binding;
        return true;
    }

    /// <inheritdoc />
    public bool Unregister(string id)
    {
        if (!_bindings.TryRemove(id, out var existing))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && _messageHost.Handle != IntPtr.Zero)
        {
            _messageHost.Invoke(hwnd => UnregisterHotKey(hwnd, existing.AtomId));
        }

        return true;
    }

    /// <inheritdoc />
    public void UnregisterAll()
    {
        foreach (var key in _bindings.Keys.ToList())
        {
            Unregister(key);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, HotkeyBinding> GetActiveBindings()
        => new Dictionary<string, HotkeyBinding>(_bindings, StringComparer.OrdinalIgnoreCase);

    private IntPtr OnWin32Message(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        if (msg == WmHotkey)
        {
            var atomId = wParam.ToInt32();
            var match = _bindings.Values.FirstOrDefault(b => b.AtomId == atomId);
            if (match is not null)
            {
                match.Callback();
                handled = true;
                return IntPtr.Zero;
            }
        }

        handled = false;
        return IntPtr.Zero;
    }

    private static string FormatVirtualKey(uint vk) => vk switch
    {
        0x20 => "Space",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"VK_{vk:X2}"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _disposed = true;
        if (_handlerId is { } id)
        {
            _messageHost.RemoveMessageHandler(id);
            _handlerId = null;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
