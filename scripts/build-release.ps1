param (
    [string]$Version = "",
    [string]$PreRelease = "",
    [string]$ProjectPath = "",
    [string]$DownloadBaseUrl = ""
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

# 1. Dynamische project-detectie indien geen expliciet pad is opgegeven
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $candidates = Get-ChildItem -Path (Join-Path $RootDir "src") -Recurse -Filter "*.csproj" | Where-Object {
        (Get-Content $_.FullName -Raw) -match "<OutputType>\s*WinExe\s*</OutputType>"
    }
    if (-not $candidates -or $candidates.Count -eq 0) {
        throw "Geen uitvoerbaar WinExe .csproj gevonden in '$RootDir\src'."
    }
    $CsprojFile = $candidates[0].FullName
} else {
    $CsprojFile = if ([System.IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $RootDir $ProjectPath }
    if ((Test-Path $CsprojFile) -and (Get-Item $CsprojFile).PSIsContainer) {
        $found = Get-ChildItem -Path $CsprojFile -Filter "*.csproj" | Select-Object -First 1
        if (-not $found) { throw "Geen .csproj gevonden in map: $CsprojFile" }
        $CsprojFile = $found.FullName
    }
}

$AppName = [System.IO.Path]::GetFileNameWithoutExtension($CsprojFile)
[xml]$CsprojXml = Get-Content $CsprojFile
$PropsPath = Join-Path $RootDir "Directory.Build.props"
[xml]$PropsXml = if (Test-Path $PropsPath) { Get-Content $PropsPath } else { $null }

# 2. Dynamische versieresolutie uit .csproj, Directory.Build.props of fallback naar SemVer standaard
if ([string]::IsNullOrWhiteSpace($Version)) {
    $xmlVer = ($CsprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($xmlVer) -and $PropsXml) {
        $xmlVer = ($PropsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
        if ($xmlVer -is [System.Xml.XmlElement]) { $xmlVer = $xmlVer.InnerText }
    }
    $Version = if (-not [string]::IsNullOrWhiteSpace($xmlVer)) { "$xmlVer".Trim() } else { "0.1.0" }
}
if ([string]::IsNullOrWhiteSpace($PreRelease)) {
    $infoVer = ($CsprojXml.Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($infoVer) -and $PropsXml) {
        $infoVer = ($PropsXml.Project.PropertyGroup.InformationalVersion | Where-Object { $_ } | Select-Object -First 1)
        if ($infoVer -is [System.Xml.XmlElement]) { $infoVer = $infoVer.InnerText }
    }
    if (-not [string]::IsNullOrWhiteSpace($infoVer) -and "$infoVer" -match "-(.+)$") {
        $PreRelease = $Matches[1].Trim()
    } else {
        $PreRelease = ""
    }
}

$FullInfoVersion = if ([string]::IsNullOrWhiteSpace($PreRelease)) { $Version } else { "$Version-$PreRelease" }

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Chassis Platform - Portable Loose-File Release" -ForegroundColor Cyan
Write-Host " Applicatie : $AppName" -ForegroundColor Cyan
Write-Host " Versie     : v$FullInfoVersion" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$ReleasesDir = Join-Path $RootDir "releases"
$VersionDir = Join-Path $ReleasesDir "v$FullInfoVersion"
$LatestDir = Join-Path $ReleasesDir "latest"
$TempPublishDir = Join-Path $RootDir "temp_publish"
$AppStageDir = Join-Path $TempPublishDir "app"

# 3. Sluit eventuele actieve instanties van de applicatie af
Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# 4. Bereid mappen voor
if (Test-Path $TempPublishDir) {
    Remove-Item -Recurse -Force $TempPublishDir
}
New-Item -ItemType Directory -Force -Path $AppStageDir | Out-Null
New-Item -ItemType Directory -Force -Path $VersionDir | Out-Null
New-Item -ItemType Directory -Force -Path $LatestDir | Out-Null

# 4b. Bouw AppxShim MSBuild task indien aanwezig en nodig
$appxShimProj = Join-Path $RootDir "tools\AppxShim\AppxShim.csproj"
if (Test-Path $appxShimProj) {
    $shimDll = Join-Path $RootDir "tools\AppxShim\bin\Release\net10.0\Microsoft.Build.AppxPackage.dll"
    if (-not (Test-Path $shimDll)) {
        Write-Host "Compileren van AppxShim MSBuild task..." -ForegroundColor Yellow
        & dotnet build $appxShimProj -c Release | Out-Null
    }
}

# 5. Publiceer losse bestanden met kleine launcher .exe (.NET 10 LTS + WinUI 3)
Write-Host "`n[1/4] Publiceren van losse bestanden + kleine $AppName.exe..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $CsprojFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", $AppStageDir,
    "/p:PublishSingleFile=false",
    "/p:IncludeNativeLibrariesForSelfExtract=false",
    "/p:EnableCompressionInSingleFile=false",
    "/p:PublishReadyToRun=false",
    "/p:DebugType=none",
    "/p:DebugSymbols=false",
    "/p:Version=$Version",
    "/p:InformationalVersion=$FullInfoVersion"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish mislukt met foutcode $LASTEXITCODE"
}

Get-ChildItem -Path $AppStageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

$sourceExe = Join-Path $AppStageDir "$AppName.exe"
if (-not (Test-Path $sourceExe)) {
    throw "Gepubliceerd uitvoerbaar bestand niet gevonden op: $sourceExe"
}

$exeBytes = (Get-Item $sourceExe).Length
$exeSizeKb = [math]::Round($exeBytes / 1KB, 1)
$fileCount = (Get-ChildItem -Path $AppStageDir -Recurse -File).Count
if ($exeBytes -ge 1MB) {
    Write-Warning "$AppName.exe is groter dan verwacht ($exeSizeKb KB) — controleer PublishSingleFile=false."
}
Write-Host "      -> $AppName.exe : $exeSizeKb KB | Totaal losse bestanden: $fileCount" -ForegroundColor Green

# 6. Bereken SHA256 Checksums, voeg instructies toe & bouw deelbare .zip
Write-Host "`n[2/4] Berekenen van SHA256 checksums & verpakken van .zip..." -ForegroundColor Yellow
$exeHash = (Get-FileHash -Path $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "      EXE SHA256 : $exeHash" -ForegroundColor Green

$stageChecksumLines = New-Object System.Collections.Generic.List[string]
$stageChecksumLines.Add("$exeHash *$AppName.exe")
foreach ($coreDll in @("$AppName.dll", "Chassis.Core.dll", "Chassis.UI.dll")) {
    $dllPath = Join-Path $AppStageDir $coreDll
    if (Test-Path $dllPath) {
        $dllHash = (Get-FileHash -Path $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $stageChecksumLines.Add("$dllHash *$coreDll")
    }
}

[System.IO.File]::WriteAllText(
    (Join-Path $AppStageDir "checksum.sha256"),
    (($stageChecksumLines -join "`n") + "`n"),
    [System.Text.UTF8Encoding]::new($false)
)

$readmeContent = @"
$AppName v$FullInfoVersion - Portable installatie & startinstructies (NL / EN)
================================================================================

[Nederlands]
1. Pak de VOLLEDIGE .zip eerst uit naar een gewone map (rechtermuisklik -> Alles uitpakken...).
   - Laat alle losse bestanden en DLL's samen in dezelfde map naast $AppName.exe staan.
   - Verplaats $AppName.exe niet los uit deze map en start de app niet rechtstreeks vanuit het .zip-venster.
2. Start daarna $AppName.exe.
3. Zie je een Windows SmartScreen-melding ("Windows heeft uw pc beschermd")?
   - Klik op "Meer informatie" -> "Toch uitvoeren".
   - Tip vooraf: klik vóór het uitpakken met rechts op het .zip-bestand -> Eigenschappen ->
     vink "Blokkering opheffen" aan -> Toepassen (of in PowerShell: Unblock-File .\$AppName-v$FullInfoVersion-win-x64.zip).
4. Optionele integriteitscontrole (PowerShell):
   Get-FileHash .\$AppName.exe -Algorithm SHA256
   Verwachte SHA256 ($AppName.exe): $exeHash

[English]
1. Extract the ENTIRE .zip archive to a normal folder first (Right-click -> Extract All...).
   - Keep all loose files and DLLs together in the same folder alongside $AppName.exe.
   - Do not move $AppName.exe out on its own and do not launch it directly from inside the zip preview window.
2. Double-click $AppName.exe to launch.
3. If Windows SmartScreen shows "Windows protected your PC":
   - Click "More info" -> "Run anyway".
   - Tip beforehand: right-click the .zip file -> Properties -> check "Unblock" -> Apply
     (or in PowerShell: Unblock-File .\$AppName-v$FullInfoVersion-win-x64.zip).
4. Optional integrity check (PowerShell):
   Get-FileHash .\$AppName.exe -Algorithm SHA256
   Expected SHA256 ($AppName.exe): $exeHash
"@
[System.IO.File]::WriteAllText(
    (Join-Path $AppStageDir "LEESMIJ-STARTEN.txt"),
    $readmeContent,
    [System.Text.UTF8Encoding]::new($true)
)

$ZipFileName = "$AppName-v$FullInfoVersion-win-x64.zip"
$TempZipPath = Join-Path $TempPublishDir $ZipFileName
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $TempZipPath) {
    Remove-Item -Force $TempZipPath
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $AppStageDir,
    $TempZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$zipSizeMb = [math]::Round((Get-Item $TempZipPath).Length / 1MB, 2)
$zipHash = (Get-FileHash -Path $TempZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "      ZIP        : $ZipFileName ($zipSizeMb MB)" -ForegroundColor Green
Write-Host "      ZIP SHA256 : $zipHash" -ForegroundColor Green

# 7. Genereer releases.json manifest (met GitHub Release URL indien RepositoryUrl of DownloadBaseUrl aanwezig is)
$repoUrl = ($CsprojXml.Project.PropertyGroup.RepositoryUrl | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($repoUrl) -and $PropsXml) {
    $repoUrl = ($PropsXml.Project.PropertyGroup.RepositoryUrl | Where-Object { $_ } | Select-Object -First 1)
    if ($repoUrl -is [System.Xml.XmlElement]) { $repoUrl = $repoUrl.InnerText }
}
if (-not [string]::IsNullOrWhiteSpace($repoUrl)) {
    $repoUrl = "$repoUrl".Trim().TrimEnd('/')
    if ($repoUrl.EndsWith(".git", [System.StringComparison]::OrdinalIgnoreCase)) {
        $repoUrl = $repoUrl.Substring(0, $repoUrl.Length - 4).TrimEnd('/')
    }
}

$resolvedDownloadUrl = if (-not [string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    "$($DownloadBaseUrl.TrimEnd('/'))/$ZipFileName"
} elseif (-not [string]::IsNullOrWhiteSpace($repoUrl)) {
    "$repoUrl/releases/download/v$FullInfoVersion/$ZipFileName"
} else {
    "releases/v$FullInfoVersion/$ZipFileName"
}

$resolvedNotesUrl = if (-not [string]::IsNullOrWhiteSpace($repoUrl)) {
    "$repoUrl/releases/tag/v$FullInfoVersion"
} else {
    ""
}

$releaseManifest = [ordered]@{
    application     = $AppName
    version         = $FullInfoVersion
    baseVersion     = $Version
    preRelease      = $PreRelease
    releaseDate     = (Get-Date -Format "yyyy-MM-dd")
    packageType     = "portable-zip"
    fileName        = $ZipFileName
    appBinary       = "$AppName.exe"
    exeSizeKb       = $exeSizeKb
    fileCount       = $fileCount
    sha256          = $zipHash
    exeSha256       = $exeHash
    zipFileName     = $ZipFileName
    zipFileSizeMb   = $zipSizeMb
    zipSha256       = $zipHash
    downloadUrl     = $resolvedDownloadUrl
    releaseNotesUrl = $resolvedNotesUrl
    mandatory       = $false
}

$manifestJson = $releaseManifest | ConvertTo-Json -Depth 5
$checksumText = "$zipHash *$ZipFileName`n$exeHash *app/$AppName.exe`n"

function Sync-ReleaseFolder {
    param([string]$TargetDir)

    foreach ($staleName in @("release.exe", "$AppName.exe", "releases.json", "checksum.sha256")) {
        $stalePath = Join-Path $TargetDir $staleName
        if (Test-Path $stalePath) { Remove-Item -Force $stalePath }
    }
    Get-ChildItem -Path $TargetDir -Filter "$AppName-v*-win-x64.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
    $targetAppDir = Join-Path $TargetDir "app"
    if (Test-Path $targetAppDir) {
        Remove-Item -Recurse -Force $targetAppDir
    }

    Copy-Item -Path $AppStageDir -Destination $targetAppDir -Recurse -Force
    Copy-Item -Path $TempZipPath -Destination (Join-Path $TargetDir $ZipFileName) -Force
    [System.IO.File]::WriteAllText((Join-Path $TargetDir "releases.json"), $manifestJson, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $TargetDir "checksum.sha256"), $checksumText, [System.Text.UTF8Encoding]::new($false))
}

# 8. Distribueer naar releases/v<Version>/ en releases/latest/
Write-Host "`n[3/4] Distribueren naar versie-map ($VersionDir)..." -ForegroundColor Yellow
Sync-ReleaseFolder -TargetDir $VersionDir

Write-Host "`n[4/4] Bijwerken van releases/latest/ map..." -ForegroundColor Yellow
Sync-ReleaseFolder -TargetDir $LatestDir

Remove-Item -Recurse -Force $TempPublishDir -ErrorAction SilentlyContinue

Write-Host "`n=============================================" -ForegroundColor Green
Write-Host " Release v$FullInfoVersion succesvol voltooid!" -ForegroundColor Green
Write-Host " App Map     : $LatestDir\app\" -ForegroundColor Cyan
Write-Host " App Exe     : $LatestDir\app\$AppName.exe ($exeSizeKb KB)" -ForegroundColor Cyan
Write-Host " Share Zip   : $LatestDir\$ZipFileName ($zipSizeMb MB)" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Green
