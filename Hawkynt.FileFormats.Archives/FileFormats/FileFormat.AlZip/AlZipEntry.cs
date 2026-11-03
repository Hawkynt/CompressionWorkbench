namespace FileFormat.AlZip;

/// <summary>One local file header of an ALZip archive.</summary>
public sealed class AlZipEntry {
  public required string FileName { get; init; }

  public long OriginalSize { get; init; }

  public long CompressedSize { get; init; }

  /// <summary>0 store, 1 ALZ bzip2, 2 deflate, 3 obfuscated deflate.</summary>
  public int Method { get; init; }

  public uint Crc32 { get; init; }

  public DateTime? LastModified { get; init; }

  /// <summary>DOS attribute bits (0x10 directory).</summary>
  public byte Attributes { get; init; }

  /// <summary>Descriptor byte: bit 0 encrypted, bit 3 data descriptor, high nibble the size-field width.</summary>
  public byte Descriptor { get; init; }

  internal uint DosTime { get; init; }

  internal byte[]? EncryptionHeader { get; init; }

  internal long DataOffset { get; init; }

  public bool IsDirectory => (this.Attributes & 0x10) != 0;

  public bool IsEncrypted => (this.Descriptor & 0x01) != 0;

  public string MethodName => this.Method switch {
    0 => "Store",
    1 => "Bzip2",
    2 => "Deflate",
    3 => "Obfuscated deflate",
    _ => $"Unknown({this.Method})",
  };
}
