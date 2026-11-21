param (
    [string]$Version = "",
    [string]$PreRelease = "",
    [string]$ProjectPath = "",
    [string]$ReleaseExeName = "release.exe"
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
Write-Host " Chassis Platform - Dynamisch Release Proces" -ForegroundColor Cyan
Write-Host " Applicatie : $AppName" -ForegroundColor Cyan
Write-Host " Versie     : v$FullInfoVersion" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$ReleasesDir = Join-Path $RootDir "releases"
$VersionDir = Join-Path $ReleasesDir "v$FullInfoVersion"
$LatestDir = Join-Path $ReleasesDir "latest"
$TempPublishDir = Join-Path $RootDir "temp_publish"

# 3. Sluit eventuele actieve instanties van de applicatie of release binary af
$procNames = @($AppName, [System.IO.Path]::GetFileNameWithoutExtension($ReleaseExeName)) | Select-Object -Unique
foreach ($pName in $procNames) {
    Get-Process -Name $pName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 400

# 4. Bereid mappen voor
if (Test-Path $TempPublishDir) {
    Remove-Item -Recurse -Force $TempPublishDir
}
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

# 5. Publiceer Single-File Self-Contained Executable
Write-Host "`n[1/4] Publiceren van Single-File WinUI 3 .exe (.NET 10 LTS)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $CsprojFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", $TempPublishDir,
    "/p:Version=$Version",
    "/p:InformationalVersion=$FullInfoVersion"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish mislukt met foutcode $LASTEXITCODE"
}

$sourceExe = Join-Path $TempPublishDir "$AppName.exe"
if (-not (Test-Path $sourceExe)) {
    throw "Gepubliceerd uitvoerbaar bestand niet gevonden op: $sourceExe"
}

$exeSizeMb = [math]::Round((Get-Item $sourceExe).Length / 1MB, 2)
Write-Host "      -> Succesvol gegenereerd: $exeSizeMb MB" -ForegroundColor Green

# 6. Bereken SHA256 Checksum
Write-Host "`n[2/4] Berekenen van SHA256 checksum..." -ForegroundColor Yellow
$hash = (Get-FileHash -Path $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "      SHA256: $hash" -ForegroundColor Green

# 7. Genereer releases.json manifest
$releaseManifest = [ordered]@{
    application  = $AppName
    version      = $FullInfoVersion
    baseVersion  = $Version
    preRelease   = $PreRelease
    releaseDate  = (Get-Date -Format "yyyy-MM-dd")
    fileName     = $ReleaseExeName
    appBinary    = "$AppName.exe"
    fileSizeMb   = $exeSizeMb
    sha256       = $hash
    downloadUrl  = "releases/v$FullInfoVersion/$ReleaseExeName"
    mandatory    = $false
}

$manifestJson = $releaseManifest | ConvertTo-Json -Depth 5

# 8. Distribueer naar releases/v<Version>/ en releases/latest/
Write-Host "`n[3/4] Distribueren naar versie-map ($VersionDir)..." -ForegroundColor Yellow
Copy-Item $sourceExe -Destination (Join-Path $VersionDir "$AppName.exe") -Force
Copy-Item $sourceExe -Destination (Join-Path $VersionDir $ReleaseExeName) -Force
$manifestJson | Set-Content (Join-Path $VersionDir "releases.json") -Encoding utf8
"$hash *$ReleaseExeName`n$hash *$AppName.exe" | Set-Content (Join-Path $VersionDir "checksum.sha256") -Encoding utf8

Write-Host "`n[4/4] Bijwerken van releases/latest/ map..." -ForegroundColor Yellow
Copy-Item $sourceExe -Destination (Join-Path $LatestDir "$AppName.exe") -Force
Copy-Item $sourceExe -Destination (Join-Path $LatestDir $ReleaseExeName) -Force
$manifestJson | Set-Content (Join-Path $LatestDir "releases.json") -Encoding utf8
"$hash *$ReleaseExeName`n$hash *$AppName.exe" | Set-Content (Join-Path $LatestDir "checksum.sha256") -Encoding utf8

Remove-Item -Recurse -Force $TempPublishDir -ErrorAction SilentlyContinue

Write-Host "`n=============================================" -ForegroundColor Green
Write-Host " Release v$FullInfoVersion succesvol voltooid!" -ForegroundColor Green
Write-Host " Release Exe : $LatestDir\$ReleaseExeName" -ForegroundColor Cyan
Write-Host " App Exe     : $LatestDir\$AppName.exe" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Green
