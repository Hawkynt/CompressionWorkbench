namespace FileFormat.Wad2;

/// <summary>
/// Represents a single entry in a WAD2/WAD3 texture archive.
/// </summary>
public sealed class Wad2Entry {
  /// <summary>Gets the entry name (up to 16 ASCII characters).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the uncompressed size of the entry data in bytes.</summary>
  public int Size { get; init; }

  /// <summary>Gets the on-disk (compressed) size of the entry data in bytes.</summary>
  public int CompressedSize { get; init; }

  /// <summary>Gets the entry type byte (e.g. 0x40=palette, 0x43=texture, 0x44=MIP texture).</summary>
  public byte Type { get; init; }

  /// <summary>Gets the compression method (0=none, 1=LZSS).</summary>
  public byte Compression { get; init; }

  /// <summary>Gets the offset of the entry data from the start of the WAD file.</summary>
  public int DataOffset { get; init; }
}
