<div align="center">

  <img src="src/Dial/Assets/dial.png" alt="Dial Logo" width="88" height="88" />

  <h1>Dial</h1>

  <p><strong>Lichtgewicht, modulaire Windows 10 & 11 internetradio-desktopapp gebouwd met WinUI 3, .NET 10 en Microsoft Fluent 2</strong></p>

  <p>
    <a href="https://github.com/projectmark/Dial/releases"><img src="https://img.shields.io/github/v/release/projectmark/Dial?include_prereleases&logo=github&label=version&color=1f6feb" alt="Version" /></a>
    <a href="https://github.com/projectmark/Dial/commits/master"><img src="https://img.shields.io/github/last-commit/projectmark/Dial?logo=github&label=updated&color=6a9955" alt="Last Updated" /></a>
    <a href="https://github.com/projectmark/Dial/releases"><img src="https://img.shields.io/github/downloads/projectmark/Dial/total?label=downloads&color=4c1" alt="Downloads" /></a>
  </p>
  <p>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4?logo=windows11&logoColor=white" alt="Platform Windows 10 | 11" />
    <img src="https://img.shields.io/badge/WinUI%203-.NET%2010%20%2B%20Fluent%202-512BD4?logo=dotnet&logoColor=white" alt="WinUI 3 .NET 10 + Fluent 2" />
    <img src="https://img.shields.io/badge/Arch-x64-0071C5?logo=intel&logoColor=white" alt="Arch x64" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue?logo=gnu&logoColor=white" alt="License GPLv3" /></a>
  </p>
  <p>
    <a href="https://github.com/projectmark/Dial/stargazers"><img src="https://img.shields.io/github/stars/projectmark/Dial?logo=github&label=stars&color=1f6feb" alt="Stars" /></a>
    <a href="https://github.com/projectmark/Dial/graphs/contributors"><img src="https://img.shields.io/github/contributors/projectmark/Dial?logo=github&label=contributors&color=c9510c" alt="Contributors" /></a>
  </p>

</div>

---

## Download

[![Download Latest Release](https://img.shields.io/badge/DOWNLOAD-LATEST%20RELEASE-6CC644?style=for-the-badge&labelColor=555555)](https://github.com/projectmark/Dial/releases)

- **Directe download (`v0.1.5-alpha` portable `.zip`):** [`Dial-v0.1.5-alpha-win-x64.zip`](https://github.com/projectmark/Dial/releases/download/v0.1.5-alpha/Dial-v0.1.5-alpha-win-x64.zip)
- **Installatie:** Pak het volledige `.zip`-archief uit naar een map naar keuze en start **`Dial.exe`** (er zijn geen extra runtimes of installaties nodig).

---

<img width="1233" height="885" alt="Dial Screenshot" src="https://github.com/user-attachments/assets/76fbdcbe-131a-41d2-a929-0b75caddc5f1" />

---

## Functies

### Stabiele audio-playout & instelbare buffer
- **Frame-nauwkeurige `MediaStreamSource`-engine**: Parseert live MP3 (`MPEG 1/2/2.5 Layer III`) en AAC (`ADTS` / `HE-AACp`) streams frame-voor-frame met ID3v2-skipping en dubbele sync-validatie.
- **Één schone HTTP-verbinding**: Inline Shoutcast/Icecast ICY-metadata (`icy-metaint`) wordt direct uit de audiostream gescheiden voor live *Now Playing*-titels, zonder een tweede streamverbinding te openen.
- **Gebruikersbuffer (2s – 30s)**: Stel onder **Instellingen → Radio** zelf in hoeveel seconden vooruit wordt gebufferd (`2s`, `5s` standaard, `10s`, `15s` of `30s`) met live bufferstatus in de speler (`Buffer X,X/Ys`), hysteresis-herstel bij onderbrekingen en automatische herverbinding op een schone frame-grens.
- **Fallback-ondersteuning**: Niet-MP3/AAC-streams en HLS/playlist-URL’s (`.m3u8`, `.pls`) vallen direct terug op Windows Media Foundation met aparte ICY-metadata uitlezing.
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
    │   ├── BufferedStreamProxy.cs       # Gedeelde gepoolde HttpClient, ICY-demuxer & circulaire audiobuffer
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
└── build-release.ps1           # Bouwt portable loose-file release (kleine Dial.exe + DLL's + .zip) met SHA256 & releases.json
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

### Portable loose-file release bouwen
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```
De gegenereerde portable build (`app\Dial.exe` als kleine launcher naast de losse runtime-DLL's) wordt inclusief een deelbaar `.zip`-pakket (`Dial-v<Versie>-win-x64.zip`), `checksum.sha256` en `releases.json` geplaatst in `releases/v<Versie>/` en `releases/latest/`.

> **Delen op andere Windows-pc's:** omdat `Dial.exe` niet digitaal ondertekend is met een betaald Code Signing-certificaat, zit er in de `.zip` een `LEESMIJ-STARTEN.txt` met uitpak-, SmartScreen- en SHA256-instructies. Pak altijd eerst de **volledige** `.zip` uit (zodat alle losse bestanden en DLL's naast `Dial.exe` blijven staan) en kies zo nodig *Eigenschappen → Blokkering opheffen* op het `.zip`-bestand vóór het uitpakken.

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

---

## Licentie

Dit project wordt uitgebracht onder de **[GNU General Public License v3.0 (GPLv3)](LICENSE)**.
