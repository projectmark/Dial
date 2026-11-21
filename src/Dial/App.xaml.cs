using Chassis.Core.Changelog;
using Chassis.Core.Diagnostics;
using Chassis.Core.Feedback;
using Chassis.Core.Hotkeys;
using Chassis.Core.Interop;
using Chassis.Core.KnowledgeBase;
using Chassis.Core.Localization;
using Chassis.Core.Modules;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Tray;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Navigation;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Dial.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Dial;

public sealed partial class App : Application
{
    private const string ShellTrayInitializedOptionKey = "shell.tray_initialized";

    private static readonly IReadOnlyList<IChassisModule> _modules = new IChassisModule[]
    {
        new RadioModule()
    };

    public static IServiceProvider Services { get; private set; } = default!;
    public static bool IsExiting { get; private set; }

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        InitializeExceptionHandlers();
        EnsureFirstRunTrayDefaults();
        InitializeTrayService();
        InitializeShellContent();

        foreach (var module in _modules)
        {
            module.Initialize(Services);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        var settings = Services.GetRequiredService<ISettingsService>();
        if (settings.Current.StartMinimized)
        {
            window.CollapseToTray(showNotification: false);
        }
        else
        {
            window.Activate();
        }
    }

    public static void OnExternalInstanceActivated()
    {
        MainWindow.Instance?.DispatcherQueue.TryEnqueue(() => MainWindow.Instance?.RestoreFromTrayOrActivate());
    }

    public static void RequestExit()
    {
        IsExiting = true;

        try { Services.GetService<ISleepTimerService>()?.Dispose(); } catch { }
        try { (Services.GetService<IRadioPlayerService>() as IDisposable)?.Dispose(); } catch { }
        try { Services.GetService<IAudioMeterService>()?.Dispose(); } catch { }
        try { Services.GetService<IListeningStatsService>()?.Dispose(); } catch { }
        try { Services.GetService<IRadioBrowserService>()?.Dispose(); } catch { }
        try { Services.GetService<IHotkeyService>()?.Dispose(); } catch { }
        try { Services.GetService<ITrayService>()?.Dispose(); } catch { }
        try { Services.GetService<Win32MessageHost>()?.Dispose(); } catch { }

        MainWindow.Instance?.Close();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPathService>(_ => new PathService());
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICrashReportService, CrashReportService>();
        services.AddSingleton<IFeedbackService, FeedbackService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddSingleton<IChangelogService, ChangelogService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddSingleton<Win32MessageHost>();
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();

        services.AddSingleton<INavigationRegistry, NavigationRegistry>();
        services.AddSingleton<ThemeManager>();

        foreach (var module in _modules)
        {
            module.RegisterServices(services);
        }
    }

    private static void EnsureFirstRunTrayDefaults()
    {
        var settings = Services.GetRequiredService<ISettingsService>();
        if (!settings.GetOption(ShellTrayInitializedOptionKey, false))
        {
            settings.Update(s => s.CloseToTrayEnabled = true, autoSave: false);
            settings.SetOption(RadioOptions.MinimizeToTray, true, autoSave: false);
            settings.SetOption(ShellTrayInitializedOptionKey, true, autoSave: true);
        }
    }

    private static void InitializeTrayService()
    {
        var localization = Services.GetRequiredService<ILocalizationService>();
        var tray = Services.GetRequiredService<ITrayService>();
        tray.Initialize(
            localization["Tray.Open"],
            localization["Tray.Exit"],
            () => MainWindow.Instance?.DispatcherQueue.TryEnqueue(() => MainWindow.Instance?.RestoreFromTrayOrActivate()),
            () => MainWindow.Instance?.DispatcherQueue.TryEnqueue(RequestExit));
    }

    private void InitializeExceptionHandlers()
    {
        var crashService = Services.GetRequiredService<ICrashReportService>();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                crashService.WriteCrashReport("AppDomain.UnhandledException", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crashService.WriteCrashReport("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        UnhandledException += (_, e) =>
        {
            if (e.Exception is not null)
            {
                crashService.WriteCrashReport("Application.UnhandledException", e.Exception);
            }
        };
    }

    private static void InitializeShellContent()
    {
        var nav = Services.GetRequiredService<INavigationRegistry>();
        nav.Register(new NavigationItemDescriptor(
            Id: "home",
            TitleLocalizationKey: "Nav.Radio",
            Glyph: "\uE7F6",
            ViewFactory: () => new HomeView(),
            AcceleratorKey: VirtualKey.Number1,
            Order: 0,
            ShowInNav: true));

        nav.Register(new NavigationItemDescriptor(
            Id: "discover",
            TitleLocalizationKey: "Nav.Discover",
            Glyph: "\uE721",
            ViewFactory: () => new DiscoverView(),
            AcceleratorKey: VirtualKey.Number2,
            Order: 1,
            ShowInNav: true));

        nav.Register(new NavigationItemDescriptor(
            Id: "help",
            TitleLocalizationKey: "Nav.Help",
            Glyph: "\uE897",
            ViewFactory: () => new HelpView(),
            AcceleratorKey: VirtualKey.Number3,
            Order: 2,
            ShowInNav: false));

        nav.Register(new NavigationItemDescriptor(
            Id: "settings",
            TitleLocalizationKey: "Nav.Settings",
            Glyph: "\uE713",
            ViewFactory: () => new SettingsView(),
            AcceleratorKey: VirtualKey.Number9,
            Order: 900,
            ShowInNav: true));

        var kb = Services.GetRequiredService<IKnowledgeBaseService>();
        kb.RegisterArticle(new KnowledgeArticle(
            Id: "dial-stations",
            CategoryKey: "KB.Category.Radio",
            TitleKey: "KB.Article.Stations.Title",
            SummaryKey: "KB.Article.Stations.Summary",
            BodyKey: "KB.Article.Stations.Body",
            BadgeText: "40k+ Streams",
            CopyableReference: "https://api.radio-browser.info",
            Order: 1));

        kb.RegisterArticle(new KnowledgeArticle(
            Id: "dial-timer-mini",
            CategoryKey: "KB.Category.Radio",
            TitleKey: "KB.Article.TimerMini.Title",
            SummaryKey: "KB.Article.TimerMini.Summary",
            BodyKey: "KB.Article.TimerMini.Body",
            BadgeText: "Ctrl+M",
            CopyableReference: "Ctrl+Alt+P / N / B / M",
            Order: 2));

        kb.RegisterArticle(new KnowledgeArticle(
            Id: "tray-background",
            CategoryKey: "KB.Category.Operations",
            TitleKey: "KB.Article.Tray.Title",
            SummaryKey: "KB.Article.Tray.Summary",
            BodyKey: "KB.Article.Tray.Body",
            BadgeText: "Tray & GC",
            CopyableReference: "CollapseToTray() -> GC.Collect(1)",
            Order: 3));

        kb.RegisterArticle(new KnowledgeArticle(
            Id: "appdata-storage",
            CategoryKey: "KB.Category.Platform",
            TitleKey: "KB.Article.Storage.Title",
            SummaryKey: "KB.Article.Storage.Summary",
            BodyKey: "KB.Article.Storage.Body",
            BadgeText: AppVersion.ShortDisplayVersion,
            CopyableReference: Services.GetRequiredService<IPathService>().RootDirectory,
            Order: 4));

        var changelog = Services.GetRequiredService<IChangelogService>();
        changelog.RegisterRelease(new ChangelogRelease(
            Version: AppVersion.InformationalVersion,
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v031.CustomizableHotkeys")
            },
            IsLatest: true));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.3.0",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Changed", "Changelog.v030.RefactorZeroHardcoding"),
                new ChangelogChange("Fixed", "Changelog.v030.SettingsAlignmentAndColorPicker"),
                new ChangelogChange("Added", "Changelog.v030.DocumentationReadme")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.9",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Changed", "Changelog.v029.FrameAccuratePlayoutEngine")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.8",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v028.BufferedStreamEngine")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.7",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Changed", "Changelog.v027.TrueStickyEditToolbar")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.6",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Changed", "Changelog.v026.StickyEditToolbar")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.5",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Changed", "Changelog.v025.MovedEditButton")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.4",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v024.AddedBulkSelectionAndDrag")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.3",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v023.AddedRadiOhImportAndSmoothing")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.2",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v022.AddedMiniVuAndControls")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.1",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Fixed", "Changelog.v021.FixedMiniPlayer")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.2.0",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v020.RebrandDial"),
                new ChangelogChange("Added", "Changelog.v020.AddedDiscover"),
                new ChangelogChange("Added", "Changelog.v020.AddedTimerStats"),
                new ChangelogChange("Added", "Changelog.v020.AddedMiniHotkeys")
            },
            IsLatest: false));

        changelog.RegisterRelease(new ChangelogRelease(
            Version: "0.1.0",
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v010.AddedRadio"),
                new ChangelogChange("Added", "Changelog.v010.AddedTray")
            },
            IsLatest: false));
    }
}
