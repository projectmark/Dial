using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Chassis.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Chassis.UI.Theme;

/// <summary>
/// Manages Fluent 2 materials (Mica Alt + DWM tabbed backdrop), 5 synchronized control states,
/// and WCAG AA HSL adaptive accent contrast without depending on C# resource lookups.
/// </summary>
public sealed class ThemeManager
{
    public static readonly string[] StandardAccentPresets =
    [
        "#0078D4",
        "#107C10",
        "#D83B01",
        "#8764B8",
        "#FF8C00",
        "#00B7C3"
    ];

    private sealed class InteractiveButtonState
    {
        public required Func<bool> IsSelected { get; init; }
        public bool IsHovered { get; set; }
        public bool IsPressed { get; set; }
    }

    private readonly ISettingsService _settings;
    private readonly UISettings _uiSettings = new();
    private readonly ConditionalWeakTable<Button, InteractiveButtonState> _buttonStates = new();
    private Window? _attachedWindow;
    private FrameworkElement? _rootElement;
    private bool _systemIsDark;

    public ThemeManager(ISettingsService settings)
    {
        _settings = settings;
        _systemIsDark = DetectSystemDarkTheme();
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
    }

    public bool IsDarkTheme => _settings.Current.Theme switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => _systemIsDark
    };

    public event Action? ThemeChanged;

    public void AttachWindow(Window window, FrameworkElement rootElement)
    {
        _attachedWindow = window;
        _rootElement = rootElement;
        ApplyToAttachedWindow();
    }

    public void RefreshFromSettings()
    {
        _systemIsDark = DetectSystemDarkTheme();
        ApplyToAttachedWindow();
        ThemeChanged?.Invoke();
    }

    public Color GetAdaptiveAccentColor()
    {
        var baseColor = ResolveRawAccentColor();
        return AdaptForWcagAa(baseColor, IsDarkTheme);
    }

    public SolidColorBrush GetAdaptiveAccentBrush() => new(GetAdaptiveAccentColor());

    public SolidColorBrush GetAdaptiveAccentTintBrush()
    {
        var c = GetAdaptiveAccentColor();
        return new SolidColorBrush(Color.FromArgb(IsDarkTheme ? (byte)0x26 : (byte)0x18, c.R, c.G, c.B));
    }

    public SolidColorBrush GetTextPrimaryBrush()
        => new(IsDarkTheme ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE4, 0x00, 0x00, 0x00));

    public SolidColorBrush GetTextSecondaryBrush()
        => new(IsDarkTheme ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x96, 0x00, 0x00, 0x00));

    public SolidColorBrush GetTextDisabledBrush()
        => new(IsDarkTheme ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0x00, 0x00, 0x00));

    public SolidColorBrush GetCardBackgroundBrush()
        => new(IsDarkTheme ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));

    public SolidColorBrush GetCardBorderBrush()
        => new(IsDarkTheme ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

    public SolidColorBrush GetSuccessBrush()
        => new(IsDarkTheme ? Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F) : Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F));

    public SolidColorBrush GetCautionBrush()
        => new(IsDarkTheme ? Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00) : Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));

    public SolidColorBrush GetCriticalBrush()
        => new(IsDarkTheme ? Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4) : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    /// <summary>
    /// Attaches 5-state Fluent 2 feedback without overwriting <see cref="FrameworkElement.Tag"/>.
    /// </summary>
    public void BindInteractiveButton(Button button, Func<bool> isSelectedPredicate)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(isSelectedPredicate);

        if (_buttonStates.TryGetValue(button, out _))
        {
            _buttonStates.Remove(button);
        }

        var state = new InteractiveButtonState { IsSelected = isSelectedPredicate };
        _buttonStates.Add(button, state);

        button.FontWeight = FontWeights.SemiBold;
        button.BorderThickness = new Thickness(1);

        button.PointerEntered += (_, _) =>
        {
            state.IsHovered = true;
            ApplyButtonState(button, state);
        };
        button.PointerExited += (_, _) =>
        {
            state.IsHovered = false;
            state.IsPressed = false;
            ApplyButtonState(button, state);
        };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            state.IsPressed = true;
            ApplyButtonState(button, state);
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            state.IsPressed = false;
            ApplyButtonState(button, state);
        }), handledEventsToo: true);
        button.IsEnabledChanged += (_, _) => ApplyButtonState(button, state);

        ApplyButtonState(button, state);
    }

    public void RefreshInteractiveButton(Button button)
    {
        if (button is not null && _buttonStates.TryGetValue(button, out var state))
        {
            ApplyButtonState(button, state);
        }
    }

    public static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            color = Color.FromArgb(0xFF, r, g, b);
            return true;
        }

        if (clean.Length == 8 &&
            byte.TryParse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r8) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g8) &&
            byte.TryParse(clean[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b8))
        {
            color = Color.FromArgb(a, r8, g8, b8);
            return true;
        }

        return false;
    }

    private void ApplyButtonState(Button button, InteractiveButtonState state)
    {
        button.FontWeight = FontWeights.SemiBold;
        button.BorderThickness = new Thickness(1);

        var restBg = IsDarkTheme ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF4, 0xFF, 0xFF, 0xFF);
        var hoverBg = IsDarkTheme ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA);
        var pressedBg = IsDarkTheme ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xDA, 0xDA, 0xDA);
        var disabledBg = IsDarkTheme ? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0xF4, 0xF4, 0xF4);

        var restBorder = IsDarkTheme ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        var restFg = IsDarkTheme ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
        var pressedFg = IsDarkTheme ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x96, 0x00, 0x00, 0x00);
        var disabledFg = IsDarkTheme ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0x00, 0x00, 0x00);

        var accent = GetAdaptiveAccentColor();
        var isSelected = state.IsSelected();

        var activeBorderColor = isSelected ? accent : restBorder;
        var activeRestFg = isSelected ? accent : restFg;

        button.Resources["ButtonBackground"] = new SolidColorBrush(restBg);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hoverBg);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressedBg);
        button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(disabledBg);

        button.Resources["ButtonBorderBrush"] = new SolidColorBrush(activeBorderColor);
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(activeBorderColor);
        button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(activeBorderColor);
        button.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(restBorder);

        button.Resources["ButtonForeground"] = new SolidColorBrush(activeRestFg);
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(activeRestFg);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(isSelected ? accent : pressedFg);
        button.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(disabledFg);

        if (!button.IsEnabled)
        {
            button.Background = new SolidColorBrush(disabledBg);
            button.BorderBrush = new SolidColorBrush(restBorder);
            button.Foreground = new SolidColorBrush(disabledFg);
        }
        else if (state.IsPressed)
        {
            button.Background = new SolidColorBrush(pressedBg);
            button.BorderBrush = new SolidColorBrush(activeBorderColor);
            button.Foreground = new SolidColorBrush(isSelected ? accent : pressedFg);
        }
        else if (state.IsHovered)
        {
            button.Background = new SolidColorBrush(hoverBg);
            button.BorderBrush = new SolidColorBrush(activeBorderColor);
            button.Foreground = new SolidColorBrush(activeRestFg);
        }
        else
        {
            button.Background = new SolidColorBrush(restBg);
            button.BorderBrush = new SolidColorBrush(activeBorderColor);
            button.Foreground = new SolidColorBrush(activeRestFg);
        }
    }

    private void OnSystemColorValuesChanged(UISettings sender, object args)
    {
        var previousDark = _systemIsDark;
        _systemIsDark = DetectSystemDarkTheme();

        if (_rootElement?.DispatcherQueue is { } dq)
        {
            dq.TryEnqueue(() =>
            {
                ApplyToAttachedWindow();
                if (previousDark != _systemIsDark || _settings.Current.UseSystemAccentColor)
                {
                    ThemeChanged?.Invoke();
                }
            });
        }
    }

    private void ApplyToAttachedWindow()
    {
        if (_rootElement is not null)
        {
            _rootElement.RequestedTheme = IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        }

        if (_attachedWindow is null)
        {
            return;
        }

        if (_attachedWindow.SystemBackdrop is not MicaBackdrop mica || mica.Kind != MicaKind.BaseAlt)
        {
            _attachedWindow.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }

        var hwnd = WindowNative.GetWindowHandle(_attachedWindow);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var darkFlag = IsDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref darkFlag, sizeof(int));

        var tabbedBackdrop = 4; // DWMSBT_TABBEDWINDOW (Mica Alt)
        _ = DwmSetWindowAttribute(hwnd, 38, ref tabbedBackdrop, sizeof(int));

        var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(winId);
        if (appWindow?.TitleBar is { } tb && AppWindowTitleBar.IsCustomizationSupported())
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;

            if (IsDarkTheme)
            {
                tb.ButtonForegroundColor = Colors.White;
                tb.ButtonHoverForegroundColor = Colors.White;
                tb.ButtonHoverBackgroundColor = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
                tb.ButtonPressedForegroundColor = Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
                tb.ButtonPressedBackgroundColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
                tb.ButtonInactiveForegroundColor = Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);
            }
            else
            {
                tb.ButtonForegroundColor = Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
                tb.ButtonHoverForegroundColor = Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
                tb.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
                tb.ButtonPressedForegroundColor = Color.FromArgb(0x96, 0x00, 0x00, 0x00);
                tb.ButtonPressedBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
                tb.ButtonInactiveForegroundColor = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            }
        }
    }

    private Color ResolveRawAccentColor()
    {
        if (_settings.Current.UseSystemAccentColor)
        {
            try
            {
                return _uiSettings.GetColorValue(UIColorType.Accent);
            }
            catch
            {
                // Fall back to preset/custom color below.
            }
        }

        return TryParseHexColor(_settings.Current.CustomAccentHex, out var custom)
            ? custom
            : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    }

    private bool DetectSystemDarkTheme()
    {
        try
        {
            var bg = _uiSettings.GetColorValue(UIColorType.Background);
            var luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return luminance < 0.5;
        }
        catch
        {
            return false;
        }
    }

    private static Color AdaptForWcagAa(Color color, bool isDarkTheme)
    {
        RgbToHsl(color, out var h, out var s, out var l);

        if (isDarkTheme)
        {
            l = Math.Clamp(l, 0.62, 0.80);
            s = Math.Min(s, 0.95);
        }
        else
        {
            l = Math.Clamp(l, 0.26, 0.38);
            s = Math.Max(s, 0.55);
        }

        return HslToRgb(color.A, h, s, l);
    }

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        l = (max + min) / 2.0;

        if (delta < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

        if (Math.Abs(max - r) < 1e-6)
        {
            h = ((g - b) / delta) + (g < b ? 6 : 0);
        }
        else if (Math.Abs(max - g) < 1e-6)
        {
            h = ((b - r) / delta) + 2;
        }
        else
        {
            h = ((r - g) / delta) + 4;
        }

        h /= 6.0;
    }

    private static Color HslToRgb(byte a, double h, double s, double l)
    {
        if (s < 1e-6)
        {
            var gray = (byte)Math.Round(l * 255.0);
            return Color.FromArgb(a, gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1.0 + s) : l + s - (l * s);
        var p = 2.0 * l - q;

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        var r = (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255.0);
        var g = (byte)Math.Round(HueToRgb(p, q, h) * 255.0);
        var b = (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255.0);
        return Color.FromArgb(a, r, g, b);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
