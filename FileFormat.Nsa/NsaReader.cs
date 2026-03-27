using System.Text;

namespace FileFormat.Nsa;

/// <summary>
/// Reads entries from an NScripter NSA archive.
/// </summary>
/// <remarks>
/// NSA format:
/// <list type="bullet">
///   <item>Header: uint16 BE file count, uint32 BE data offset</item>
///   <item>Per entry: null-terminated filename, uint8 compression type, uint32 BE offset (relative to data start), uint32 BE compressed size, uint32 BE original size</item>
///   <item>Data area: file data starts at the data offset</item>
/// </list>
/// Compression types: 0=none, 1=SPB, 2=LZSS, 3=NBZ (bzip2 without BZ header).
/// </remarks>
public sealed class NsaReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the entries in this archive.</summary>
  public IReadOnlyList<NsaEntry> Entries { get; }

  private readonly uint _dataOffset;

  /// <summary>
  /// Initializes a new <see cref="NsaReader"/> from a stream containing an NSA archive.
  /// </summary>
  public NsaReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    var fileCount = ReadUInt16BE();
    this._dataOffset = ReadUInt32BE();

    var entries = new List<NsaEntry>(fileCount);
    for (var i = 0; i < fileCount; i++) {
      var name = ReadNullTerminatedString();
      var compType = (NsaCompressionType)ReadOneByte();
      var offset = ReadUInt32BE();
      var compSize = ReadUInt32BE();
      var origSize = ReadUInt32BE();

      entries.Add(new NsaEntry {
        Name = name,
        CompressionType = compType,
        Offset = this._dataOffset + offset,
        CompressedSize = compSize,
        OriginalSize = origSize,
      });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw or decompressed data for the given entry.
  /// </summary>
  public byte[] Extract(NsaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    this._stream.Position = entry.Offset;
    var compData = new byte[entry.CompressedSize];
    this._stream.ReadExactly(compData);

    return entry.CompressionType switch {
      NsaCompressionType.None => compData,
      NsaCompressionType.Lzss => DecompressLzss(compData, (int)entry.OriginalSize),
      NsaCompressionType.Nbz => DecompressNbz(compData),
      NsaCompressionType.Spb => compData,
      _ => compData,
    };
  }

  /// <summary>
  /// NSA LZSS decompression: 4KB ring buffer, flag-byte framing.
  /// Each flag byte controls 8 operations (MSB first):
  /// bit=1: literal byte, bit=0: (offset:12, length:4) match.
  /// </summary>
  private static byte[] DecompressLzss(byte[] src, int uncompressedSize) {
    var output = new byte[uncompressedSize];
    var ring = new byte[4096];
    var ringPos = 4078;
    Array.Fill(ring, (byte)0x20);

    int srcPos = 0, outPos = 0;

    while (outPos < uncompressedSize && srcPos < src.Length) {
      var flags = src[srcPos++];
      for (var bit = 0; bit < 8 && outPos < uncompressedSize && srcPos < src.Length; bit++) {
        if ((flags & (1 << bit)) != 0) {
          var b = src[srcPos++];
          output[outPos++] = b;
          ring[ringPos] = b;
          ringPos = (ringPos + 1) & 0xFFF;
        } else {
          if (srcPos + 1 >= src.Length) break;
          var b1 = src[srcPos++];
          var b2 = src[srcPos++];
          var offset = b1 | ((b2 & 0xF0) << 4);
          var length = (b2 & 0x0F) + 3;

          for (var j = 0; j < length && outPos < uncompressedSize; j++) {
            var b = ring[(offset + j) & 0xFFF];
            output[outPos++] = b;
            ring[ringPos] = b;
            ringPos = (ringPos + 1) & 0xFFF;
          }
        }
      }
    }

    return output;
  }

  /// <summary>
  /// NBZ decompression: bzip2 data without the leading "BZh" magic.
  /// We prepend "BZh9" (block size 9) and feed to our bzip2 decoder.
  /// </summary>
  private static byte[] DecompressNbz(byte[] compData) {
    var withHeader = new byte[4 + compData.Length];
    withHeader[0] = (byte)'B';
    withHeader[1] = (byte)'Z';
    withHeader[2] = (byte)'h';
    withHeader[3] = (byte)'9';
    Buffer.BlockCopy(compData, 0, withHeader, 4, compData.Length);

    using var input = new MemoryStream(withHeader);
    using var bz2 = new FileFormat.Bzip2.Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    bz2.CopyTo(output);
    return output.ToArray();
  }

  private ushort ReadUInt16BE() {
    Span<byte> buf = stackalloc byte[2];
    this._stream.ReadExactly(buf);
    return (ushort)((buf[0] << 8) | buf[1]);
  }

  private uint ReadUInt32BE() {
    Span<byte> buf = stackalloc byte[4];
    this._stream.ReadExactly(buf);
    return (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
  }

  private byte ReadOneByte() {
    var b = this._stream.ReadByte();
    if (b < 0) throw new EndOfStreamException();
    return (byte)b;
  }

  private string ReadNullTerminatedString() {
    var bytes = new List<byte>();
    while (true) {
      var b = this._stream.ReadByte();
      if (b <= 0) break;
      bytes.Add((byte)b);
    }
    return Encoding.ASCII.GetString(bytes.ToArray());
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
