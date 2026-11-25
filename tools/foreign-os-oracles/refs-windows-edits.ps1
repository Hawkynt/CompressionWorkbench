# ReFS edits made by Windows itself, beside the same edits made by our offline editor, on one
# freshly formatted volume per variant. The pairs show what Windows changes on disk for an edit
# (which pages, checkpoints, allocator and log records) and what ours changes differently.
#
# For each variant: base.refs (formatted, filled, dismounted), win-noop.refs (mounted
# read-write and dismounted with no change), win-<op>.refs (Windows made the edit), and
# ours-<op>.refs with ours-<op>.json (our editor made the edit from base.refs; Windows then
# mounted the result read-only and hashed every file, and read-write for refsutil).
param(
  [Parameter(Mandatory)] [string] $OutDir,
  [Parameter(Mandatory)] [string] $Probe,
  [string] $WorkDir = (Join-Path $env:RUNNER_TEMP 'refs-edit-work'),
  [string[]] $Variants = @('default', 'cluster64k', 'integrity')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, $WorkDir | Out-Null
. (Join-Path $PSScriptRoot 'refs-common.ps1')

# The empty* variants are formatted and left empty: what a fresh volume holds before any file.
$formats = @{ default = @{}; cluster64k = @{ AllocationUnitSize = 65536 }; integrity = @{ SetIntegrityStreams = $true }
  empty = @{}; empty64k = @{ AllocationUnitSize = 65536 }; emptyintegrity = @{ SetIntegrityStreams = $true } }
$gen = Join-Path $WorkDir 'gen'
Invoke-Probe @('gen', $gen)
Invoke-Probe @('pattern', '30', '65536', (Join-Path $WorkDir 'replace-same.bin'))
Invoke-Probe @('pattern', '31', '300000', (Join-Path $WorkDir 'replace-grow.bin'))
Invoke-Probe @('pattern', '32', '20000', (Join-Path $WorkDir 'replace-shrink.bin'))
Invoke-Probe @('pattern', '33', '70000', (Join-Path $WorkDir 'replace-grow-small.bin'))
Invoke-Probe @('pattern', '34', '30000', (Join-Path $WorkDir 'added.bin'))
foreach ($i in 0..23) { Invoke-Probe @('pattern', "$(40 + $i)", "$(5000 + $i)", (Join-Path $WorkDir ('split-{0:D2}.bin' -f $i))) }
Invoke-Probe @('patterns', '100', '3000', '120', (Join-Path $WorkDir 'grow-{0:D3}.bin'))

# CWB_REFS_OPS limits the edits made (comma list); CWB_REFS_KEEP names the edits whose volumes
# (base, win-<op>, ours-<op>) are kept for upload, so an artifact holds only what is compared.
# A bench branch may pin both in refs-bench.txt beside this script (ops=..., keep=...).
$bench = Join-Path $PSScriptRoot 'refs-bench.txt'
if (Test-Path $bench) {
  foreach ($line in Get-Content $bench) {
    if ($line -match '^ops=(.*)$') { $env:CWB_REFS_OPS = $Matches[1] }
    if ($line -match '^keep=(.*)$') { $env:CWB_REFS_KEEP = $Matches[1] }
    if ($line -match '^variants=(.*)$') { $Variants = @($Matches[1] -split ',') }
  }
}
$onlyOps = @(($env:CWB_REFS_OPS -split ',') | Where-Object { $_ })
$keepOps = @(($env:CWB_REFS_KEEP -split ',') | Where-Object { $_ })

function Mount-Rw([string] $Vhd) {
  $img = Mount-DiskImage -ImagePath $Vhd -PassThru
  Start-Sleep -Seconds 3
  $p = $img | Get-Disk | Get-Partition | Where-Object { $_.Type -ne 'Reserved' } | Select-Object -First 1
  if (-not $p.DriveLetter) {
    Add-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AssignDriveLetter
    $p = Get-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber
  }
  return "$($p.DriveLetter):\"
}

$summary = @()
foreach ($name in $Variants) {
  $vhd = Join-Path $WorkDir "$name.vhd"
  $vol = $null
  foreach ($size in 1024, 2048) {
    try { $vol = New-RefsVhd $vhd $size $formats[$name]; break }
    catch { try { Dismount-DiskImage -ImagePath $vhd | Out-Null } catch {} }
  }
  if (-not $vol) { $summary += [ordered]@{ variant = $name; error = 'format failed' }; continue }
  if ($name -notlike 'empty*') { Copy-Item -Path (Join-Path $gen '*') -Destination "$($vol.Letter):\" -Recurse -Force }
  $offset = $vol.Partition.Offset; $length = $vol.Partition.Size
  Dismount-DiskImage -ImagePath $vhd | Out-Null
  $dir = Join-Path $OutDir $name
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $base = Join-Path $dir 'base.refs'
  Copy-Partition $vhd $offset $length $base

  $edits = [ordered]@{
    'noop'         = { param($r) }
    'replace-same' = { param($r) Copy-Item (Join-Path $WorkDir 'replace-same.bin') (Join-Path $r 'replace-me.bin') -Force }
    'replace-grow' = { param($r) Copy-Item (Join-Path $WorkDir 'replace-grow.bin') (Join-Path $r 'replace-me.bin') -Force }
    'replace-shrink' = { param($r) Copy-Item (Join-Path $WorkDir 'replace-shrink.bin') (Join-Path $r 'replace-me.bin') -Force }
    'replace-grow-small' = { param($r) Copy-Item (Join-Path $WorkDir 'replace-grow-small.bin') (Join-Path $r 'replace-me.bin') -Force }
    'remove'       = { param($r) Remove-Item (Join-Path $r 'remove-me.bin') -Force }
    'remove-leaf'  = { param($r) foreach ($n in 'f002.txt', 'f003.txt', 'f004.txt') { Remove-Item (Join-Path $r "many/$n") -Force } }
    'remove-small' ={ param($r) Remove-Item (Join-Path $r 'many/f007.txt') -Force }
    'add'          = { param($r) Copy-Item (Join-Path $WorkDir 'added.bin') (Join-Path $r 'added.bin') -Force }
    'add-dir'      = { param($r) New-Item -ItemType Directory -Path (Join-Path $r 'new-folder') | Out-Null }
    # "many" holds 200 files over several directory pages.
    'add-many'     = { param($r) Copy-Item (Join-Path $WorkDir 'added.bin') (Join-Path $r 'many/added.bin') -Force }
    'add-dir-many' = { param($r) New-Item -ItemType Directory -Path (Join-Path $r 'many/new-folder') | Out-Null }
    'rename-many'  = { param($r) Rename-Item (Join-Path $r 'many/f010.txt') 'f010-renamed.txt' }
    'replace-many' = { param($r) Copy-Item (Join-Path $WorkDir 'replace-grow-small.bin') (Join-Path $r 'many/f020.txt') -Force }
    # 24 files sorting into one page of "many", enough to split it.
    'add-many-split' = { param($r) foreach ($i in 0..23) { Copy-Item (Join-Path $WorkDir ('split-{0:D2}.bin' -f $i)) (Join-Path $r ('many/f015-{0:D2}.bin' -f $i)) -Force } }
    # 120 files into dir1, a one-page directory, enough to grow it a level at 4 and 64 KiB.
    'add-dir1-grow' = { param($r) foreach ($i in 0..119) { Copy-Item (Join-Path $WorkDir ('grow-{0:D3}.bin' -f $i)) (Join-Path $r ('dir1/grow-{0:D3}.bin' -f $i)) -Force } }
    # Ours formats a new volume from scratch over the partition (Windows' half changes nothing).
    'create'       = { param($r) }
    'create-plain' = { param($r) }
    'create-files' = { param($r) }
    'create-file1' = { param($r) }
    'rename'       = { param($r) Rename-Item (Join-Path $r 'rename-me.txt') 'renamed.txt' }
    'rename-long'  = { param($r) Rename-Item (Join-Path $r 'small.txt') 'a-much-longer-name-for-the-small-file.txt' }
    'rename-unicode' = { param($r) Rename-Item (Join-Path $r 'rename-me.txt') ([string]::new([char[]]@(0x47,0x72,0xFC,0xDF,0x65,0x2D,0x3A9,0x3BC,0x3AD,0x3B3,0x3B1,0x2D,0x65E5,0x672C)) + '.txt') }
  }
  $record = [ordered]@{ variant = $name; partitionOffset = $offset; bytes = $length; ops = @() }
  foreach ($op in $edits.Keys) {
    if ($onlyOps.Count -gt 0 -and $op -ne 'noop' -and $op -notin $onlyOps) { continue }
    # Windows makes the edit.
    $copy = Join-Path $WorkDir "$name.win.vhd"
    Copy-Item $vhd $copy -Force
    $root = Mount-Rw $copy
    & $edits[$op] $root
    Write-VolumeCache -DriveLetter $root[0] -ErrorAction SilentlyContinue
    Dismount-DiskImage -ImagePath $copy | Out-Null
    Copy-Partition $copy $offset $length (Join-Path $dir "win-$op.refs")
    Remove-Item $copy -Force
    if ($op -eq 'noop') { continue }

    # Ours makes the same edit from the same base.
    $ours = Join-Path $dir "ours-$op.refs"
    $resultFile = Join-Path $dir "ours-$op.json"
    Invoke-Probe @('mutate', $base, $op, $ours, $resultFile)
    $result = Get-Content $resultFile -Raw | ConvertFrom-Json
    $verdict = [ordered]@{ op = $op; outcome = $result.outcome; changed = $result.changed }
    if ($result.outcome -eq 'applied') {
      $copy = Join-Path $WorkDir "$name.ours.vhd"
      Copy-Item $vhd $copy -Force
      Write-Partition $copy $offset $ours
      $since = Get-Date
      try {
        $img = Mount-DiskImage -ImagePath $copy -Access ReadOnly -PassThru
        Start-Sleep -Seconds 3
        $letter = ($img | Get-Disk | Get-Partition | Where-Object DriveLetter | Select-Object -First 1).DriveLetter
        $verdict.readOnly = @(Get-Manifest "$($letter):\" | ForEach-Object { "$($_.path) $($_.size) $($_.sha256)" })
      } catch { $verdict.readOnly = "FAILED: $($_.Exception.Message)" }
      finally { try { Dismount-DiskImage -ImagePath $copy | Out-Null } catch {} }
      try {
        $root = Mount-Rw $copy
        $verdict.readWrite = @(Get-Manifest $root | ForEach-Object { "$($_.path) $($_.size) $($_.sha256)" })
        $verdict.directories = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'System Volume Information' } | ForEach-Object { $_.FullName.Substring($root.Length) })
        $scan = Join-Path $WorkDir "$name.$op.salvage"
        New-Item -ItemType Directory -Force -Path $scan | Out-Null
        $verdict.refsinfo = ((fsutil fsinfo refsinfo $root.TrimEnd('\') 2>&1) -join "`n")
        # Something can still hold the volume right after the listing (a version upgrade in
        # progress, a scanner); salvage needs it locked, so it is retried for a minute.
        for ($try = 0; $try -lt 7; ++$try) {
          $verdict.refsutil = ((refsutil salvage -QS $root.TrimEnd('\') $scan 2>&1) -join "`n")
          $verdict.salvageTries = $try + 1
          if ($verdict.refsutil -notmatch 'Failed to lock the volume') { break }
          Start-Sleep -Seconds 10
        }
        $verdict.refsinfoAfter = ((fsutil fsinfo refsinfo $root.TrimEnd('\') 2>&1) -join "`n")
      } catch { $verdict.readWrite = "FAILED: $($_.Exception.Message)" }
      finally { try { Dismount-DiskImage -ImagePath $copy | Out-Null } catch {} }
      $verdict.events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-ReFS/Operational'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Level -le 3 } | ForEach-Object { "$($_.Id): $($_.Message)" })
      Remove-Item $copy -Force
    }
    $record.ops += $verdict
  }
  foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.refs') {
    $op = $file.BaseName -replace '^(win|ours)-', ''
    if ($file.BaseName -eq 'base' -and $keepOps.Count -gt 0) { continue }
    if ($op -notin $keepOps) { Remove-Item -LiteralPath $file.FullName -Force }
  }
  $summary += $record
  Remove-Item $vhd -Force
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'edits.json')
Get-Content (Join-Path $OutDir 'edits.json')
