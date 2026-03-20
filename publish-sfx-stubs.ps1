<# .SYNOPSIS
   Pre-publishes all SFX stub executables so they're available as resources
   for the CLI and UI tools. Run once after cloning or when SFX projects change.

   Usage:
     .\publish-sfx-stubs.ps1              # Publish current platform only
     .\publish-sfx-stubs.ps1 -All         # Publish all supported platforms
     .\publish-sfx-stubs.ps1 -Rid win-x64 # Publish specific RID
#>
param(
    [switch]$All,
    [string]$Rid,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Determine which RIDs to publish
$allRids = @(
    "win-x64", "win-x86", "win-arm64",
    "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64",
    "osx-x64", "osx-arm64"
)

if ($Rid) {
    $targetRids = @($Rid)
} elseif ($All) {
    $targetRids = $allRids
} else {
    # Current platform only
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower()
    if ($IsWindows -or $env:OS -eq "Windows_NT") { $os = "win" }
    elseif ($IsMacOS) { $os = "osx" }
    else { $os = "linux" }
    $targetRids = @("$os-$arch")
}

$projects = @(
    @{ Name = "Compression.Sfx.Cli"; Dir = "Compression.Sfx.Cli" },
    @{ Name = "Compression.Sfx.Ui";  Dir = "Compression.Sfx.Ui" }
)

$stubsDir = Join-Path (Join-Path $root "Compression.CLI") "stubs"

foreach ($rid in $targetRids) {
    foreach ($proj in $projects) {
        # Skip UI stub for non-Windows targets (WPF is Windows-only)
        if ($proj.Name -eq "Compression.Sfx.Ui" -and -not $rid.StartsWith("win")) {
            Write-Host "  Skipping $($proj.Name) for $rid (Windows-only)" -ForegroundColor DarkGray
            continue
        }

        $projPath = Join-Path (Join-Path $root $proj.Dir) "$($proj.Name).csproj"
        Write-Host "Publishing $($proj.Name) for $rid..." -ForegroundColor Cyan
        dotnet publish $projPath -r $rid -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to publish $($proj.Name) for $rid"
            continue
        }

        # Copy published stub to stubs/{rid}/ directory for easy discovery
        $tfm = if ($proj.Name -eq "Compression.Sfx.Ui") { "net10.0-windows" } else { "net10.0" }
        $pubDir = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $root $proj.Dir) "bin") $Configuration) $tfm) $rid) "publish"
        $exeName = if ($rid.StartsWith("win")) {
            if ($proj.Name -eq "Compression.Sfx.Cli") { "sfx-cli.exe" } else { "sfx-ui.exe" }
        } else {
            "sfx-cli"
        }

        $src = Join-Path $pubDir $exeName
        if (Test-Path $src) {
            $destDir = Join-Path $stubsDir $rid
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            Copy-Item $src $destDir -Force
            $size = [math]::Round((Get-Item $src).Length / 1MB, 1)
            Write-Host "  -> stubs/$rid/$exeName ($size MB)" -ForegroundColor Green
        } else {
            Write-Warning "Published file not found: $src"
        }
    }
}

Write-Host "`nDone. Stubs are in: $stubsDir" -ForegroundColor Yellow
