# Chassis Blueprint: Modular Windows 11 WinUI 3 Platform

> Token-efficient master blueprint for Windows 11 desktop apps using **WinUI 3 (Windows App SDK)**, **.NET 10 (LTS)**, and **Microsoft Fluent 2** (`https://fluent2.microsoft.design`).

## 1. Platform Contract (Core Invariants)
1. **Zero-Hardcoding:** Resolve app name, title, mutex key (`Chassis_SingleInstance_<EntryAssemblyName>`), `%AppData%\<AppName>\`, and version badges dynamically from `Assembly.GetEntryAssembly()` via `AppVersion`.
2. **XAML-Free Core & Modularity:** Keep `Chassis.Core` (`net10.0`) 100% XAML-free. Keep `Chassis.Core` and `Chassis.UI` domain-agnostic; mount app features exclusively via `IChassisModule` from the user's app spec.
3. **Standardized Quickstart Shell + Opt-In Capabilities:** Always provide Titlebar (+ clickable version badge), Home/Overview, Help (Knowledge Base & Changelog), Feedback, and Settings; enable `IDataStore<T>`, `IPackageShareService<T>`, `ITrayService`/`IHotkeyService`, and extra locales *only* when needed.
4. **Fluent 2 & Zero Layout Shift:** Layer 0 `Mica Alt`, Layer 1 translucent `1px`-bordered cards (**no `DropShadow`**), Layer 2 `Acrylic`. Maintain 5 synchronized states (`Rest`, `Hover`, `Pressed`, `Disabled`, `Selected`), WCAG AA adaptive accent contrast, `4px` grid, immutable `FontWeight="SemiBold"` and `BorderThickness="1"`, plus `AutomationProperties.Name`, `F1`, and `Ctrl+1..9`.
5. **Low-RAM Lifecycle:** Instantiate views lazily via `ViewFactory`; when tray mode is active, `CollapseToTray()` nulls `ContentHost.Content`, clears view cache, hides the window, and runs `GC.Collect(1)`.
6. **Mandatory Git & Commit Discipline:** Always ensure a Git repository exists (`git init` + `.gitignore` ignoring `bin/`, `obj/`, `releases/`, `temp_publish/`, `*.user`, `.vs/`) and create a clean commit after every logical change or phase (push only on request).

---

## 2. 3-Layer Architecture, Capabilities & Build Config

### 2.1 Solution Layers
| Layer | Project | TFM | Contents |
| :--- | :--- | :--- | :--- |
| **Core** | `Chassis.Core` | `net10.0` | `Versioning/` (`AppVersion`, `SemVersion`), `Paths/`, `Settings/`, `Diagnostics/`, `Feedback/`, `Localization/`, `KnowledgeBase/`, `Changelog/`, `Updates/`, `Modules/` (`IChassisModule`), plus opt-in `Data/` (`IDataStore<T>`, `IPackageShareService<T>`), `Interop/` (`Win32MessageHost`), `Hotkeys/`, `Tray/`. |
| **UI** | `Chassis.UI` | `net10.0-windows10.0.26100.0` | `Controls/` (`ChassisTitleBar`, `SettingsCard`), `Dialogs/` (`ChassisFeedbackDialog`), `Navigation/` (`INavigationRegistry`, `ISettingsCategoryProvider`), `Theme/` (`ThemeManager`), `Themes/Generic.xaml`. |
| **Host** | `<AppName>` | `net10.0-windows10.0.26100.0` | `Program.cs`, `app.manifest` (`PerMonitorV2`), `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `Assets/Locales/*.json`, `Views/{Home,Help,Settings}View`, and optional `Modules/<Feature>/`. |

### 2.2 Always-On vs. Opt-In Capabilities
| Capability | Mode | Rule |
| :--- | :--- | :--- |
| Titlebar (+ version badge), Settings, Help (Docs/Changelog), Feedback, Logs, Updates | **Always On** | Core quickstart shell present in every Chassis app. |
| `HomeView` Canvas | **App-Specific** | Show app dashboard/workspace; keep neutral on a bare quickstart. |
| `IDataStore<T>` (`JsonDataStore<T>`) | **Opt-In** | Use when app persists local collections under `data/`. |
| `ITrayService` + `IHotkeyService` + `Win32MessageHost` | **Opt-In** | Use for background/tray apps; otherwise hide tray controls in Settings. |
| `IPackageShareService<T>` (`.chassispack`, `<APP>-v1:...`) | **Opt-In** | Use only when sharing presets/bundles; never force share UI onto unrelated apps. |
| `ILocalizationService` (`Assets/Locales/*.json`) | **Extensible** | Auto-discovers bundled/disk `.json` locales (supports 1 or N languages). |

### 2.3 MSBuild (`Directory.Build.props` & `.csproj`)
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <Version Condition="'$(Version)' == ''">0.1.0</Version>
    <InformationalVersion Condition="'$(InformationalVersion)' == ''">$(Version)</InformationalVersion>
    <WindowsAppSDKVersion Condition="'$(WindowsAppSDKVersion)' == ''">1.6.250205002</WindowsAppSDKVersion>
    <WindowsSDKBuildToolsVersion Condition="'$(WindowsSDKBuildToolsVersion)' == ''">10.0.26100.1742</WindowsSDKBuildToolsVersion>
    <MicrosoftExtensionsVersion Condition="'$(MicrosoftExtensionsVersion)' == ''">10.0.0</MicrosoftExtensionsVersion>
    <AppxMSBuildToolsPath>$(MSBuildThisFileDirectory)tools\AppxShim\bin\Release\net10.0\</AppxMSBuildToolsPath>
  </PropertyGroup>
  <PropertyGroup Condition="Exists('$(AppxMSBuildToolsPath)Microsoft.Build.AppxPackage.dll')">
    <PriProjTaskAssembly>$(AppxMSBuildToolsPath)Microsoft.Build.Packaging.Pri.Tasks.dll</PriProjTaskAssembly>
    <AppxMSBuildTaskAssembly>$(AppxMSBuildToolsPath)Microsoft.Build.AppxPackage.dll</AppxMSBuildTaskAssembly>
  </PropertyGroup>
</Project>
```
- **`Chassis.Core.csproj`**: `net10.0` + `Microsoft.Extensions.DependencyInjection.Abstractions` `$(MicrosoftExtensionsVersion)`. Zero WinUI refs.
- **`Chassis.UI.csproj`**: `net10.0-windows10.0.26100.0`, `Platforms=x64`, `RuntimeIdentifiers=win-x64`, `UseWinUI=true`, `WinUISDKReferences=false`, `EnableMsixTooling=true`, `Microsoft.WindowsAppSDK` + `Microsoft.Windows.SDK.BuildTools` + `ProjectReference` to `Chassis.Core`.
- **`<AppName>.csproj` (`WinExe`)**: adds `TargetPlatformMinVersion=10.0.17763.0`, `ApplicationManifest=app.manifest`, `DefineConstants=DISABLE_XAML_GENERATED_MAIN`, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `PublishSingleFile=true`, `SelfContained=true`, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`, `Microsoft.Extensions.DependencyInjection`, `EmbeddedResource` (`Assets\Locales\*.json`), `Content` (`Assets\**\*`).
- **Rule:** Never set `WindowsAppSDKSelfContained` or `DISABLE_XAML_GENERATED_MAIN` in library `.csproj` files. If `tools/AppxShim/` exists in the solution, build it first (`dotnet build tools/AppxShim -c Release`).

---

## 3. Core Service Contracts (`Chassis.Core` & `Chassis.UI`)
```csharp
public interface IChassisModule { string ModuleId { get; } void RegisterServices(IServiceCollection s); void Initialize(IServiceProvider p); }
public interface IPathService { string RootDirectory, ConfigDirectory, DataDirectory, ExportsDirectory, LogsDirectory, FeedbackDirectory, PendingUpdatesDirectory, SettingsFilePath { get; } void EnsureDirectoriesExist(); }
public interface ISettingsService { AppSettings Current { get; } event Action<AppSettings>? SettingsChanged; void Save(); void Update(Action<AppSettings> mutate, bool autoSave = true); T GetOption<T>(string key, T defaultValue); void SetOption<T>(string key, T value, bool autoSave = true); }
public interface IDataStore<T> { Task<IReadOnlyList<T>> LoadAllAsync(string col, CancellationToken ct = default); Task SaveAllAsync(string col, IEnumerable<T> items, CancellationToken ct = default); Task UpsertAsync(string col, T item, Func<T, string> key, CancellationToken ct = default); Task DeleteAsync(string col, string keyVal, Func<T, string> key, CancellationToken ct = default); }
public interface IPackageShareService<T> { Task ExportToFileAsync(T payload, string path, CancellationToken ct = default); Task<T> ImportFromFileAsync(string path, CancellationToken ct = default); string ExportToShareCode(T payload); T ImportFromShareCode(string code); }
public interface IHotkeyService : IDisposable { bool Register(string id, HotkeyModifiers mods, uint vk, Action cb); bool Unregister(string id); void UnregisterAll(); IReadOnlyDictionary<string, HotkeyBinding> GetActiveBindings(); }
public interface ITrayService : IDisposable { void Initialize(string show, string exit, Action onRestore, Action onExit); void UpdateLabels(string show, string exit); void SetCustomMenuItems(IReadOnlyList<TrayMenuItemDescriptor> items); void ShowNotification(string title, string msg); }
public sealed record NavigationItemDescriptor(string Id, string TitleLocalizationKey, string Glyph, Func<UIElement> ViewFactory, VirtualKey AcceleratorKey = VirtualKey.None, int Order = 0, bool ShowInNav = true);
public sealed record SettingsCategoryDescriptor(string Id, string TitleLocalizationKey, Func<UIElement> SectionFactory, int Order = 100);
```
- **`ThemeManager`:** `StandardAccentPresets` (`#0078D4`, `#107C10`, `#D83B01`, `#8764B8`, `#FF8C00`, `#00B7C3`), `IsDarkTheme`, `ThemeChanged`, `AttachWindow(window, root)`, `RefreshFromSettings()`, `GetAdaptiveAccent{Color,Brush,TintBrush}()`, `GetText{Primary,Secondary,Disabled}Brush()`, `GetCard{Background,Border}Brush()`, `Get{Success,Caution,Critical}Brush()`, `BindInteractiveButton(btn, Func<bool> isSelected)`, `RefreshInteractiveButton(btn)`. **Never read `Application.Current.Resources[...]` in C# or overwrite `btn.Tag`.**
- **`IUpdateService`:** `CheckForUpdatesAsync(manifestPathOrUrl, ct)` returns `UpdateCheckResult(IsUpdateAvailable, CurrentVersion, LatestVersion, Manifest, ErrorMessage)`. `VerifyPayloadHash(path, sha256)` + `ApplyUpdateAndRestart(stagedExe, sha256)` via `pending\apply_update.cmd` waiting on `Environment.ProcessId`.
- **`LocalizationService`:** Auto-discovers `Assets/Locales/*.json` + embedded `.json` + `%AppData%\<App>\config\locales\<culture>.json`; falls back cleanly across discovered cultures or keys.

---

## 4. Fluent 2 Visual Tokens, Layout & Breakpoints

### 4.1 3 Material Layers & `App.xaml` `ThemeDictionaries`
- **Layer 0 (Canvas):** `MicaBackdrop { Kind = MicaKind.BaseAlt }` + `DwmSetWindowAttribute(hwnd, 38, DWMSBT_TABBEDWINDOW=4)` + `20` dark mode; root Grid `Background="Transparent"`.
- **Layer 1 (Surface):** Cards/rows use `CardBackgroundFillColorDefaultBrush` + `1px` `CardStrokeColorDefaultBrush`, `CornerRadius="8"`, **no `DropShadow`**.
- **Layer 2 (Overlay):** `Flyout`, `ContentDialog` use Acrylic + `1px` border.

Merge `<XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />` and define identical **`Default`** and **`Dark`** dictionaries plus **`Light`**:

| Resource Key (`SolidColorBrush`) | `Default` / `Dark` | `Light` | Fluent 2 Role / State |
| :--- | :--- | :--- | :--- |
| `CardBackgroundFillColorDefaultBrush` | `#18FFFFFF` | `#C8FFFFFF` | Layer 1 Card fill |
| `CardStrokeColorDefaultBrush` | `#28FFFFFF` | `#14000000` | Layer 1 `1px` border |
| `ButtonBackground` / `PointerOver` | `#1FFFFFFF` / `#2EFFFFFF` | `#F4FFFFFF` / `#EAEAEA` | State 1 Rest / State 2 Gray Hover |
| `ButtonBackgroundPressed` / `Disabled` | `#12FFFFFF` / `#0DFFFFFF` | `#DADADA` / `#70F4F4F4` | State 3 Pressed / State 4 Disabled |
| `ButtonBorderBrush` | `#2EFFFFFF` | `#1A000000` | Rest/Hover `1px` border |
| `ButtonForeground` / `PointerOver` | `#FFFFFFFF` | `#E4000000` | Text/Icon Rest & Hover |
| `ButtonForegroundPressed` / `Disabled` | `#C8FFFFFF` / `#5DFFFFFF` | `#96000000` / `#5C000000` | Text/Icon Pressed / Disabled |
| `TextFillColorPrimaryBrush` / `Secondary` / `Disabled` | `#FFFFFFFF` / `#C8FFFFFF` / `#5DFFFFFF` | `#E4000000` / `#96000000` / `#5C000000` | Typography ramp |
| `SystemFillColorSuccessBrush` / `Caution` / `Critical` | `#6CCB5F` / `#FCE100` / `#FF99A4` | `#0F7B0F` / `#9D5D00` / `#C42B1C` | Semantic badges & dots |

- **State 5 (Selected):** Keep Rest fill, switch `BorderBrush` & `Foreground` to adaptive accent; add `3px` vertical accent bar on list rows.
- **HSL Accent Contrast (WCAG AA 4.5:1+):** Dark mode `L in [0.62, 0.80]`, `S <= 0.95`; Light mode `L in [0.26, 0.38]`, `S >= 0.55`. Two custom slots (`PersonalAccentPreset1Hex`, `PersonalAccentPreset2Hex`, render only non-empty) + `(+)` `ColorPicker` (`IsAlphaEnabled="False"`).

### 4.2 Type, Icons, Geometry & Breakpoints
- **Fonts:** `Segoe UI Variable {Small, Text, Display}` (`Caption 12/400`, `Body 14/400`, `BodyStrong 14-15/600`, `Subtitle 20/600`, `Title 24-28/600`); `Cascadia Mono, Consolas` (`12/400`) for code/paths.
- **Icons:** Exclusively `FontIcon` (`Segoe Fluent Icons`, `16px` cards/nav, `14px` titlebar; prefer crisp glyphs like `\uE80F`, `\uE713`, `\uE897`, `\uED15`, `\uE73E`, `\uE739` — avoid ambiguous dot glyphs like `\uE915`).
- **Grid & Radii:** `4px` spacing (`24px` page padding, `8-12px` gaps); `8px` cards/dialogs, `4-6px` buttons/inputs, `50%` pills/swatches; `150-250ms` transitions gated by `AnimationsEnabled`.

| Area | Layout & Breakpoints |
| :--- | :--- |
| `MainWindow` | Min `420 x 480 px` (or `1280 x 720 px` for dense master-detail desktop workspaces) scaled by `GetDpiForWindow(hwnd)/96.0` via `WM_GETMINMAXINFO`; persist `Window{Width,Height,X,Y}` & `IsWindowMaximized` in `AppSettings` clamped to `DisplayArea.WorkArea` |
| `ChassisTitleBar` (`40px`) | `[Logo 22px + Title + VersionBadge(24px->Changelog)] [Drag(*)] [Feedback(\uED15) \| Help(\uE897 + unread dot)] [140px margin]`; shortens badge `< 480px` |
| Top Nav (`52px`) | Left tabs (`Order < 900`), right `Settings` (`Order >= 900`), `h=36, p=12,6, r=6`; hides labels `< 660px`; set `ShowInNav: false` when accessible only via Titlebar/hotkeys (e.g. `Help`) |
| `SettingsCard` | `r=8, p=20,16`; stacks `ActionContent` to row 1 `< 600px` |
| Page Views (`*`) | Shell views (`Home`/`Help`/`Settings`) centered at `MaxWidth="1000"`; master-detail module workspaces may use `MaxWidth="1600-1920"` with responsive column collapse; `HomeView` 2x2 stacks `< 640px`, actions `< 580px`; `HelpView` index `300 -> 220px < 760px`; `Settings` pills -> `ComboBox < 720px` |

---

## 5. Quickstart Screens & Win32 Lifecycle
- **`HomeView`:** Centered `MaxWidth="1000"` canvas (`AppVersion.ProductTitle`, `ShortDisplayVersion`). Present the app's primary dashboard; keep bare quickstarts focused on platform readiness and quick navigation to Help/Settings without invented business features.
- **`HelpView`:** Header + `Documentation`/`Changelog` pills + search & category `ComboBox` + split viewport with **two independent `ScrollViewer`s** (left index with `3px` accent bar; right detail + copyable references). Changelog draws `2px` timeline with `IsLatest` marker, localized `[Added/Changed/Fixed]`, and calls `MarkCurrentAsViewed()` on open.
- **`SettingsView`:** Categories (`All`, `Appearance`, optional `Background & Hotkeys`, `Language`, `Storage & Updates` + `ISettingsCategoryProvider`); Theme, System Accent, Swatches+`ColorPicker`, Tray/Hotkey toggles (when opt-in enabled), Language switcher (pre-populated from `SupportedLanguages`, hide if `< 2`), `%AppData%`/Logs open, SHA256 Update check.
- **`ChassisFeedbackDialog`:** Layer 2 `ContentDialog` (`r=8`) saving JSON diagnostics + optional crash log + `FileOpenPicker` (`InitializeWithWindow`) to `%AppData%\<App>\feedback\`.
- **`Win32MessageHost` & Tray (Opt-In):** Hidden `HWND_MESSAGE` (`-3`) STA loop handling `WM_HOTKEY` and `Shell_NotifyIconW`. `CollapseToTray()` clears UI trees; `RequestExit()` sets `IsExiting = true` and disposes Win32 hosts.
- **Single-Instance `Program.cs`:**
```csharp
[STAThread]
static void Main(string[] args)
{
    WinRT.ComWrappersSupport.InitializeComWrappers();
    var main = AppInstance.FindOrRegisterForKey($"Chassis_SingleInstance_{AppVersion.EntryAssemblyName}");
    if (!main.IsCurrent) { main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().GetAwaiter().GetResult(); return; }
    main.Activated += (_, _) => App.OnExternalInstanceActivated();
    Application.Start(p => { SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())); new App(); });
}
```

---

## 6. Storage, Logs & Updates
- **`%AppData%\<EntryAssemblyName>\`:** `config\settings.json`, `data\*.json`, `exports\`, `logs\crash_*.log` (keep newest 20 by `CreationTimeUtc`), `feedback\`, `pending\`.
- **`ISettingsService` & `IDataStore<T>`:** Atomic writes via temp file + `File.Move(tmp, target, overwrite: true)`. Never store entity collections or plaintext secrets/tokens inside `settings.json`; encrypt user tokens via Windows DPAPI (`DataProtectionScope.CurrentUser`) and exclude `*.pat` / credential bins in `.gitignore`.

---

## 7. Mandatory WinUI 3 / .NET 10 AI Guards
1. **`SettingsCard` Base Class:** `Border` is `sealed` in WinUI 3 — inherit `UserControl` wrapping an inner `Border`.
2. **`ComboBox` i18n Guard:** Never call `Items.Clear()` during runtime language switches (`COMException 0x80070490`). Pre-populate once (e.g. from `SupportedLanguages` or XAML) and mutate `.Content` behind `_isUpdating`.
3. **`ScrollViewer` Centering:** `<ScrollViewer HorizontalScrollBarVisibility="Disabled"><Grid Padding="24"><StackPanel MaxWidth="1000" HorizontalAlignment="Stretch">`.
4. **Window Helper & Title:** Name static instance `MainWindow.Instance` (`CS0108`). Set both `Title` and `_appWindow.Title`.
5. **Lambda Discard & `Tag` Trap:** Use `p => { ... new App(); }` in `Program.cs` (`CS0029`); never overwrite `button.Tag` on buttons bound with `ThemeManager.BindInteractiveButton`.
6. **Zero-Hardcoding Badges & Nullable Swatches:** Pass `AppVersion.ShortDisplayVersion` to KB/Changelog items; skip empty custom accent slots when building swatch lists (`CS8604`).
7. **Domain Neutrality Guard:** Never invent unrequested product domains (checklists, trackers, share-code toolbars) when scaffolding a quickstart shell.
8. **Input Visual-Tree & Control Sync Guards:** Never tear down/reparent active visual-tree containers mid-event inside live `PointerMoved` drag handlers; bind action buttons via `ThemeManager.BindInteractiveButton(...)` (or refresh on `ThemeChanged`), and set `AutomationProperties.Name` + `ToolTipService.SetToolTip` on all icon-only buttons.

---

## 8. 5-Phase Execution & Release Protocol
0. **Phase 0 — Scope Gate & Git Init:** Ensure the repo is initialized (`git init` + `.gitignore` + baseline commit if empty), inspect the user's prompt, and activate only matching Opt-In Capabilities (Section 2.2). Keep bare quickstarts domain-neutral.
1. **Phase 1 — Foundation:** Build `tools/AppxShim` (if present), scaffold `Directory.Build.props`, `.slnx`, `src/Chassis.Core` (0% XAML), verify `dotnet build src/Chassis.Core -c Release`.
2. **Phase 2 — Quickstart Shell:** Build `Chassis.UI` (`ThemeManager`, `ChassisTitleBar`, `SettingsCard`, `ChassisFeedbackDialog`, registries) and Host shell (`Program.cs`, `App.xaml(.cs)`, `MainWindow`, `HomeView`, `HelpView`, `SettingsView`, `Assets/Locales/*.json`).
3. **Phase 3 — Domain (`IChassisModule`):** Implement only user-specified modules and localizations (skip on bare quickstarts).
4. **Phase 4 — Verify, Release & Commit:** Add xUnit tests in `tests/Chassis.Tests`, verify `dotnet build` (`0 Warnings, 0 Errors`) + `dotnet test -c Release`, run `powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1`, smoke-test launch, and commit all changes (`git add -A && git commit -m "..."`).
