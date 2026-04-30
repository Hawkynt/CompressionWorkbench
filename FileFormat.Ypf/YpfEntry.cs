namespace FileFormat.Ypf;

/// <summary>
/// One entry in a YPF v480 archive. <see cref="IsCorrupt"/> is set by <see cref="YpfReader"/>
/// when the stored CRC doesn't match the recomputed CRC of the on-disk compressed bytes.
/// The reader doesn't throw on CRC mismatch — callers can still extract the bytes and
/// decide what to do.
/// </summary>
public sealed class YpfEntry {
  /// <summary>The file name as stored in the entry table (raw ASCII, not deobfuscated).</summary>
  public string Name { get; init; } = "";

  /// <summary>The 32-bit name hash stored in the entry record. Engines use it for fast lookup;
  /// we recompute on write but don't validate on read so externally-produced archives load.</summary>
  public uint NameHash { get; init; }

  /// <summary>Type byte: 0=unspecified, 1=script, 2=picture, 3=sound.</summary>
  public byte Type { get; init; }

  /// <summary>Compression method: 0=stored, 1=zlib.</summary>
  public byte Compression { get; init; }

  /// <summary>Original (uncompressed) byte count.</summary>
  public uint RawSize { get; init; }

  /// <summary>On-disk compressed byte count (equals <see cref="RawSize"/> when stored).</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Absolute byte offset of this entry's data within the file.</summary>
  public uint Offset { get; init; }

  /// <summary>CRC-32 of the on-disk COMPRESSED bytes (per YPF/PSF spec).</summary>
  public uint Crc32 { get; init; }

  /// <summary>True when the recomputed CRC of the on-disk bytes didn't match <see cref="Crc32"/>.</summary>
  public bool IsCorrupt { get; init; }
}
