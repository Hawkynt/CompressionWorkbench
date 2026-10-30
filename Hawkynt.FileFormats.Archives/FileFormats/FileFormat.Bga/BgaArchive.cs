// GZA/BZA (BGA32.DLL) archive layout, converted from QBga32's bga_melter.d
// (http://www.kmonos.net/repos/qbga32, k.inaba, NYSL 0.9982). See THIRD-PARTY-NOTICE.qbga32.txt.

using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Bzip2;

namespace FileFormat.Bga;

/// <summary>One member of a GZA/BZA archive.</summary>
public sealed class BgaEntry {
  /// <summary>Gets the stored name ('\' replaced by '/'; directories without the trailing separator).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets the method tag: "GZIP" or "BZ2".</summary>
  public string Method { get; init; } = string.Empty;

  /// <summary>Gets whether the data is stored uncompressed.</summary>
  public bool IsStored { get; init; }

  /// <summary>Gets whether this is a directory entry.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the original size.</summary>
  public uint OriginalSize { get; init; }

  /// <summary>Gets the stored size.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets the DOS modification time (local time, as stored), when set.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Gets the DOS attribute byte.</summary>
  public byte Attributes { get; init; }

  internal long DataOffset { get; init; }

  internal long RecordOffset { get; init; }
}

/// <summary>
/// GZA and BZA archives (Toshinobu Kimura's BGA32.DLL): per-file headers with a signed-byte
/// checksum, each followed by that file's gzip or bzip2 stream (or its bytes when stored). A
/// self-extracting stub is skipped by searching the first 64 KiB for a header whose checksum holds.
/// </summary>
public static class BgaArchive {
  private const int HeaderSize = 28;
  private const int SearchWindow = 0x10000;
  private const byte DirectoryAttribute = 0x10;
  private const byte ArchiveAttribute = 0x20;

  internal static readonly Encoding NameEncoding;

  static BgaArchive() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    NameEncoding = Encoding.GetEncoding(932);
  }

  // The checksum is the sum, as signed chars, of every header byte after it and the name.
  private static int Checksum(ReadOnlySpan<byte> bytes) {
    var sum = 0;
    foreach (var b in bytes)
      sum += (sbyte)b;
    return sum;
  }

  private static bool HeaderAt(ReadOnlySpan<byte> data, int at) {
    if (data.Length - at < HeaderSize)
      return false;
    var method = data.Slice(at + 4, 4);
    if (!method.SequenceEqual("GZIP"u8) && !method.SequenceEqual("BZ2\0"u8))
      return false;
    var names = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 24)..]) + BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 26)..]);
    if (data.Length - at - HeaderSize < names)
      return false;
    return BinaryPrimitives.ReadInt32LittleEndian(data[at..]) == Checksum(data.Slice(at + 4, HeaderSize - 4 + names));
  }

  /// <summary>Lists the members, starting at the first valid header in the first 64 KiB.</summary>
  /// <param name="data">The whole archive (or self-extractor).</param>
  /// <param name="truncated">Set when a header is damaged or a member runs past the end.</param>
  /// <exception cref="InvalidDataException">No GZA/BZA header is found.</exception>
  public static List<BgaEntry> Read(ReadOnlySpan<byte> data, out bool truncated) {
    truncated = false;
    var start = -1;
    for (var i = 0; i < Math.Min(data.Length, SearchWindow); ++i)
      if (HeaderAt(data, i)) {
        start = i;
        break;
      }

    if (start < 0)
      throw new InvalidDataException("Not a GZA/BZA archive (no header with a valid checksum).");

    var entries = new List<BgaEntry>();
    for (var at = start; at < data.Length;) {
      if (!HeaderAt(data, at)) {
        truncated = true;
        break;
      }

      var method = data[(at + 4)..(at + 8)].SequenceEqual("GZIP"u8) ? "GZIP" : "BZ2";
      var compressed = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
      var original = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
      var date = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 16)..]);
      var time = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 18)..]);
      var attributes = data[at + 20];
      var arcType = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 22)..]);
      var names = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 24)..]) + BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 26)..]);
      var name = NameEncoding.GetString(data.Slice(at + HeaderSize, names));
      long dataAt = at + HeaderSize + names;
      if (compressed > data.Length - dataAt) {
        truncated = true;
        break;
      }

      var directory = name.EndsWith('\\') || name.EndsWith('/');
      entries.Add(new BgaEntry {
        FileName = name.Replace('\\', '/').TrimEnd('/'),
        Method = method,
        IsStored = !IsCompressed(arcType, compressed, original, name),
        IsDirectory = directory,
        OriginalSize = original,
        CompressedSize = compressed,
        LastModified = DosTime(date, time),
        Attributes = attributes,
        DataOffset = dataAt,
        RecordOffset = at,
      });
      at = (int)(dataAt + compressed);
    }

    return entries;
  }

  // arc_type 1 = compressed, 2 = stored; 0 (older archives) = stored only when the sizes agree and
  // the name has an already-compressed extension.
  private static bool IsCompressed(int arcType, uint compressed, uint original, string name) {
    if (arcType == 2)
      return false;
    if (arcType != 0 || compressed != original)
      return true;
    var dot = name.LastIndexOf('.');
    if (dot < 0)
      return true;
    return name[(dot + 1)..].ToLowerInvariant() is not ("arc" or "arj" or "bz2" or "bza" or "cab" or "gz" or "gza" or "lzh"
      or "lzs" or "pak" or "rar" or "taz" or "tbz" or "tgz" or "z" or "zip" or "zoo");
  }

  private static DateTime? DosTime(ushort date, ushort time) {
    if (date == 0)
      return null;
    try {
      return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  /// <summary>Decodes a member, checking the gzip CRC-32 (bzip2 checks its own).</summary>
  /// <exception cref="InvalidDataException">The data is corrupt or does not have the recorded size.</exception>
  public static byte[] Extract(ReadOnlySpan<byte> data, BgaEntry entry) {
    if (entry.IsDirectory)
      return [];
    var stored = data.Slice((int)entry.DataOffset, (int)entry.CompressedSize);
    byte[] result;
    if (entry.IsStored)
      result = stored.ToArray();
    else if (entry.Method == "GZIP") {
      if (stored.Length < 18 || stored[0] != 0x1F || stored[1] != 0x8B || stored[2] != 8 || stored[3] != 0)
        throw new InvalidDataException($"GZA member '{entry.FileName}' is not a plain gzip stream.");
      result = DeflateDecompressor.Decompress(stored[10..^8]);
      var crc = BinaryPrimitives.ReadUInt32LittleEndian(stored[^8..]);
      if (Compression.Core.Checksums.Crc32.Compute(result) != crc)
        throw new InvalidDataException($"GZA member '{entry.FileName}' fails its CRC-32.");
    } else {
      using var input = new MemoryStream(stored.ToArray());
      using var bz = new Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress);
      using var output = new MemoryStream();
      bz.CopyTo(output);
      result = output.ToArray();
    }

    if (result.Length != entry.OriginalSize)
      throw new InvalidDataException($"BGA member '{entry.FileName}' decoded to {result.Length} bytes, not {entry.OriginalSize}.");
    return result;
  }

  /// <summary>Builds one member record the way BGA32.DLL writes it.</summary>
  /// <param name="gzip">GZA (gzip) when true, BZA (bzip2) when false.</param>
  /// <param name="name">The member name, '/' or '\' separated; a directory gets a trailing '\'.</param>
  /// <param name="data">The file bytes (empty for a directory).</param>
  /// <param name="modified">The local modification time, or null.</param>
  /// <param name="directory">Whether this is a directory entry.</param>
  public static byte[] BuildRecord(bool gzip, string name, ReadOnlySpan<byte> data, DateTime? modified, bool directory) {
    var path = name.Replace('/', '\\').Trim('\\');
    if (directory)
      path += "\\";
    var slash = directory ? path.TrimEnd('\\').LastIndexOf('\\') : path.LastIndexOf('\\');
    var dirPart = NameEncoding.GetBytes(path[..(slash + 1)]);
    var filePart = NameEncoding.GetBytes(path[(slash + 1)..]);
    if (directory) {
      // BGA32 stores a directory entry wholly in the directory-name part.
      dirPart = NameEncoding.GetBytes(path);
      filePart = [];
    }

    byte[] payload = [];
    ushort arcType = 2;
    if (!directory && data.Length > 0) {
      var packed = gzip ? GzipMember(data) : Bzip2Member(data);
      if (packed.Length < data.Length) {
        payload = packed;
        arcType = 1;
      } else
        payload = data.ToArray();
    } else if (directory)
      arcType = 1;

    var header = new byte[HeaderSize + dirPart.Length + filePart.Length];
    (gzip ? "GZIP"u8 : "BZ2\0"u8).CopyTo(header.AsSpan(4));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), directory ? 0u : (uint)data.Length);
    if (!directory && modified is { } m && m.Year >= 1980) {
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), (ushort)(((m.Year - 1980) << 9) | (m.Month << 5) | m.Day));
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), (ushort)((m.Hour << 11) | (m.Minute << 5) | (m.Second / 2)));
    }

    header[20] = directory ? DirectoryAttribute : ArchiveAttribute;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), arcType);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), (ushort)dirPart.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)filePart.Length);
    dirPart.CopyTo(header.AsSpan(HeaderSize));
    filePart.CopyTo(header.AsSpan(HeaderSize + dirPart.Length));
    BinaryPrimitives.WriteInt32LittleEndian(header, Checksum(header.AsSpan(4)));
    return [.. header, .. payload];
  }

  // A bare gzip member as BGA32 writes it: no name, no time, OS 0x0B.
  private static byte[] GzipMember(ReadOnlySpan<byte> data) {
    var deflated = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);
    var member = new byte[10 + deflated.Length + 8];
    member[0] = 0x1F;
    member[1] = 0x8B;
    member[2] = 8;
    member[9] = 0x0B;
    deflated.CopyTo(member, 10);
    BinaryPrimitives.WriteUInt32LittleEndian(member.AsSpan(10 + deflated.Length), Compression.Core.Checksums.Crc32.Compute(data));
    BinaryPrimitives.WriteUInt32LittleEndian(member.AsSpan(14 + deflated.Length), (uint)data.Length);
    return member;
  }

  private static byte[] Bzip2Member(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var bz = new Bzip2Stream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      bz.Write(data);
    return output.ToArray();
  }
}
