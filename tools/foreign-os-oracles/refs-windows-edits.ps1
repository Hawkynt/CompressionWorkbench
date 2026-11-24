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
  [string[]] $Variants = @('default', 'cluster64k')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, $WorkDir | Out-Null
. (Join-Path $PSScriptRoot 'refs-common.ps1')

$formats = @{ default = @{}; cluster64k = @{ AllocationUnitSize = 65536 }; integrity = @{ SetIntegrityStreams = $true } }
$gen = Join-Path $WorkDir 'gen'
Invoke-Probe @('gen', $gen)
Invoke-Probe @('pattern', '30', '65536', (Join-Path $WorkDir 'replace-same.bin'))
Invoke-Probe @('pattern', '31', '300000', (Join-Path $WorkDir 'replace-grow.bin'))

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
  Copy-Item -Path (Join-Path $gen '*') -Destination "$($vol.Letter):\" -Recurse -Force
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
    'remove'       = { param($r) Remove-Item (Join-Path $r 'remove-me.bin') -Force }
    'rename'       = { param($r) Rename-Item (Join-Path $r 'rename-me.txt') 'renamed.txt' }
  }
  $record = [ordered]@{ variant = $name; partitionOffset = $offset; bytes = $length; ops = @() }
  foreach ($op in $edits.Keys) {
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
        $scan = Join-Path $WorkDir "$name.$op.salvage"
        New-Item -ItemType Directory -Force -Path $scan | Out-Null
        $verdict.refsutil = ((refsutil salvage -QS $root.TrimEnd('\') $scan 2>&1) -join "`n")
      } catch { $verdict.readWrite = "FAILED: $($_.Exception.Message)" }
      finally { try { Dismount-DiskImage -ImagePath $copy | Out-Null } catch {} }
      $verdict.events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-ReFS/Operational'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Level -le 3 } | ForEach-Object { "$($_.Id): $($_.Message)" })
      Remove-Item $copy -Force
    }
    $record.ops += $verdict
  }
  $summary += $record
  Remove-Item $vhd -Force
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'edits.json')
Get-Content (Join-Path $OutDir 'edits.json')
