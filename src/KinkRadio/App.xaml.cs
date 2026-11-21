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
using KinkRadio.Modules.Radio;
using KinkRadio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.System;

namespace KinkRadio;

public sealed partial class App : Application
{
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

        try { (Services.GetService<IRadioPlayerService>() as IDisposable)?.Dispose(); } catch { }
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
        if (!settings.GetOption("shell.tray_initialized", false))
        {
            settings.Update(s => s.CloseToTrayEnabled = true, autoSave: false);
            settings.SetOption("radio.minimize_to_tray", true, autoSave: false);
            settings.SetOption("shell.tray_initialized", true, autoSave: true);
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
            Id: "help",
            TitleLocalizationKey: "Nav.Help",
            Glyph: "\uE897",
            ViewFactory: () => new HelpView(),
            AcceleratorKey: VirtualKey.Number2,
            Order: 1,
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
            Id: "kink-stations",
            CategoryKey: "KB.Category.Radio",
            TitleKey: "KB.Article.Stations.Title",
            SummaryKey: "KB.Article.Stations.Summary",
            BodyKey: "KB.Article.Stations.Body",
            BadgeText: "4 Zenders",
            CopyableReference: "https://kink.nl",
            Order: 1));

        kb.RegisterArticle(new KnowledgeArticle(
            Id: "tray-background",
            CategoryKey: "KB.Category.Operations",
            TitleKey: "KB.Article.Tray.Title",
            SummaryKey: "KB.Article.Tray.Summary",
            BodyKey: "KB.Article.Tray.Body",
            BadgeText: "Tray & GC",
            CopyableReference: "CollapseToTray() -> GC.Collect(1)",
            Order: 2));

        kb.RegisterArticle(new KnowledgeArticle(
            Id: "appdata-storage",
            CategoryKey: "KB.Category.Platform",
            TitleKey: "KB.Article.Storage.Title",
            SummaryKey: "KB.Article.Storage.Summary",
            BodyKey: "KB.Article.Storage.Body",
            BadgeText: AppVersion.ShortDisplayVersion,
            CopyableReference: Services.GetRequiredService<IPathService>().RootDirectory,
            Order: 3));

        var changelog = Services.GetRequiredService<IChangelogService>();
        changelog.RegisterRelease(new ChangelogRelease(
            Version: AppVersion.InformationalVersion,
            ReleaseDate: new DateTime(2026, 11, 21),
            Changes: new ChangelogChange[]
            {
                new ChangelogChange("Added", "Changelog.v010.AddedRadio"),
                new ChangelogChange("Added", "Changelog.v010.AddedTray"),
                new ChangelogChange("Added", "Changelog.v010.AddedIcySmtc")
            },
            IsLatest: true));
    }
}
