# Regenerates the MS-NRBF reference vectors with the real .NET BinaryFormatter.
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File generate-nrbf.ps1
#
# Windows PowerShell 5.1 runs on .NET Framework, where BinaryFormatter still exists; .NET 5+
# removed it, which is exactly why our reader implements MS-NRBF directly instead of calling it.
# Nothing in CompressionWorkbench ever deserialises these files -- the reader walks records and
# never activates a type -- but a producer has to come from somewhere, and this is the producer
# that defined the format.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Vector {
  param([string]$Name, [object]$Value)

  $formatter = New-Object System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
  $memory = New-Object System.IO.MemoryStream
  $formatter.Serialize($memory, $Value)
  $bytes = $memory.ToArray()
  [System.IO.File]::WriteAllBytes((Join-Path $here $Name), $bytes)

  $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
  $hex = ($sha | ForEach-Object { $_.ToString('x2') }) -join ''
  '{0,-42} {1,7} bytes  sha256={2}' -f $Name, $bytes.Length, $hex
}

'powershell  {0}' -f $PSVersionTable.PSVersion
'clr         {0}' -f [System.Environment]::Version
''

Write-Vector 'nrbf-string.nrbf' 'hello'

$table = New-Object System.Collections.Hashtable
$table['name'] = 'value'
$table['count'] = [int]7
Write-Vector 'nrbf-hashtable.nrbf' $table

Write-Vector 'nrbf-object-array.nrbf' ([object[]]@('first', [int]42, [bool]$true, $null, [double]1.5))

# The archive envelope our NRBF writer emits, for the writer-parity inputs every structured format
# encodes (dir/file.bin, dir/long.bin, top.bin): the marker, then each path followed by its bytes.
$envelope = [object[]]@(
  'Hawkynt.CompressionWorkbench.NrbfArchive/1',
  'dir/file.bin', [byte[]](0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a),
  'dir/long.bin', [byte[]](0..63),
  'top.bin', [byte[]](0xde, 0xad, 0xbe, 0xef))
Write-Vector 'nrbf-archive.nrbf' $envelope
