using Compression.Core.Dictionary.Sqx;

namespace FileFormat.Sqx;

/// <summary>A file or directory recorded in an SQX archive.</summary>
public sealed class SqxEntry {
  /// <summary>Path inside the archive, with <c>/</c> separators.</summary>
  public string FileName { get; init; } = "";

  /// <summary>Whether this is a directory record.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Bytes of compressed data that follow the header.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Size of the file.</summary>
  public long OriginalSize { get; init; }

  /// <summary>The stored checksum: CRC-32 (reflected, initial value <c>0xFFFFFFFF</c>) without the final inversion.</summary>
  public uint Crc32 { get; init; }

  /// <summary>Compression method: 0 stored, 1-4 LZ77 + Huffman, 5 audio, 6 text (PPMd), 7 ultra.</summary>
  public byte Method { get; init; }

  /// <summary>Modification time from the DOS timestamp, or <see cref="DateTime.MinValue"/> when it is zero.</summary>
  public DateTime LastModified { get; init; }

  /// <summary>DOS file attributes.</summary>
  public uint Attributes { get; init; }

  /// <summary>File header flags.</summary>
  public ushort Flags { get; init; }

  /// <summary>Format of this file's stream: <see cref="SqxTables.Format11"/> or <see cref="SqxTables.Format20"/>.</summary>
  public byte ArchiveVersion { get; init; }

  /// <summary><c>cCompFlags</c>; bit 0 announces an extended compressor header at the start of the stream.</summary>
  public byte CompFlags { get; init; }

  /// <summary>Offset of the compressed data in the archive.</summary>
  public long DataOffset { get; init; }

  /// <summary>Whether the data is encrypted (flag bit 1).</summary>
  public bool IsEncrypted => (this.Flags & 0x0002) != 0;

  /// <summary>Whether the stream continues the previous file's dictionary (flag bit 2).</summary>
  public bool IsSolid => (this.Flags & 0x0004) != 0;

  /// <summary>Whether the file is split across volumes (flag bits 3 and 4).</summary>
  public bool IsSplit => (this.Flags & 0x0018) != 0;

  /// <summary>Dictionary size of the stream.</summary>
  public int DictionarySize => SqxTables.DictionarySize(this.Flags, this.ArchiveVersion);
}
