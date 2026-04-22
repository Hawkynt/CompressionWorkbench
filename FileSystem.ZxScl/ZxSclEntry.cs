#pragma warning disable CS1591
namespace FileSystem.ZxScl;

/// <summary>
/// Directory entry in a ZX Spectrum SCL (Sinclair Compact Language) archive.
/// </summary>
public sealed class ZxSclEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
  /// <summary>TR-DOS type character: 'B' = BASIC, 'C' = code, 'D' = data, '#' = print-out stream.</summary>
  public char FileType { get; init; }
  /// <summary>TR-DOS start address / BASIC auto-start line (param1).</summary>
  public ushort Param1 { get; init; }
  /// <summary>TR-DOS length-in-bytes field (param2).</summary>
  public ushort Param2 { get; init; }
  /// <summary>File length in 256-byte sectors (header-stated).</summary>
  public byte LengthSectors { get; init; }
  /// <summary>Absolute offset of the file data inside the SCL stream.</summary>
  internal long DataOffset { get; init; }
}
