# ReFS oracle capture for a disposable Windows Server runner (needs administrator rights).
#
# 1. Formats fixed VHDs with ReFS in several variants, copies the probe's deterministic file set
#    onto each, and records what Windows says: ReFS version (fsutil fsinfo refsinfo) and the
#    SHA-256 of every file (Get-FileHash).
# 2. Cuts the ReFS partition out of each VHD as a raw .refs image.
# 3. Runs our reader on it, and every offline edit our editor offers, each on its own copy.
# 4. Writes each edited partition back into a copy of the VHD, has Windows mount it read-only,
#    hashes every file and compares with what the edit should have produced, then mounts it
#    read-write and scans it with `refsutil salvage -QS` and the ReFS event log.
#
# Everything lands in $OutDir: the original .refs images, manifests and JSON verdicts.
param(
  [Parameter(Mandatory)] [string] $OutDir,
  [Parameter(Mandatory)] [string] $Probe,
  [string] $WorkDir = (Join-Path $env:RUNNER_TEMP 'refs-work')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, $WorkDir | Out-Null

. (Join-Path $PSScriptRoot 'refs-common.ps1')

function Expected($Manifest, $Result) {
  $files = @{}
  foreach ($f in $Manifest) { $files[$f.path] = [pscustomobject]@{ size = $f.size; sha256 = $f.sha256 } }
  $e = $Result.expectation
  switch ($Result.op) {
    'rename'       { $files[$e.renamed[1]] = $files[$e.renamed[0]]; $files.Remove($e.renamed[0]) }
    'replace-same' { $files[$e.replaced] = [pscustomobject]@{ size = $e.size; sha256 = $e.sha256 } }
    'replace-grow' { $files[$e.replaced] = [pscustomobject]@{ size = $e.size; sha256 = $e.sha256 } }
    'remove'       { $files.Remove($e.removed) }
    'clone'        { $files[$e.cloned[1]] = $files[$e.cloned[0]] }
  }
  return $files
}

$gen = Join-Path $WorkDir 'gen'
Invoke-Probe @('gen', $gen)

$os = (Get-CimInstance Win32_OperatingSystem)
$variants = @(
  @{ Name = 'default';   Format = @{} },
  @{ Name = 'cluster64k'; Format = @{ AllocationUnitSize = 65536 } },
  @{ Name = 'integrity'; Format = @{ SetIntegrityStreams = $true } }
)
$ops = 'rename', 'replace-same', 'replace-grow', 'remove', 'clone', 'defrag'
$summary = [ordered]@{ os = $os.Caption; build = $os.BuildNumber; variants = @() }

foreach ($variant in $variants) {
  $name = $variant.Name
  $vhd = Join-Path $WorkDir "$name.vhd"
  $vol = $null
  foreach ($size in 1024, 2048, 4096) {
    try { $vol = New-RefsVhd $vhd $size $variant.Format; break }
    catch { Write-Host "ReFS $name at $size MB: $($_.Exception.Message)"; try { Dismount-DiskImage -ImagePath $vhd | Out-Null } catch {} }
  }
  if (-not $vol) { $summary.variants += [ordered]@{ name = $name; error = 'format failed at every size' }; continue }
  $root = "$($vol.Letter):\"
  Copy-Item -Path (Join-Path $gen '*') -Destination $root -Recurse -Force
  $refsinfo = (fsutil fsinfo refsinfo "$($vol.Letter):") -join "`n"
  $manifest = @(Get-Manifest $root)
  $offset = $vol.Partition.Offset; $length = $vol.Partition.Size
  Dismount-DiskImage -ImagePath $vhd | Out-Null

  $raw = Join-Path $OutDir "$name.refs"
  Copy-Partition $vhd $offset $length $raw
  $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir "$name.manifest.json")
  $refsinfo | Set-Content (Join-Path $OutDir "$name.refsinfo.txt")

  $ours = Join-Path $OutDir "$name.ours.json"
  Invoke-Probe @('read', $raw, $ours)
  $oursFiles = (Get-Content $ours -Raw | ConvertFrom-Json)
  $readMismatch = @()
  foreach ($f in $manifest) {
    $o = $oursFiles.files | Where-Object { $_.path -eq $f.path }
    if (-not $o -or $o.sha256 -ne $f.sha256) { $readMismatch += $f.path }
  }

  $opResults = @()
  foreach ($op in $ops) {
    $edited = Join-Path $WorkDir "$name.$op.refs"
    $resultFile = Join-Path $OutDir "$name.$op.result.json"
    Invoke-Probe @('mutate', $raw, $op, $edited, $resultFile)
    $result = Get-Content $resultFile -Raw | ConvertFrom-Json
    $verdict = [ordered]@{ op = $op; outcome = $result.outcome; changed = $result.changed }
    if ($result.outcome -eq 'applied') {
      $copy = Join-Path $WorkDir "$name.$op.vhd"
      Copy-Item $vhd $copy -Force
      Write-Partition $copy $offset $edited
      $since = Get-Date
      try {
        $img = Mount-DiskImage -ImagePath $copy -Access ReadOnly -PassThru
        Start-Sleep -Seconds 3
        $letter = ($img | Get-Disk | Get-Partition | Where-Object DriveLetter | Select-Object -First 1).DriveLetter
        if (-not $letter) {
          $p = $img | Get-Disk | Get-Partition | Select-Object -First 1
          Add-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AssignDriveLetter
          $letter = (Get-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber).DriveLetter
        }
        $verdict.fileSystem = (Get-Volume -DriveLetter $letter).FileSystemType
        $seen = @{}
        foreach ($f in Get-Manifest "$($letter):\") { $seen[$f.path] = $f }
        $want = Expected $manifest $result
        $diff = @()
        foreach ($k in $want.Keys) { if (-not $seen.ContainsKey($k)) { $diff += "missing $k" } elseif ($seen[$k].sha256 -ne $want[$k].sha256 -or $seen[$k].size -ne $want[$k].size) { $diff += "content $k" } }
        foreach ($k in $seen.Keys) { if (-not $want.ContainsKey($k)) { $diff += "extra $k" } }
        $verdict.windowsReadOnlyMount = if ($diff.Count -eq 0) { 'match' } else { 'MISMATCH' }
        $verdict.differences = $diff
      } catch {
        $verdict.windowsReadOnlyMount = "FAILED: $($_.Exception.Message)"
      } finally {
        try { Dismount-DiskImage -ImagePath $copy | Out-Null } catch {}
      }
      try {
        $img = Mount-DiskImage -ImagePath $copy -PassThru
        Start-Sleep -Seconds 3
        $letter = ($img | Get-Disk | Get-Partition | Where-Object DriveLetter | Select-Object -First 1).DriveLetter
        if ($letter) {
          $scanDir = Join-Path $WorkDir "$name.$op.salvage"
          New-Item -ItemType Directory -Force -Path $scanDir | Out-Null
          $verdict.refsutilSalvageQS = ((refsutil salvage -QS "$($letter):" $scanDir 2>&1) -join "`n")
        }
      } catch {
        $verdict.refsutilSalvageQS = "FAILED: $($_.Exception.Message)"
      } finally {
        try { Dismount-DiskImage -ImagePath $copy | Out-Null } catch {}
      }
      try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-ReFS/Operational'; StartTime = $since } -ErrorAction SilentlyContinue |
          Where-Object { $_.Level -le 3 } | ForEach-Object { "$($_.Id) $($_.LevelDisplayName): $($_.Message)" }
        $verdict.refsEvents = @($events)
      } catch { $verdict.refsEvents = @("log unavailable: $($_.Exception.Message)") }
      Remove-Item $copy -Force
    }
    Remove-Item $edited -Force -ErrorAction SilentlyContinue
    $opResults += $verdict
  }
  $summary.variants += [ordered]@{
    name = $name; sizeBytes = $length; partitionOffset = $offset; refsinfo = $refsinfo
    files = $manifest.Count; ourReaderMismatches = $readMismatch; ourReaderError = $oursFiles.error; ops = $opResults
  }
  Remove-Item $vhd -Force
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'summary.json')
Get-Content (Join-Path $OutDir 'summary.json')
