using Compression.Core.Deflate;

namespace FileFormat.AlZip;

/// <summary>
/// Reads ALZip (.alz) archive files.
/// </summary>
/// <remarks>
/// ALZip is a Korean archive format by ESTsoft. The format uses a simple
/// container with per-file deflate or bzip2 compression.
///
/// Archive layout:
///   [ALZ\x01]                    — 4-byte magic
///   [BLZ\x01][header][data]...   — repeated file entries
///   [CLZ\x02]                    — end-of-archive marker
/// </remarks>
public sealed class AlZipReader : IDisposable {

  /// <summary>Archive magic: "ALZ\x01".</summary>
  internal static ReadOnlySpan<byte> Magic => [0x41, 0x4C, 0x5A, 0x01];

  /// <summary>Local file header signature: BLZ\x01 as uint32 LE = 0x015A4C42.</summary>
  private const uint LocalSig = 0x015A4C42;

  /// <summary>End-of-archive signature: CLZ\x02 as uint32 LE = 0x025A4C43.</summary>
  private const uint EndSig = 0x025A4C43;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<AlZipEntry> _entries = [];

  /// <summary>
  /// Creates a new ALZip reader over the given stream.
  /// </summary>
  public AlZipReader(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;
    _ReadArchive();
  }

  /// <summary>File entries in the archive.</summary>
  public IReadOnlyList<AlZipEntry> Entries => _entries;

  /// <summary>
  /// Extracts the raw (decompressed) data for the given entry.
  /// </summary>
  public byte[] Extract(AlZipEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.IsDirectory)
      return [];

    _stream.Position = entry.DataOffset;
    var compressedData = new byte[entry.CompressedSize];
    _ReadExactly(compressedData);

    return entry.Method switch {
      0 => compressedData, // Store
      2 => DeflateDecompressor.Decompress(compressedData), // Deflate
      _ => throw new InvalidDataException($"Unsupported ALZ compression method: {entry.Method}"),
    };
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!_leaveOpen)
      _stream.Dispose();
  }

  private void _ReadArchive() {
    Span<byte> magic = stackalloc byte[4];
    _ReadExactly(magic);

    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not a valid ALZ file: invalid magic.");

    Span<byte> sigBuf = stackalloc byte[4];
    while (true) {
      if (_stream.Read(sigBuf) < 4)
        break;

      var sig = ReadUInt32LE(sigBuf);
      if (sig == EndSig)
        break;

      if (sig != LocalSig)
        throw new InvalidDataException($"Unexpected ALZ signature: 0x{sig:X8}");

      _ReadEntry();
    }
  }

  private void _ReadEntry() {
    // Filename length (2 bytes LE)
    Span<byte> buf2 = stackalloc byte[2];
    _ReadExactly(buf2);
    var filenameLen = (ushort)(buf2[0] | (buf2[1] << 8));

    // File attribute (1 byte)
    var attr = (byte)_ReadByte();
    var isDir = (attr & 0x10) != 0;

    // DOS timestamp (4 bytes LE)
    Span<byte> timeBuf = stackalloc byte[4];
    _ReadExactly(timeBuf);
    var dosTime = ReadUInt32LE(timeBuf);
    var lastModified = DosTimeToDateTime(dosTime);

    // File descriptor (1 byte) — upper nibble determines size field width
    var descriptor = (byte)_ReadByte();
    var sizeWidth = (descriptor & 0xF0) switch {
      0x10 => 2,
      0x20 => 4,
      0x40 => 8,
      _ => 4,
    };

    // Unknown/reserved (1 byte)
    _ReadByte();

    // Compression method (1 byte)
    var method = (byte)_ReadByte();

    // CRC-32 (4 bytes LE)
    Span<byte> crcBuf = stackalloc byte[4];
    _ReadExactly(crcBuf);
    var crc32 = ReadUInt32LE(crcBuf);

    // Compressed size
    var compressedSize = _ReadSizeField(sizeWidth);

    // Uncompressed size
    var uncompressedSize = _ReadSizeField(sizeWidth);

    // Filename
    var filenameBuf = new byte[filenameLen];
    _ReadExactly(filenameBuf);
    var filename = System.Text.Encoding.UTF8.GetString(filenameBuf);
    filename = filename.Replace('\\', '/');

    var dataOffset = _stream.Position;

    _entries.Add(new AlZipEntry {
      FileName = filename,
      OriginalSize = uncompressedSize,
      CompressedSize = compressedSize,
      IsDirectory = isDir,
      Method = method,
      Crc32 = crc32,
      LastModified = lastModified,
      Attributes = attr,
      DataOffset = dataOffset,
    });

    // Skip past compressed data
    if (compressedSize > 0 && _stream.CanSeek)
      _stream.Position += compressedSize;
    else if (compressedSize > 0)
      _SkipBytes(compressedSize);
  }

  private long _ReadSizeField(int width) {
    Span<byte> buf = stackalloc byte[8];
    _ReadExactly(buf[..width]);
    return width switch {
      2 => buf[0] | (buf[1] << 8),
      4 => ReadUInt32LE(buf),
      8 => (long)ReadUInt64LE(buf),
      _ => throw new InvalidDataException($"Invalid ALZ size field width: {width}"),
    };
  }

  internal static DateTime? DosTimeToDateTime(uint dosTime) {
    if (dosTime == 0) return null;
    try {
      var time = (ushort)(dosTime & 0xFFFF);
      var date = (ushort)(dosTime >> 16);
      var year = ((date >> 9) & 0x7F) + 1980;
      var month = (date >> 5) & 0x0F;
      var day = date & 0x1F;
      var hour = (time >> 11) & 0x1F;
      var minute = (time >> 5) & 0x3F;
      var second = (time & 0x1F) * 2;
      if (month < 1 || month > 12 || day < 1 || day > 31) return null;
      return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    } catch {
      return null;
    }
  }

  internal static uint DateTimeToDosTime(DateTime dt) {
    var date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    var time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    return (uint)((date << 16) | time);
  }

  internal static uint ReadUInt32LE(ReadOnlySpan<byte> b) =>
    (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

  private static ulong ReadUInt64LE(ReadOnlySpan<byte> b) =>
    (ulong)b[0] | ((ulong)b[1] << 8) | ((ulong)b[2] << 16) | ((ulong)b[3] << 24) |
    ((ulong)b[4] << 32) | ((ulong)b[5] << 40) | ((ulong)b[6] << 48) | ((ulong)b[7] << 56);

  private int _ReadByte() {
    var b = _stream.ReadByte();
    if (b < 0) throw new InvalidDataException("Unexpected end of ALZ stream.");
    return b;
  }

  private void _ReadExactly(Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = _stream.Read(buffer[offset..]);
      if (read == 0) throw new InvalidDataException("Unexpected end of ALZ stream.");
      offset += read;
    }
  }

  private void _SkipBytes(long count) {
    var buf = new byte[Math.Min(count, 8192)];
    while (count > 0) {
      var toRead = (int)Math.Min(count, buf.Length);
      var read = _stream.Read(buf, 0, toRead);
      if (read == 0) throw new InvalidDataException("Unexpected end of ALZ stream.");
      count -= read;
    }
  }
}
