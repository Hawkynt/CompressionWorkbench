# Helpers shared by the ReFS oracle scripts; they use the caller's $WorkDir and $Probe.

function Invoke-Probe([string[]] $Arguments) {
  & dotnet run $Probe -- @Arguments
  if ($LASTEXITCODE -ne 0) { throw "probe failed: $Arguments" }
}

function New-RefsVhd([string] $Path, [int] $SizeMb, [hashtable] $Format) {
  if (Test-Path $Path) { Remove-Item $Path -Force }
  "create vdisk file=`"$Path`" maximum=$SizeMb type=fixed" | Set-Content "$WorkDir\dp.txt" -Encoding ascii
  diskpart /s "$WorkDir\dp.txt" | Out-Null
  $image = Mount-DiskImage -ImagePath $Path -PassThru
  $disk = $image | Get-Disk
  Initialize-Disk -Number $disk.Number -PartitionStyle MBR
  $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
  $fmt = @{ Partition = $part; FileSystem = 'ReFS'; NewFileSystemLabel = 'CWB'; Confirm = $false; Force = $true }
  foreach ($k in $Format.Keys) { $fmt[$k] = $Format[$k] }
  Format-Volume @fmt | Out-Null
  $part = Get-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber
  return [pscustomobject]@{ Image = $image; Partition = $part; Letter = $part.DriveLetter }
}

function Get-Manifest([string] $Root) {
  Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch 'System Volume Information' } |
    ForEach-Object {
      [pscustomobject]@{
        path   = $_.FullName.Substring($Root.Length).TrimStart('\').Replace('\', '/')
        size   = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      }
    } | Sort-Object path
}

function Copy-Partition([string] $Vhd, [long] $Offset, [long] $Length, [string] $Target) {
  $src = [IO.File]::OpenRead($Vhd)
  $dst = [IO.File]::Create($Target)
  try {
    $src.Position = $Offset
    $buffer = New-Object byte[] (4MB)
    $left = $Length
    while ($left -gt 0) {
      $n = $src.Read($buffer, 0, [Math]::Min($buffer.Length, $left))
      if ($n -le 0) { throw 'short read' }
      $dst.Write($buffer, 0, $n); $left -= $n
    }
  } finally { $src.Dispose(); $dst.Dispose() }
}

function Write-Partition([string] $Vhd, [long] $Offset, [string] $Source) {
  $dst = [IO.File]::Open($Vhd, 'Open', 'ReadWrite')
  $src = [IO.File]::OpenRead($Source)
  try { $dst.Position = $Offset; $src.CopyTo($dst) } finally { $src.Dispose(); $dst.Dispose() }
}

