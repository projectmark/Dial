# Dial Repository Guidelines & Rules

## 1. Project Overview & Blueprint Constraint

- **Project**: **Dial** — modular Windows 10/11 internet radio desktop app built on the **Chassis** platform using **WinUI 3 (Windows App SDK)**, **.NET 10 (LTS)**, and **Microsoft Fluent 2**.
- **License**: **GNU General Public License v3.0 (`GPL-3.0-only`)** — keep root `LICENSE`, `src/Dial/Dial.csproj` (`<PackageLicenseExpression>GPL-3.0-only</PackageLicenseExpression>`), and `README.md` aligned.
- **Single Master Blueprint (`REFERENCE_CHASSIS.md`)**:
  - `REFERENCE_CHASSIS.md` is the sole architectural and Fluent 2 reference blueprint (no `languages/` translation folders).
  - Must strictly remain under **46,080 bytes** and **800 lines** so AI tools can ingest it in a single `read_file` call.

---

## 2. Architecture, Low-Footprint & Modularity Rules

- **Zero-Hardcoding**: Never hardcode application names, mutex keys (`Chassis_SingleInstance_<EntryAssemblyName>`), `%AppData%` paths, HTTP `User-Agent`, or versions. Resolve metadata dynamically at runtime via `AppVersion`. Keep all UI text in `src/Dial/Assets/Locales/nl-NL.json` and `en-US.json` with identical keys (enforced by unit tests).
- **Zero-XAML Core & `IChassisModule`**: Keep `Chassis.Core` (`net10.0`) 100% free of XAML/WinUI dependencies. Implement domain features through `IChassisModule` (`RadioModule`).
- **Shared HTTP Pool & In-Memory Audio**: Reuse `AudioBufferSizing.SharedStreamHttpClient` across `BufferedFrameMediaSource`, `IcyMetadataReader`, and `RadioBrowserService` instead of creating separate `HttpClient` socket pools. Do **not** re-introduce local loopback TCP proxy servers (`TcpListener` / `System.Net.Sockets`).
- **Lazy Win32 & Safe Managed UI**: Keep `Win32MessageHost`, `TrayService`, and `HotkeyService` lazy so background STA threads and `HWND_MESSAGE` windows are only created when actually used. Keep `Chassis.UI` and `Dial` free of `/unsafe`, and never add foreign-thread input hooks (`AttachThreadInput` / `GetWindowThreadProcessId`).
- **Fluent 2 & Zero Layout Shift**: Adhere strictly to Layer 0 `Mica Alt`, Layer 1 `1px`-bordered cards (no `DropShadow`), Layer 2 `Acrylic`, 5 synchronized control states, `4px` spatial grid, `ThemeManager.BindInteractiveButton`, `AutomationProperties.Name` + `ToolTip` on icon-only buttons, and shared UI helpers in `OnAirBadgeVisuals`.

---

## 3. Antivirus Hygiene & Portable Loose-File Releases (MANDATORY)

- **Loose-File Portable Build (No Single-File Self-Extract)**:
  - Keep `PublishSingleFile=false`, `IncludeNativeLibrariesForSelfExtract=false`, `EnableCompressionInSingleFile=false`, `PublishReadyToRun=false`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`, and `SatelliteResourceLanguages=nl-NL;en-US` in `src/Dial/Dial.csproj`.
  - Release builds must omit `.pdb` symbols (`DebugType=none`, `DebugSymbols=false`) and prune unused language satellite folders so `Dial.exe` stays small (~435 KB) alongside Microsoft-signed runtime DLLs.
- **Read-Only Updater (No `%AppData%` Binary Droppers)**:
  - `UpdateService` must stay strictly read-only: check GitHub Releases / local `releases.json` metadata (`UpdateCheckFrequency.Manual` by default), verify SHA256 hashes, and open verified `https://github.com/...` release pages in the user's browser.
  - Never download or extract `.exe`/`.zip` payloads into `%AppData%`, and never generate or spawn hidden `cmd.exe`, `powershell.exe`, or `.cmd`/`.bat` scripts.
- **Release Pipeline**:
  - Build local portable releases via `.\scripts\build-release.ps1` into `releases/v<Version>/` and `releases/latest/` (`app/`, `Dial-v<Version>-win-x64.zip`, `releases.json`, `checksum.sha256`).
  - When bumping versions, update `Directory.Build.props`, `src/Dial/app.manifest`, `src/Dial/App.xaml.cs` changelog, `nl-NL.json` & `en-US.json`, and the direct download link in `README.md`.
  - Pushing a `v*` tag triggers `.github/workflows/release.yml` to build, test, and publish the GitHub Release automatically.

---

## 4. Verification & Git Discipline

- **Pre-Commit Verification**: Run `dotnet test tests/Chassis.Tests/Chassis.Tests.csproj -c Release` and `dotnet build Dial.slnx -c Release` before committing code changes; scan release artifacts with Windows Defender (`MpCmdRun.exe -Scan -ScanType 3`) after release builds.
- **Git & Commit Discipline**: Keep `releases/`, `temp_publish/`, `bin/`, `obj/`, and credential files ignored in `.gitignore`. Create clean Conventional Commits after every logical change or phase, and **push only on explicit user request**.
