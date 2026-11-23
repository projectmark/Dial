using System.Runtime.InteropServices;
using Chassis.Core.Changelog;
using Chassis.Core.Feedback;
using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.Core.Tray;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Dialogs;
using Chassis.UI.Navigation;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Dial.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Dial;

public sealed partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MinLogicalWidth = 420;
    private const int MinLogicalHeight = 480;
    private const int MiniClientLogicalWidth = 420;
    private const int MiniClientLogicalHeight = 208;
    private const int DefaultLogicalWidth = 860;
    private const int DefaultLogicalHeight = 660;
    private const nuint SubclassId = 1001;

    private readonly ILocalizationService _localization;
    private readonly ISettingsService _settings;
    private readonly IChangelogService _changelog;
    private readonly INavigationRegistry _navRegistry;
    private readonly ITrayService _tray;
    private readonly IUpdateService _updates;
    private readonly IRadioPlayerService _player;
    private readonly ThemeManager _theme;
    private readonly Dictionary<string, UIElement> _viewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _navButtons = [];

    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private SubclassProc? _subclassDelegate;
    private string _activeNavId = "home";
    private bool _compactNavLabels;
    private bool _isCollapsedToTray;
    private bool _trayNoticeShown;
    private bool _isMiniPlayerMode;
    private SizeInt32 _lastRestoredSize;
    private PointInt32 _lastRestoredPosition;
    private bool _hasRestoredBounds;
    private SizeInt32 _preMiniSize;
    private PointInt32 _preMiniPosition;
    private bool _preMiniMaximized;
    private SizeInt32 _miniLockedWindowSize;
    private bool _isInitializingBounds = true;
    private bool _startupFlowsCompleted;
    private bool _startupFlowsRunning;
    private bool _isDialogOpen;

    public static MainWindow? Instance { get; private set; }
    public IntPtr Hwnd => _hwnd;
    public bool IsMiniPlayerMode => _isMiniPlayerMode;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _changelog = App.Services.GetRequiredService<IChangelogService>();
        _navRegistry = App.Services.GetRequiredService<INavigationRegistry>();
        _tray = App.Services.GetRequiredService<ITrayService>();
        _updates = App.Services.GetRequiredService<IUpdateService>();
        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = AppVersion.ProductTitle;
        var assetStem = AppVersion.EntryAssemblyName.ToLowerInvariant();
        var windowIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", $"{assetStem}.ico");
        var titleLogoPath = Path.Combine(AppContext.BaseDirectory, "Assets", $"{assetStem}.png");

        if (_appWindow is not null)
        {
            _appWindow.Title = AppVersion.ProductTitle;
            if (File.Exists(windowIconPath))
            {
                _appWindow.SetIcon(windowIconPath);
            }

            RestoreWindowBounds();
            _appWindow.Changed += OnAppWindowChanged;
            _appWindow.Closing += OnAppWindowClosing;
            TitleBarControl.Configure(
                _appWindow,
                _theme,
                _changelog.HasNewVersion,
                _localization["TitleBar.FeedbackTooltip"],
                _localization["TitleBar.HelpTooltip"],
                _localization["TitleBar.VersionTooltip"]);
            TitleBarControl.SetFeedbackEnabled(false);
            TitleBarControl.SetLogoImage(titleLogoPath);
            var catchphrases = DialCatchphrases.All;
            var catchphraseIndex = Random.Shared.Next(catchphrases.Count);
            TitleBarControl.SetSubtitle(
                catchphrases[catchphraseIndex],
                () =>
                {
                    catchphraseIndex = (catchphraseIndex + 1) % catchphrases.Count;
                    return catchphrases[catchphraseIndex];
                });
        }

        _isInitializingBounds = false;
        _theme.AttachWindow(this, RootGrid);

        TitleBarControl.ChangelogRequested += (_, _) => OpenChangelog();
        TitleBarControl.HelpRequested += (_, _) => NavigateTo("help", openChangelog: false);
        TitleBarControl.FeedbackRequested += async (_, _) => await OpenFeedbackDialogAsync();

        _theme.ThemeChanged += OnThemeChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _changelog.UnreadStateChanged += OnChangelogUnreadChanged;
        _navRegistry.ItemsChanged += BuildNavigationBar;
        _player.StateChanged += OnPlayerStateChanged;

        RootGrid.SizeChanged += OnRootSizeChanged;
        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnWindowClosed;

        BuildNavigationBar();
        RegisterAccelerators();
        InstallMinSizeSubclass();
        ApplySurfaceBrushes();
        UpdateTitleBarLiveStatus(_player.Current);
        if (!_settings.Current.StartMinimized)
        {
            NavigateTo("home");
        }
    }

    public void NavigateTo(string navId, bool openChangelog = false)
    {
        if (_isMiniPlayerMode)
        {
            ToggleMiniPlayer();
        }

        var descriptor = _navRegistry.Find(navId);
        if (descriptor is null)
        {
            return;
        }

        _activeNavId = descriptor.Id;
        if (!_viewCache.TryGetValue(descriptor.Id, out var view))
        {
            view = descriptor.ViewFactory();
            _viewCache[descriptor.Id] = view;
        }

        ContentHost.Content = view;
        if (view is HelpView helpView)
        {
            helpView.SwitchToMode(openChangelog);
        }

        RefreshNavigationButtons();
    }

    public void OpenChangelog() => NavigateTo("help", openChangelog: true);

    public async Task OpenFeedbackDialogAsync()
    {
        if (RootGrid.XamlRoot is null || _isDialogOpen)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ChassisFeedbackDialog(
                App.Services.GetRequiredService<IFeedbackService>(),
                _localization,
                _theme,
                _hwnd)
            {
                XamlRoot = RootGrid.XamlRoot
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    public async Task ShowUpdatePromptAsync(UpdateCheckResult result)
    {
        if (RootGrid.XamlRoot is null ||
            _isDialogOpen ||
            !result.IsUpdateAvailable ||
            result.Manifest is null)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ChassisUpdateDialog(
                _updates,
                _localization,
                _theme,
                result,
                onBeforeRestart: targetVersion => _settings.Update(s => s.PendingWhatsNewVersion = targetVersion),
                onRequestExit: App.RequestExit)
            {
                XamlRoot = RootGrid.XamlRoot
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    public async Task ShowWhatsNewDialogAsync()
    {
        if (RootGrid.XamlRoot is null || _isDialogOpen)
        {
            return;
        }

        var releases = _changelog.GetReleases();
        var latestRelease = releases.FirstOrDefault(r => r.IsLatest) ?? releases.FirstOrDefault();
        if (latestRelease is null)
        {
            return;
        }

        _isDialogOpen = true;
        ContentDialogResult result;
        try
        {
            var dialog = new ChassisWhatsNewDialog(latestRelease, _localization, _theme)
            {
                XamlRoot = RootGrid.XamlRoot
            };

            result = await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
            _changelog.MarkCurrentAsViewed();
            if (!string.IsNullOrWhiteSpace(_settings.Current.PendingWhatsNewVersion))
            {
                _settings.Update(s => s.PendingWhatsNewVersion = null);
            }
        }

        if (result == ContentDialogResult.Secondary)
        {
            OpenChangelog();
        }
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
        => TriggerStartupFlowsIfPending();

    private void TriggerStartupFlowsIfPending()
    {
        if (_startupFlowsCompleted || _startupFlowsRunning || _isCollapsedToTray || App.IsExiting)
        {
            return;
        }

        _ = RunStartupUpdateAndWhatsNewFlowsAsync();
    }

    private async Task RunStartupUpdateAndWhatsNewFlowsAsync()
    {
        _startupFlowsRunning = true;
        try
        {
            await Task.Delay(1200);
            if (App.IsExiting || _isCollapsedToTray || RootGrid.XamlRoot is null)
            {
                return;
            }

            _startupFlowsCompleted = true;

            if (_updates.ShouldShowWhatsNew(_settings.Current))
            {
                await ShowWhatsNewDialogAsync();
                return;
            }

            if (_updates.ShouldAutoCheck(_settings.Current, DateTimeOffset.UtcNow) &&
                !string.IsNullOrWhiteSpace(AppVersion.GitHubRepositorySlug))
            {
                var result = await _updates.CheckForGitHubUpdatesAsync(
                    includePreReleases: _settings.Current.IncludePreReleases);
                _settings.Update(s => s.LastUpdateCheckUtc = DateTimeOffset.UtcNow);

                if (result.IsUpdateAvailable &&
                    result.Manifest is not null &&
                    !_isCollapsedToTray &&
                    !App.IsExiting)
                {
                    await ShowUpdatePromptAsync(result);
                }
            }
        }
        catch
        {
            // Background startup update checks are best-effort and never interrupt launch.
        }
        finally
        {
            _startupFlowsRunning = false;
        }
    }

    public void ToggleMiniPlayer()
    {
        if (_appWindow is null)
        {
            return;
        }

        var dpi = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96u;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        if (!_isMiniPlayerMode)
        {
            _preMiniMaximized = _appWindow.Presenter is OverlappedPresenter p &&
                                p.State == OverlappedPresenterState.Maximized;
            if (_preMiniMaximized && _appWindow.Presenter is OverlappedPresenter maxPresenter)
            {
                maxPresenter.Restore();
            }

            _preMiniSize = _hasRestoredBounds && _lastRestoredSize.Width > 0
                ? _lastRestoredSize
                : _appWindow.Size;
            _preMiniPosition = _hasRestoredBounds
                ? _lastRestoredPosition
                : _appWindow.Position;

            _isMiniPlayerMode = true;
            _miniLockedWindowSize = default;
            NavBarRow.Height = new GridLength(0);
            NavBarBorder.Visibility = Visibility.Collapsed;
            _viewCache.Clear();
            ContentHost.Content = new MiniPlayerView();

            if (_appWindow.Presenter is OverlappedPresenter miniPresenter)
            {
                miniPresenter.IsResizable = false;
                miniPresenter.IsMaximizable = false;
            }

            var miniClientW = (int)Math.Ceiling(MiniClientLogicalWidth * scale);
            var miniClientH = (int)Math.Ceiling(MiniClientLogicalHeight * scale);
            _appWindow.ResizeClient(new SizeInt32(miniClientW, miniClientH));

            var actualMiniSize = _appWindow.Size;
            _miniLockedWindowSize = actualMiniSize;

            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var targetX = Math.Clamp(_preMiniPosition.X, area.X, Math.Max(area.X, area.X + area.Width - actualMiniSize.Width));
            var targetY = Math.Clamp(_preMiniPosition.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - actualMiniSize.Height));

            _appWindow.Move(new PointInt32(targetX, targetY));
            ApplyMiniAlwaysOnTop();
        }
        else
        {
            _miniLockedWindowSize = default;
            _isMiniPlayerMode = false;
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = false;
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
            }

            NavBarRow.Height = new GridLength(52);
            NavBarBorder.Visibility = Visibility.Visible;

            if (_preMiniSize.Width > 0 && _preMiniSize.Height > 0)
            {
                _appWindow.MoveAndResize(new RectInt32(
                    _preMiniPosition.X,
                    _preMiniPosition.Y,
                    _preMiniSize.Width,
                    _preMiniSize.Height));
            }

            if (_preMiniMaximized && _appWindow.Presenter is OverlappedPresenter p)
            {
                p.Maximize();
            }

            NavigateTo(_activeNavId);
        }
    }

    public void ApplyMiniAlwaysOnTop()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _isMiniPlayerMode &&
                                      _settings.GetOption(RadioModule.MiniAlwaysOnTopOptionKey, true);
        }
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const int DwmwaCloak = 13;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNottopmost = new(-2);

    private int _restoreSerial;
    private EventHandler<object>? _pendingRevealRenderHandler;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pendingRevealTimer;

    public void ToggleVisibilityFromTray() => ToggleVisibility();

    public void ToggleVisibilityFromHotkey() => ToggleVisibility();

    private void ToggleVisibility()
    {
        if (IsWindowHiddenOrMinimized())
        {
            RestoreFromTrayOrActivate();
        }
        else
        {
            CollapseToTray(showNotification: false);
        }
    }

    private bool IsWindowHiddenOrMinimized()
    {
        if (_isCollapsedToTray || _appWindow is null || !_appWindow.IsVisible)
        {
            return true;
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            return true;
        }

        return _hwnd != IntPtr.Zero && IsIconic(_hwnd);
    }

    public void CollapseToTray(bool showNotification)
    {
        CancelPendingReveal();

        if (_isCollapsedToTray)
        {
            return;
        }

        _isCollapsedToTray = true;
        ContentHost.Content = null;
        _viewCache.Clear();
        _appWindow?.Hide();
        SetWindowCloaked(false);
        GC.Collect(1, GCCollectionMode.Forced, blocking: false);

        if (showNotification && _settings.Current.ShowTrayNotifications && !_trayNoticeShown)
        {
            _trayNoticeShown = true;
            _tray.ShowNotification(
                AppVersion.ProductTitle,
                _localization["Tray.MinimizedNotification"]);
        }
    }

    public void RestoreFromTrayOrActivate()
    {
        var wasHiddenOrMinimized = IsWindowHiddenOrMinimized();
        _isCollapsedToTray = false;
        CancelPendingReveal();

        // Populate the XAML view BEFORE revealing the HWND so the unloaded UI isn't rebuilt after Show().
        if (_isMiniPlayerMode)
        {
            if (ContentHost.Content is not MiniPlayerView)
            {
                ContentHost.Content = new MiniPlayerView();
            }

            ApplyMiniAlwaysOnTop();
        }
        else if (ContentHost.Content is null)
        {
            NavigateTo(_activeNavId);
        }

        if (!wasHiddenOrMinimized)
        {
            BringWindowToFrontOnTop();
            TriggerStartupFlowsIfPending();
            return;
        }

        // Cloak the window via DWM while WinUI renders its initial frame (titlebar + Mica Alt + view)
        // so Windows never paints an unrendered white HWND frame on restore.
        var cloaked = SetWindowCloaked(true);

        if (_appWindow is not null)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            _appWindow.Show();
        }

        if (!cloaked)
        {
            BringWindowToFrontOnTop();
            TriggerStartupFlowsIfPending();
            return;
        }

        var serial = ++_restoreSerial;
        var layoutCompleted = false;
        var postLayoutRenderTicks = 0;

        void CompleteReveal()
        {
            if (serial != _restoreSerial || _isCollapsedToTray)
            {
                return;
            }

            CancelPendingReveal();
            SetWindowCloaked(false);
            BringWindowToFrontOnTop();
            TriggerStartupFlowsIfPending();
        }

        // Wait until XAML finishes layout (Low priority) and submits 2 Composition frames before uncloaking.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (serial == _restoreSerial && !_isCollapsedToTray)
            {
                layoutCompleted = true;
            }
        });

        _pendingRevealRenderHandler = (_, _) =>
        {
            if (!layoutCompleted)
            {
                return;
            }

            postLayoutRenderTicks++;
            if (postLayoutRenderTicks >= 2)
            {
                CompleteReveal();
            }
        };
        CompositionTarget.Rendering += _pendingRevealRenderHandler;

        _pendingRevealTimer = DispatcherQueue.CreateTimer();
        _pendingRevealTimer.Interval = TimeSpan.FromMilliseconds(160);
        _pendingRevealTimer.IsRepeating = false;
        _pendingRevealTimer.Tick += (_, _) => CompleteReveal();
        _pendingRevealTimer.Start();
    }

    private void CancelPendingReveal()
    {
        _restoreSerial++;
        if (_pendingRevealRenderHandler is not null)
        {
            CompositionTarget.Rendering -= _pendingRevealRenderHandler;
            _pendingRevealRenderHandler = null;
        }

        if (_pendingRevealTimer is not null)
        {
            _pendingRevealTimer.Stop();
            _pendingRevealTimer = null;
        }
    }

    private bool SetWindowCloaked(bool cloak)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(_hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    private void BringWindowToFrontOnTop()
    {
        if (_hwnd == IntPtr.Zero)
        {
            Activate();
            return;
        }

        if (IsIconic(_hwnd))
        {
            ShowWindow(_hwnd, SwRestore);
        }
        else
        {
            ShowWindow(_hwnd, SwShow);
        }

        var keepAlwaysOnTop = _isMiniPlayerMode && _settings.GetOption(RadioModule.MiniAlwaysOnTopOptionKey, true);
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpShowWindow);
        if (!keepAlwaysOnTop)
        {
            SetWindowPos(_hwnd, HwndNottopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpShowWindow);
        }

        var fgHwnd = GetForegroundWindow();
        var fgThread = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0u;
        var currentThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != currentThread && AttachThreadInput(currentThread, fgThread, true);
        try
        {
            SetForegroundWindow(_hwnd);
            BringWindowToTop(_hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, fgThread, false);
            }
        }

        Activate();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!App.IsExiting && _settings.Current.CloseToTrayEnabled)
        {
            args.Cancel = true;
            CollapseToTray(showNotification: true);
        }
    }

    private void BuildNavigationBar()
    {
        LeftNavPanel.Children.Clear();
        RightNavPanel.Children.Clear();
        _navButtons.Clear();

        foreach (var item in _navRegistry.GetItems().Where(i => i.ShowInNav))
        {
            var button = CreateNavButton(item.Id, item.Glyph, _localization[item.TitleLocalizationKey]);
            button.Tag = item;
            button.Click += (_, _) => NavigateTo(item.Id);

            _theme.BindInteractiveButton(button, () => string.Equals(_activeNavId, item.Id, StringComparison.OrdinalIgnoreCase));
            _navButtons.Add(button);

            if (item.Order < 900)
            {
                LeftNavPanel.Children.Add(button);
            }
            else
            {
                RightNavPanel.Children.Add(button);
            }
        }

        UpdateResponsiveNavLabels();
    }

    private static Button CreateNavButton(string id, string glyph, string label)
    {
        var icon = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            Glyph = glyph,
            FontSize = 16
        };

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(icon);
        stack.Children.Add(text);

        var btn = new Button
        {
            Height = 36,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            Content = stack
        };

        AutomationProperties.SetName(btn, label);
        ToolTipService.SetToolTip(btn, label);
        return btn;
    }

    private static TextBlock? FindLabelTextBlock(Button button)
        => button.Content is StackPanel sp && sp.Children.Count > 1 ? sp.Children[1] as TextBlock : null;

    private void RefreshNavigationButtons()
    {
        foreach (var btn in _navButtons)
        {
            _theme.RefreshInteractiveButton(btn);
        }
    }

    private void RegisterAccelerators()
    {
        // Prevent WinUI from auto-generating an "F1" hover tooltip on child controls that lack their own tooltip.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        var f1 = new KeyboardAccelerator { Key = VirtualKey.F1 };
        f1.Invoked += (_, e) =>
        {
            NavigateTo("help");
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(f1);

        var mini = new KeyboardAccelerator
        {
            Modifiers = VirtualKeyModifiers.Control,
            Key = VirtualKey.M
        };
        mini.Invoked += (_, e) =>
        {
            ToggleMiniPlayer();
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(mini);

        foreach (var item in _navRegistry.GetItems())
        {
            if (item.AcceleratorKey == VirtualKey.None)
            {
                continue;
            }

            var accel = new KeyboardAccelerator
            {
                Modifiers = VirtualKeyModifiers.Control,
                Key = item.AcceleratorKey
            };
            var targetId = item.Id;
            accel.Invoked += (_, e) =>
            {
                NavigateTo(targetId);
                e.Handled = true;
            };
            RootGrid.KeyboardAccelerators.Add(accel);
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var shouldCompact = e.NewSize.Width < 660;
        if (shouldCompact != _compactNavLabels)
        {
            _compactNavLabels = shouldCompact;
            UpdateResponsiveNavLabels();
        }
    }

    private void UpdateResponsiveNavLabels()
    {
        var vis = _compactNavLabels ? Visibility.Collapsed : Visibility.Visible;
        foreach (var btn in _navButtons)
        {
            if (FindLabelTextBlock(btn) is { } tb)
            {
                tb.Visibility = vis;
            }
        }
    }

    private void OnLanguageChanged(string _)
    {
        TitleBarControl.UpdateLabels(
            _localization["TitleBar.FeedbackTooltip"],
            _localization["TitleBar.HelpTooltip"],
            _localization["TitleBar.VersionTooltip"]);

        _tray.UpdateLabels(_localization["Tray.Open"], _localization["Tray.Exit"]);

        foreach (var btn in _navButtons)
        {
            if (btn.Tag is NavigationItemDescriptor item && FindLabelTextBlock(btn) is { } tb)
            {
                var localized = _localization[item.TitleLocalizationKey];
                tb.Text = localized;
                AutomationProperties.SetName(btn, localized);
                ToolTipService.SetToolTip(btn, localized);
            }
        }
    }

    private void OnThemeChanged()
    {
        ApplySurfaceBrushes();
        TitleBarControl.ApplyThemeVisuals();
        UpdateTitleBarLiveStatus(_player.Current);
        RefreshNavigationButtons();
    }

    private void OnPlayerStateChanged(RadioStateSnapshot snapshot)
        => DispatcherQueue.TryEnqueue(() => UpdateTitleBarLiveStatus(snapshot));

    private void UpdateTitleBarLiveStatus(RadioStateSnapshot snapshot)
    {
        var status = DialCatchphrases.FormatLiveTitleBarStatus(snapshot);
        if (status is null)
        {
            TitleBarControl.SetLiveStatus(null, null);
            return;
        }

        var dotBrush = snapshot.State == RadioPlaybackState.Playing
            ? _theme.GetSuccessBrush()
            : _theme.GetCautionBrush();
        TitleBarControl.SetLiveStatus(status, dotBrush);
    }

    private void OnChangelogUnreadChanged(bool hasUnread)
        => TitleBarControl.SetUnreadChangelogState(hasUnread);

    private void ApplySurfaceBrushes()
        => NavBarBorder.BorderBrush = _theme.GetCardBorderBrush();

    private void InstallMinSizeSubclass()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _subclassDelegate = WindowSubclassProc;
        SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);
    }

    private IntPtr WindowSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            if (_isMiniPlayerMode && _miniLockedWindowSize.Width > 0 && _miniLockedWindowSize.Height > 0)
            {
                mmi.ptMinTrackSize.X = _miniLockedWindowSize.Width;
                mmi.ptMinTrackSize.Y = _miniLockedWindowSize.Height;
                mmi.ptMaxTrackSize.X = _miniLockedWindowSize.Width;
                mmi.ptMaxTrackSize.Y = _miniLockedWindowSize.Height;
            }
            else if (!_isMiniPlayerMode)
            {
                var dpi = GetDpiForWindow(hwnd);
                var scale = dpi > 0 ? dpi / 96.0 : 1.0;

                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinLogicalWidth * scale);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinLogicalHeight * scale);
            }

            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void RestoreWindowBounds()
    {
        if (_appWindow is null) return;

        var dpi = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96u;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        var primaryWorkArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var minW = Math.Min((int)Math.Ceiling(MinLogicalWidth * scale), primaryWorkArea.Width);
        var minH = Math.Min((int)Math.Ceiling(MinLogicalHeight * scale), primaryWorkArea.Height);

        var savedW = _settings.Current.WindowWidth;
        var savedH = _settings.Current.WindowHeight;
        var savedX = _settings.Current.WindowX;
        var savedY = _settings.Current.WindowY;

        int targetW;
        int targetH;
        int targetX;
        int targetY;

        if (savedW is > 0 && savedH is > 0)
        {
            targetW = Math.Clamp(savedW.Value, minW, primaryWorkArea.Width);
            targetH = Math.Clamp(savedH.Value, minH, primaryWorkArea.Height);

            if (savedX.HasValue && savedY.HasValue)
            {
                var targetWorkArea = DisplayArea.GetFromRect(
                    new RectInt32(savedX.Value, savedY.Value, targetW, targetH),
                    DisplayAreaFallback.Primary).WorkArea;
                targetW = Math.Clamp(savedW.Value, minW, targetWorkArea.Width);
                targetH = Math.Clamp(savedH.Value, minH, targetWorkArea.Height);
                targetX = Math.Clamp(savedX.Value, targetWorkArea.X, Math.Max(targetWorkArea.X, targetWorkArea.X + targetWorkArea.Width - targetW));
                targetY = Math.Clamp(savedY.Value, targetWorkArea.Y, Math.Max(targetWorkArea.Y, targetWorkArea.Y + targetWorkArea.Height - targetH));
            }
            else
            {
                targetX = primaryWorkArea.X + Math.Max(0, (primaryWorkArea.Width - targetW) / 2);
                targetY = primaryWorkArea.Y + Math.Max(0, (primaryWorkArea.Height - targetH) / 2);
            }
        }
        else
        {
            targetW = Math.Clamp((int)Math.Ceiling(DefaultLogicalWidth * scale), minW, primaryWorkArea.Width);
            targetH = Math.Clamp((int)Math.Ceiling(DefaultLogicalHeight * scale), minH, primaryWorkArea.Height);
            targetX = primaryWorkArea.X + Math.Max(0, (primaryWorkArea.Width - targetW) / 2);
            targetY = primaryWorkArea.Y + Math.Max(0, (primaryWorkArea.Height - targetH) / 2);
        }

        _lastRestoredSize = new SizeInt32(targetW, targetH);
        _lastRestoredPosition = new PointInt32(targetX, targetY);
        _hasRestoredBounds = true;

        _appWindow.MoveAndResize(new RectInt32(targetX, targetY, targetW, targetH));

        if (_settings.Current.IsWindowMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isInitializingBounds) return;
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange) return;

        if (sender.Presenter is OverlappedPresenter presenter)
        {
            if (!_isMiniPlayerMode && presenter.State == OverlappedPresenterState.Restored)
            {
                var size = sender.Size;
                if (size.Width > 0 && size.Height > 0)
                {
                    _lastRestoredSize = size;
                    _lastRestoredPosition = sender.Position;
                    _hasRestoredBounds = true;
                }
            }
            else if (args.DidPresenterChange &&
                     presenter.State == OverlappedPresenterState.Minimized &&
                     _settings.GetOption(RadioOptions.MinimizeToTray, true))
            {
                CollapseToTray(showNotification: false);
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        CancelPendingReveal();
        SetWindowCloaked(false);

        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow.Closing -= OnAppWindowClosing;

            var isMaximized = _isMiniPlayerMode
                ? _preMiniMaximized
                : _appWindow.Presenter is OverlappedPresenter presenter &&
                  presenter.State == OverlappedPresenterState.Maximized;

            if (_isMiniPlayerMode && _preMiniSize.Width > 0 && _preMiniSize.Height > 0)
            {
                _lastRestoredSize = _preMiniSize;
                _lastRestoredPosition = _preMiniPosition;
                _hasRestoredBounds = true;
            }
            else if (!isMaximized &&
                     _appWindow.Presenter is OverlappedPresenter p &&
                     p.State == OverlappedPresenterState.Restored &&
                     _appWindow.Size.Width > 0 &&
                     _appWindow.Size.Height > 0)
            {
                _lastRestoredSize = _appWindow.Size;
                _lastRestoredPosition = _appWindow.Position;
                _hasRestoredBounds = true;
            }

            _settings.Update(s =>
            {
                s.IsWindowMaximized = isMaximized;
                if (_hasRestoredBounds && _lastRestoredSize.Width > 0 && _lastRestoredSize.Height > 0)
                {
                    s.WindowWidth = _lastRestoredSize.Width;
                    s.WindowHeight = _lastRestoredSize.Height;
                    s.WindowX = _lastRestoredPosition.X;
                    s.WindowY = _lastRestoredPosition.Y;
                }
            });
        }

        _theme.ThemeChanged -= OnThemeChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _changelog.UnreadStateChanged -= OnChangelogUnreadChanged;
        _navRegistry.ItemsChanged -= BuildNavigationBar;
        _player.StateChanged -= OnPlayerStateChanged;

        if (_hwnd != IntPtr.Zero && _subclassDelegate is not null)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
            _subclassDelegate = null;
        }

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
