param([switch]$NoLaunch)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$projectFile = (Get-ChildItem -LiteralPath $projectRoot -Filter '*.csproj' -File | Select-Object -First 1).FullName
$releaseDirectory = Join-Path $projectRoot 'bin\Release\net10.0-windows'
$application = Join-Path $releaseDirectory ((Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.exe' -File -ErrorAction SilentlyContinue |
    Select-Object -First 1).Name)
$buildStamp = Join-Path $projectRoot 'obj\desktop-launcher-release.stamp'

$sourceFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Include '*.cs','*.csproj','*.resx','*.ps1' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|Tests|\.git)\\' }
$latestSource = $sourceFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$needsBuild = -not (Test-Path -LiteralPath $application)
if (-not $needsBuild -and -not (Test-Path -LiteralPath $buildStamp)) {
    $needsBuild = $true
}
if (-not $needsBuild -and $null -ne $latestSource) {
    $needsBuild = $latestSource.LastWriteTimeUtc -gt (Get-Item -LiteralPath $buildStamp).LastWriteTimeUtc
}

if ($needsBuild) {
    Write-Host 'Building the latest V7 release...'
    # Restore from the installed package cache first. Network restore is allowed
    # only when NuGet explicitly reports a missing package/version (or no cache).
    $packageCache = $env:NUGET_PACKAGES
    if ([string]::IsNullOrWhiteSpace($packageCache)) {
        $packageCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
    }
    $needsDownload = -not (Test-Path -LiteralPath $packageCache -PathType Container)
    if (-not $needsDownload) {
        $restoreOutput = & dotnet restore $projectFile --source $packageCache -p:NuGetAudit=false --verbosity quiet 2>&1
        $restoreExit = $LASTEXITCODE
        $restoreOutput | ForEach-Object { Write-Host $_ }
        if ($restoreExit -ne 0) {
            $needsDownload = [bool]($restoreOutput -match 'NU1101|NU1102')
            if (-not $needsDownload) {
                Write-Host 'Local dependency restore failed. See the error above.' -ForegroundColor Red
                exit $restoreExit
            }
        }
    }
    if ($needsDownload) {
        Write-Host 'A required dependency is missing locally. Downloading missing dependencies...'
        & dotnet restore $projectFile -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    & dotnet build $projectFile -c Release --nologo --no-restore
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $application)) {
        Write-Host 'Build failed. The previous release was not started.' -ForegroundColor Red
        Read-Host 'Press Enter to close this window'
        exit 1
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $buildStamp) -Force | Out-Null
    Set-Content -LiteralPath $buildStamp -Value (Get-Date).ToUniversalTime().ToString('O') -NoNewline
}

if ($NoLaunch) {
    Write-Host 'The latest V7 release is ready.'
    exit 0
}

Start-Process -FilePath $application -WorkingDirectory $releaseDirectory
