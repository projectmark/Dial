# Dial

Een lichtgewicht, modulaire Windows 11 internetradio-desktopapp gebouwd met **WinUI 3 (Windows App SDK)**, **.NET 10 (LTS)** en het **Microsoft Fluent 2 Design System** op basis van het **Chassis**-platform.

---
<img width="1233" height="885" alt="image" src="https://github.com/user-attachments/assets/76fbdcbe-131a-41d2-a929-0b75caddc5f1" />

---

## Functies

### Stabiele audio-playout & instelbare buffer
- **Frame-nauwkeurige `MediaStreamSource`-engine**: Parseert live MP3 (`MPEG 1/2/2.5 Layer III`) en AAC (`ADTS` / `HE-AACp`) streams frame-voor-frame met ID3v2-skipping en dubbele sync-validatie.
- **Één schone HTTP-verbinding**: Inline Shoutcast/Icecast ICY-metadata (`icy-metaint`) wordt direct uit de audiostream gescheiden voor live *Now Playing*-titels, zonder een tweede streamverbinding te openen.
- **Gebruikersbuffer (2s – 30s)**: Stel onder **Instellingen → Radio** zelf in hoeveel seconden vooruit wordt gebufferd (`2s`, `5s` standaard, `10s`, `15s` of `30s`) met live bufferstatus in de speler (`Buffer X,X/Ys`), hysteresis-herstel bij onderbrekingen en automatische herverbinding op een schone frame-grens.
- **Fallback-ondersteuning**: Niet-MP3/AAC-streams (zoals Ogg, Opus of FLAC) worden via een lokale loopback-bufferproxy (`127.0.0.1`) afgespeeld; HLS/playlist-URL’s (`.m3u8`, `.pls`) vallen direct terug op Windows Media Foundation.
- **Windows SMTC-integratie & nummer opzoeken**: Bediening en live track-info via Windows mediatoetsen en het vergrendelscherm, plus één-klik nummer opzoeken in **Spotify**, **YouTube Music**, **Deezer** of **Tidal** (instelbaar onder **Instellingen → Radio**).

### Favorieten, ontdekken & beheren
- **Radi-oh!-stijl favorietenlijst of 2×2 kaarten**: Instelbare weergave, positienummers (`01.`, `02.`), uitlijning en lettergrootte, met studiostijl **ON AIR**-statusbadge bij live streams.
- **Bewerkmodus met compositor-locked sticky werkbalk**: Eigen zender toevoegen, hernoemen, selectievakjes, `Shift+klik` bereikselectie, bulk verwijderen en multi-select drag-and-drop herordenen terwijl de spelerkaart natuurlijk uit beeld scrolt.
- **40.000+ wereldwijde zenders ontdekken**: Ingebouwde **Radio Browser**-zoekfunctie met land-, alfabet- en minimum bitratefilter (`64+` t/m `320 kbps`), direct voorbeluisteren en met één klik toevoegen aan favorieten.
- **Importeren & exporteren**: Ondersteuning voor Radi-oh! JSON-backups (incl. `customName` en sortering, met bevestigingsdialoog vóór het aanvullen), `.chassispack`-bestanden en compacte Brotli deelcodes.

### Mini-speler, VU-meter & achtergrondgebruik
- **Vaste mini-speler (`Ctrl+M`)**: Compact venster met *Altijd op voorgrond*-pin, volledige bedienings- en volumebalk en een soepele 60fps WASAPI stereo L/R VU-meter.
- **Slaaptimer & luisterstatistieken**: Slaaptimer tot 6 uur en per-zender luistertijdstatistieken (met energiezuinige timer die alleen loopt tijdens afspelen).
- **Systeemvak-modus**: Minimaliseren of sluiten naar het systeemvak geeft XAML-views vrij (`CollapseToTray()` + `GC.Collect(1)`) terwijl de radio op de achtergrond doorspeelt, compleet met zender-snelmenu.
- **Fluent 2 & toegankelijkheid**: `Mica Alt` vensterachtergrond, systeem- of eigen accentkleur met WCAG AA-contrast, Nederlands (`nl-NL`) en Engels (`en-US`) en instelbare globale sneltoetsen.

---

## Architectuur

Dial volgt de modulaire **Chassis**-architectuur zonder hardcoded applicatienamen, paden of versies:

```text
src/
├── Chassis.Core/               # 100% XAML-vrije core: AppVersion, PathService, SettingsService,
│                               # LocalizationService, JsonDataStore, PackageShareService, TrayService,
│                               # HotkeyService, ChangelogService, KnowledgeBaseService, UpdateService
├── Chassis.UI/                 # Fluent 2 shell-componenten: ThemeManager, ChassisTitleBar,
│                               # SettingsCard, ChassisFeedbackDialog, navigatie- & settings-contracten
└── Dial/                       # WinUI 3 host-applicatie
    ├── Modules/Radio/          # IChassisModule domeinlogica:
    │   ├── RadioModule.cs               # Service-registratie, traymenu, hotkeys & Radio-instellingen
    │   ├── RadioPlayerService.cs        # Playback-regie, favorieten, SMTC & streamherstel
    │   ├── AudioStreamFrameEngine.cs    # MP3/ADTS frame-parser & TimedAudioFrameQueue
    │   ├── BufferedFrameMediaSource.cs  # WinRT MediaStreamSource playout-engine
    │   ├── BufferedStreamProxy.cs       # Loopback HTTP-bufferproxy (fallback) & gedeelde HttpClient
    │   ├── IcyMetadataReader.cs         # ICY StreamTitle parser & fallback-lezer
    │   ├── AudioMeterService.cs         # WASAPI per-proces stereo peak meter (IDisposable)
    │   ├── RadioBrowserService.cs       # Radio Browser API client met mirror-failover
    │   ├── RadioBackupImporter.cs       # Radi-oh! JSON, .chassispack & deelcode importer
    │   ├── SleepTimerService.cs         # Slaaptimer (1 min – 6 uur)
    │   └── ListeningStatsService.cs     # Sessie- & zenderstatistieken (idle-safe timer)
    ├── Views/                  # HomeView, DiscoverView, MiniPlayerView, SettingsView, HelpView
    └── Assets/Locales/         # nl-NL.json & en-US.json
tests/
└── Chassis.Tests/              # xUnit .NET 10 testproject (zonder XAML/WinUI-afhankelijkheid)
scripts/
└── build-release.ps1           # Bouwt standalone single-file Dial.exe met SHA256 & releases.json
```

---

## Sneltoetsen

| Sneltoets | Actie |
| :--- | :--- |
| `Ctrl+1` / `Ctrl+2` / `Ctrl+9` | Navigeer naar Radio / Ontdekken / Instellingen |
| `F1` | Open Kennisbank & Changelog |
| `Ctrl+M` | Schakel tussen volledig venster en Mini-speler |
| `Ctrl+Alt+P` *(standaard, aanpasbaar)* | Afspelen / Stoppen |
| `Ctrl+Alt+N` / `Ctrl+Alt+B` *(standaard, aanpasbaar)* | Volgende / Vorige zender |
| `Ctrl+Alt+M` *(standaard, aanpasbaar)* | Geluid dempen aan/uit |
| `Ctrl+Shift+Space` *(standaard, aanpasbaar)* | Venster tonen / verbergen naar systeemvak |

> Alle globale sneltoetsen zijn onder **Instellingen → Radio** naar eigen wens in te stellen met elke combinatie van `Ctrl`, `Alt` en/of `Shift` + een letter (`A–Z`), cijfer (`0–9`) of `Space` (inclusief directe toetsopname).

---

## Bouwen, testen & release maken

### Vereisten
- Windows 10 (1809+) of Windows 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/)

### Unit-tests uitvoeren
```powershell
dotnet test tests/Chassis.Tests/Chassis.Tests.csproj -c Release
```

### Oplossing compileren
```powershell
dotnet build Dial.slnx -c Release
```

### Standalone single-file executable bouwen
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```
De gegenereerde standalone executable wordt inclusief `checksum.sha256` en `releases.json` geplaatst in `releases/v<Versie>/` en `releases/latest/Dial.exe`.

---

## Lokale opslag

Alle configuratie, favorieten, luisterstatistieken en crashlogs worden geïsoleerd en atomisch opgeslagen onder:
```text
%AppData%\Dial\
├── config\settings.json
├── data\stations.json
├── data\listening_stats.json
└── logs\
```
