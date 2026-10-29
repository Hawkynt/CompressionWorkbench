using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Streams;
using FileFormat.Compress;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Reads a single DiskDoubler compressed file and decodes its forks.
/// </summary>
/// <remarks>
/// <para>DiskDoubler (Salient Software, 1989-1993) replaced a file by a compressed one with this 84-byte
/// header (all big-endian), followed by the compressed data fork and then the compressed resource fork:</para>
/// <code>
///   [0..4]   magic 0xABCD0054
///   [4..8]   data fork size        [8..12]  data fork compressed size
///   [12..16] resource fork size    [16..20] resource fork compressed size
///   [20]     data fork method      [21]     resource fork method        [22] version byte
///   [24..28] modified              [28..32] created     (seconds since 1904)
///   [32..36] file type             [36..40] creator     [40..42] Finder flags
///   [48..50] data fork checksum    [50..52] resource fork checksum      [52] second version byte
///   [54..56] data fork delta type  [56..58] resource fork delta type
///   [82..84] CRC-16/XMODEM of bytes 0..82 (very old files store 0000)
/// </code>
/// <para>The file may sit inside a MacBinary wrapper, in which case the header follows its 128 bytes.
/// The header holds no name: the entries take the name of the file being read, without ".dd".</para>
/// <para>Decoded methods: 0 (stored), 1 (Unix <c>compress</c>, with its own header XOR-masked by 0x5A and
/// a 16-bit byte-sum check), 6 and 9 ("AD" LZSS blocks, see <see cref="DiskDoublerAdDecoder"/>). Every
/// other method — the DiskDoubler Huffman variants, RLE, Stac LZS, Compact Pro and "DD" — and any delta
/// pre-processing is refused with <see cref="NotSupportedException"/> rather than returned undecoded.</para>
/// <para>Sourcing: layout and methods from The Unarchiver's XADMaster (<c>XADDiskDoublerParser.m</c>,
/// LGPL-2.1-or-later); verified on real DiskDoubler files against unar's output.</para>
/// </remarks>
public sealed class DiskDoublerReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<DiskDoublerEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a DiskDoubler compressed file from the given stream and parses its header.
  /// </summary>
  /// <param name="stream">A readable and seekable stream containing a DiskDoubler file.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <param name="name">The name to give the entries; defaults to the stream's file name without ".dd".</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">The stream holds no DiskDoubler header, or its header CRC is wrong.</exception>
  public DiskDoublerReader(Stream stream, bool leaveOpen = false, string? name = null) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ParseHeader(name ?? DefaultName(stream));
  }

  /// <summary>Gets the forks parsed from the header: the data fork (if any), then the resource fork (if any).</summary>
  public IReadOnlyList<DiskDoublerEntry> Entries => this._entries;

  /// <summary>Returns whether <paramref name="header"/> starts a DiskDoubler file (directly or behind MacBinary).</summary>
  public static bool IsDiskDoubler(ReadOnlySpan<byte> header) =>
    StartsWithHeader(header) ||
    (header.Length >= DiskDoublerConstants.MacBinaryHeaderSize && StartsWithHeader(header[DiskDoublerConstants.MacBinaryHeaderSize..]));

  /// <summary>Decodes the specified fork.</summary>
  /// <exception cref="NotSupportedException">The fork uses a method (or delta step) this reader does not decode.</exception>
  /// <exception cref="InvalidDataException">The compressed data is damaged or fails its check.</exception>
  public byte[] Extract(DiskDoublerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.DeltaType != 0)
      throw new NotSupportedException($"DiskDoubler fork '{entry.Name}' uses delta pre-processing type {entry.DeltaType}, which is not supported.");

    this._stream.Position = entry.DataOffset;
    var compressed = new byte[entry.CompressedSize];
    this._stream.ReadExactly(compressed);

    return entry.Method switch {
      DiskDoublerConstants.MethodStored => entry.OriginalSize <= compressed.LongLength
        ? compressed[..(int)entry.OriginalSize]
        : throw new InvalidDataException($"DiskDoubler stored fork '{entry.Name}' is shorter than its size."),
      DiskDoublerConstants.MethodCompress => DecodeCompress(entry, compressed),
      DiskDoublerConstants.MethodAds or DiskDoublerConstants.MethodAd1 => DiskDoublerAdDecoder.Decode(compressed, entry.OriginalSize),
      _ => throw new NotSupportedException(
        $"DiskDoubler fork '{entry.Name}' uses method {entry.Method} ({MethodName(entry.Method)}), which is not supported."),
    };
  }

  /// <summary>Names a method as the format's tools do.</summary>
  public static string MethodName(int method) => method switch {
    0 => "None",
    1 => "Compress",
    2 => "Method 2",
    3 => "RLE",
    4 => "Huffman",
    5 => "Method 5",
    6 => "ADS/AD2",
    7 => "Stac LZS",
    8 => "Compact Pro",
    9 => "AD/AD1",
    10 => "DDn",
    _ => $"Method {method}",
  };

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Decoding ──────────────────────────────────────────────────────────────────

  private static byte[] DecodeCompress(DiskDoublerEntry entry, byte[] compressed) {
    if (compressed.Length < 3)
      throw new InvalidDataException($"DiskDoubler compress fork '{entry.Name}' is shorter than its header.");
    // The three header bytes are a .Z header (1F 9D flags), masked like the output.
    var header = new byte[] { (byte)(compressed[0] ^ entry.XorKey), (byte)(compressed[1] ^ entry.XorKey), (byte)(compressed[2] ^ entry.XorKey) };
    if (header[0] != 0x1F || header[1] != 0x9D)
      throw new InvalidDataException($"DiskDoubler compress fork '{entry.Name}' lacks the compress magic.");

    var unmasked = (byte[])compressed.Clone();
    header.CopyTo(unmasked, 0);

    var output = new byte[checked((int)entry.OriginalSize)];
    using (var source = new MemoryStream(unmasked, writable: false))
    using (var lzw = new CompressStream(source, CompressionStreamMode.Decompress, leaveOpen: true)) {
      var total = 0;
      while (total < output.Length) {
        var read = lzw.Read(output, total, output.Length - total);
        if (read == 0)
          throw new InvalidDataException($"DiskDoubler compress fork '{entry.Name}' ends after {total} of {output.Length} bytes.");
        total += read;
      }
    }

    var sum = 0;
    for (var i = 0; i < output.Length; ++i) {
      output[i] ^= entry.XorKey;
      sum += output[i];
    }
    // The stored check is the byte sum of the output plus the three (unmasked) header bytes.
    var expected = (entry.Checksum - header[0] - header[1] - header[2]) & 0xFFFF;
    if ((sum & 0xFFFF) != expected)
      throw new InvalidDataException($"DiskDoubler compress fork '{entry.Name}' fails its checksum.");
    return output;
  }

  // ── Parsing ───────────────────────────────────────────────────────────────────

  private static bool StartsWithHeader(ReadOnlySpan<byte> data) =>
    data.Length >= DiskDoublerConstants.HeaderSize &&
    BinaryPrimitives.ReadUInt32BigEndian(data) == DiskDoublerConstants.Magic;

  private static string DefaultName(Stream stream) {
    var path = (stream as FileStream)?.Name;
    if (string.IsNullOrEmpty(path))
      return "file";
    var name = Path.GetFileName(path);
    return name.EndsWith(".dd", StringComparison.OrdinalIgnoreCase) && name.Length > 3 ? name[..^3] : name;
  }

  private void ParseHeader(string name) {
    var origin = this._stream.Position;
    var probe = new byte[DiskDoublerConstants.MacBinaryHeaderSize + DiskDoublerConstants.HeaderSize];
    var got = this._stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
    var span = probe.AsSpan(0, got);

    int headerAt;
    if (StartsWithHeader(span))
      headerAt = 0;
    else if (got >= DiskDoublerConstants.MacBinaryHeaderSize && StartsWithHeader(span[DiskDoublerConstants.MacBinaryHeaderSize..]))
      headerAt = DiskDoublerConstants.MacBinaryHeaderSize;
    else
      throw new InvalidDataException("Stream does not start with a DiskDoubler header (magic 0xABCD0054).");

    var hdr = span.Slice(headerAt, DiskDoublerConstants.HeaderSize);
    var storedCrc = BinaryPrimitives.ReadUInt16BigEndian(hdr[82..]);
    if (storedCrc != 0 && storedCrc != Crc16Ccitt.Compute(hdr[..DiskDoublerConstants.HeaderCrcSpan]))
      throw new InvalidDataException("DiskDoubler header fails its CRC-16.");

    var dataSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
    var dataCompSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
    var rsrcSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[12..]);
    var rsrcCompSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[16..]);
    var info1 = hdr[22];
    var info2 = hdr[52];
    var xorKey = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
    var modified = BinaryPrimitives.ReadUInt32BigEndian(hdr[24..]);
    var fileType = Encoding.Latin1.GetString(hdr.Slice(32, 4));
    var creator = Encoding.Latin1.GetString(hdr.Slice(36, 4));

    var dataStart = origin + headerAt + DiskDoublerConstants.HeaderSize;
    if (dataStart + (long)dataCompSize + rsrcCompSize > this._stream.Length)
      throw new InvalidDataException("DiskDoubler file is shorter than its header's fork sizes.");

    DiskDoublerEntry Fork(string entryName, uint size, uint compSize, int method, int checksumAt, int deltaAt, long offset, bool isData) => new() {
      Name = entryName,
      Method = method & 0x7F,
      OriginalSize = size,
      CompressedSize = compSize,
      DataOffset = offset,
      IsDataFork = isData,
      Checksum = BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(headerAt + checksumAt)),
      DeltaType = BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(headerAt + deltaAt)),
      FileType = fileType,
      Creator = creator,
      Modified = modified == 0 ? null : DateTime.UnixEpoch.AddSeconds(modified - DiskDoublerConstants.MacEpochOffsetSeconds),
      XorKey = xorKey,
    };

    if (dataSize > 0 || rsrcSize == 0)
      this._entries.Add(Fork(name, dataSize, dataCompSize, hdr[20], 48, 54, dataStart, true));
    if (rsrcSize > 0)
      this._entries.Add(Fork(name + ".rsrc", rsrcSize, rsrcCompSize, hdr[21], 50, 56, dataStart + dataCompSize, false));
  }
}
