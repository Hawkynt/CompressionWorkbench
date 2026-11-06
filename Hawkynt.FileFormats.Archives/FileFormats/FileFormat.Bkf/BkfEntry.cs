#pragma warning disable CS1591
namespace FileFormat.Bkf;

/// <summary>
/// A folder (MTF_DIRB) or file (MTF_FILE) of a Microsoft NTBackup (.bkf) medium.
/// </summary>
public sealed class BkfEntry {
  /// <summary>Path below the backed-up volume, '/'-separated.</summary>
  public string Name { get; init; } = "";

  /// <summary>Length of the file's data in bytes.</summary>
  public long Size { get; init; }

  /// <summary>True for a folder (DIRB).</summary>
  public bool IsDirectory { get; init; }

  /// <summary>The file's or folder's last modification time, when recorded.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Where the STAN stream's data starts in the medium.</summary>
  internal long DataOffset { get; init; }

  /// <summary>Length of the STAN stream's data.</summary>
  internal long DataLength { get; init; }

  /// <summary>The CSUM stream's 32-bit XOR of the data, when the STAN stream is checksummed.</summary>
  internal uint? Checksum { get; init; }

  /// <summary>
  /// True when the data stream is compressed or encrypted (Stream Media Format Attributes
  /// BIT4/BIT3); its algorithm is a registered vendor ID this reader does not decode.
  /// </summary>
  internal bool IsCompressed { get; init; }

  /// <summary>True when the data is held in SPAR (sparse) streams, which this reader does not assemble.</summary>
  internal bool IsSparse { get; init; }
}
