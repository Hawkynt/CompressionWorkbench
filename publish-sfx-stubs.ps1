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

# Stubs go into Compression.Lib for embedding as resources (and legacy CLI path)
$stubsDir = Join-Path (Join-Path $root "Compression.Lib") "stubs"
$legacyStubsDir = Join-Path (Join-Path $root "Compression.CLI") "stubs"

foreach ($rid in $targetRids) {
    foreach ($proj in $projects) {
        # The GUI stub is NativeForms now, with a backend for win, linux and osx alike. Only
        # musl RIDs are skipped: no NativeForms backend targets them.
        if ($proj.Name -eq "Compression.Sfx.Ui" -and $rid.Contains("musl")) {
            Write-Host "  Skipping $($proj.Name) for $rid (no GUI backend)" -ForegroundColor DarkGray
            continue
        }

        $projPath = Join-Path (Join-Path $root $proj.Dir) "$($proj.Name).csproj"
        Write-Host "Publishing $($proj.Name) for $rid..." -ForegroundColor Cyan
        # Universal tier: one stub per RID covering every archive format. Carved per-format stubs
        # are a CI concern; a dev build wants one stub that reads anything.
        dotnet publish $projPath -r $rid -c $Configuration -p:ExcludeStubs=true -p:SfxTier=Universal --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to publish $($proj.Name) for $rid"
            continue
        }

        # Copy published stub to stubs/{rid}/ directory for easy discovery
        # Both stubs target net10.0 since the GUI left WPF; AOT inserts a platform segment.
        $tfm = "net10.0"
        $projRoot = Join-Path $root $proj.Dir
        $pubDir = $null
        foreach ($platform in @("", "x64", "arm64")) {
            $bin = if ($platform -eq "") { Join-Path $projRoot "bin" } else { Join-Path (Join-Path $projRoot "bin") $platform }
            $candidate = Join-Path (Join-Path (Join-Path $bin $Configuration) $tfm) (Join-Path $rid "publish")
            if (Test-Path $candidate) { $pubDir = $candidate; break }
        }
        if (-not $pubDir) {
            Write-Warning "No publish output found for $($proj.Name) / $rid"
            continue
        }
        $exeName = if ($rid.StartsWith("win")) {
            if ($proj.Name -eq "Compression.Sfx.Cli") { "sfx-cli.exe" } else { "sfx-ui.exe" }
        } else {
            "sfx-cli"
        }

        $src = Join-Path $pubDir $exeName
        if (Test-Path $src) {
            # Primary: Compression.Lib/stubs/ (embedded resources)
            $destDir = Join-Path $stubsDir $rid
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            Copy-Item $src $destDir -Force

            # Legacy: Compression.CLI/stubs/ (file-system fallback)
            $legacyDestDir = Join-Path $legacyStubsDir $rid
            New-Item -ItemType Directory -Path $legacyDestDir -Force | Out-Null
            Copy-Item $src $legacyDestDir -Force

            $size = [math]::Round((Get-Item $src).Length / 1MB, 1)
            Write-Host "  -> stubs/$rid/$exeName ($size MB)" -ForegroundColor Green
        } else {
            Write-Warning "Published file not found: $src"
        }
    }
}

Write-Host "`nDone. Stubs are in: $stubsDir" -ForegroundColor Yellow
