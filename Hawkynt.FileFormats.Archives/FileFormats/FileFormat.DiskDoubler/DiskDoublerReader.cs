using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Streams;
using FileFormat.Compress;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Reads a DiskDoubler compressed file, or a DDAR or DDA2 archive of them, and decodes the forks.
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
/// <para>An archive ("DDAR" or "DDA2", also behind MacBinary) holds folders and files: each file is
/// either one of these compressed files, header included, or (DDAR, Finder flag 0x20) its forks as
/// they are. Entry names are Mac Roman, '/' within a name shown as ':'.</para>
/// <para>Decoded methods: 0 (stored), 1 (Unix <c>compress</c>, with its own header XOR-masked by 0x5A and
/// a 16-bit byte-sum check), 2 and 5 (adaptive splay trees, 256 or a stored number of them, byte-sum
/// check), 6 and 9 ("AD" LZSS blocks, see <see cref="DiskDoublerAdDecoder"/>), 7 (Stac LZS on
/// bit-inverted data behind an 18-byte block table, XOR-sum check) and 8 (Compact Pro RLE, CRC-16 check,
/// with or without its LZH layer). Delta type 1 (each byte plus the one before) is undone after
/// decoding. Method 3 (RLE), 4 (Huffman), 10 ("DD") and other delta types are refused with <see cref="NotSupportedException"/> rather than returned undecoded.</para>
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

  /// <summary>
  /// Gets where a DDAR archive continues with records that are not entries (null when it ends
  /// cleanly); the entries listed are the ones before that point.
  /// </summary>
  public long? UnreadTailOffset { get; private set; }

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

    if (entry.DeltaType is not (0 or 1))
      throw new NotSupportedException($"DiskDoubler fork '{entry.Name}' uses delta pre-processing type {entry.DeltaType}, which is not supported.");

    this._stream.Position = entry.DataOffset;
    var compressed = new byte[entry.CompressedSize];
    this._stream.ReadExactly(compressed);

    var data = Decode(entry, compressed);
    if (entry.DeltaType == 1)
      DiskDoublerCodecs.UndoDelta(data);
    return data;
  }

  private static byte[] Decode(DiskDoublerEntry entry, byte[] compressed) {
    if (entry.OriginalSize > Array.MaxLength)
      throw new NotSupportedException($"DiskDoubler fork '{entry.Name}' is larger than this reader handles.");
    return entry.Method switch {
      DiskDoublerConstants.MethodStored => entry.OriginalSize <= compressed.LongLength
        ? compressed[..(int)entry.OriginalSize]
        : throw new InvalidDataException($"DiskDoubler stored fork '{entry.Name}' is shorter than its size."),
      DiskDoublerConstants.MethodCompress => DecodeCompress(entry, compressed),
      DiskDoublerConstants.MethodAds or DiskDoublerConstants.MethodAd1 => DiskDoublerAdDecoder.Decode(compressed, entry.OriginalSize),
      DiskDoublerConstants.MethodSplay => DecodeSplay(entry, compressed, 256),
      DiskDoublerConstants.MethodSplayTrees => compressed.Length == 0
        ? throw new InvalidDataException($"DiskDoubler fork '{entry.Name}' lacks its tree count.")
        : DecodeSplay(entry, compressed.AsSpan(1).ToArray(), compressed[0] == 0 ? 256 : compressed[0]),
      DiskDoublerConstants.MethodLzs => DecodeLzs(entry, compressed),
      DiskDoublerConstants.MethodCompactPro => DecodeCompactPro(entry, compressed),
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

  private static byte[] DecodeSplay(DiskDoublerEntry entry, byte[] compressed, int trees) {
    var output = DiskDoublerCodecs.SplayDecode(compressed, entry.OriginalSize, trees);
    var sum = 0;
    for (var i = 0; i < output.Length; ++i) {
      output[i] ^= entry.XorKey;
      sum += output[i];
    }
    if ((sum & 0xFFFF) != entry.Checksum)
      throw new InvalidDataException($"DiskDoubler fork '{entry.Name}' fails its checksum.");
    return output;
  }

  /// <summary>Method 7: a block table (6 bytes, a 32-bit entry count, 8 bytes, 2 bytes per entry), then bit-inverted Stac LZS.</summary>
  private static byte[] DecodeLzs(DiskDoublerEntry entry, byte[] compressed) {
    if (compressed.Length < 18)
      throw new InvalidDataException($"DiskDoubler LZS fork '{entry.Name}' is shorter than its table.");
    var entries = BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(6));
    var start = 18L + 2L * entries;
    if (start > compressed.Length)
      throw new InvalidDataException($"DiskDoubler LZS fork '{entry.Name}' has a table past its end.");
    var inverted = compressed.AsSpan((int)start).ToArray();
    for (var i = 0; i < inverted.Length; ++i)
      inverted[i] ^= 0xFF;
    var output = DiskDoublerCodecs.LzsDecode(inverted, entry.OriginalSize);
    byte xor = 0;
    for (var i = 0; i < output.Length; ++i)
      xor ^= output[i] ^= 0xFF;
    var expected = (byte)(entry.Checksum ^ ((output.Length & 1) == 0 ? 0xFF : 0));
    if (xor != expected)
      throw new InvalidDataException($"DiskDoubler LZS fork '{entry.Name}' fails its checksum.");
    return output;
  }

  /// <summary>
  /// Method 8: a 16-byte prefix, then Compact Pro's RLE, behind its LZH layer (with 0xFFF0-unit blocks)
  /// when the prefix bytes are all zero; CRC-16 check.
  /// </summary>
  private static byte[] DecodeCompactPro(DiskDoublerEntry entry, byte[] compressed) {
    if (compressed.Length < 16)
      throw new InvalidDataException($"DiskDoubler Compact Pro fork '{entry.Name}' is shorter than its prefix.");
    var sum = 0;
    for (var i = 0; i < 16; ++i)
      sum += compressed[i];
    var output = FileFormat.CompactPro.CompactProCodec.DecodeFork(compressed.AsSpan(16), entry.OriginalSize, lzh: sum == 0, lzhBlockSize: 0xFFF0);
    if (Crc16Arc(output) != entry.Checksum)
      throw new InvalidDataException($"DiskDoubler Compact Pro fork '{entry.Name}' fails its CRC.");
    return output;
  }

  /// <summary>CRC-16 with the reflected polynomial 0xA001, starting at 0 (the IBM CRC-16 Compact Pro uses).</summary>
  internal static ushort Crc16Arc(ReadOnlySpan<byte> data) {
    var crc = 0;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
    }
    return (ushort)crc;
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

  private const uint DdarMagic = 0x44444152; // "DDAR"
  private const uint Dda2Magic = 0x44444132; // "DDA2"
  private const int DdarHeaderSize = 78, Dda2HeaderSize = 62;

  private static uint MagicAt(ReadOnlySpan<byte> data, int at)
    => data.Length >= at + 4 ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : 0;

  private void ParseHeader(string name) {
    var origin = this._stream.Position;
    var probe = new byte[DiskDoublerConstants.MacBinaryHeaderSize + DiskDoublerConstants.HeaderSize];
    var got = this._stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
    var span = probe.AsSpan(0, got);

    var headerAt = -1;
    foreach (var at in new[] { 0, DiskDoublerConstants.MacBinaryHeaderSize })
      if (MagicAt(span, at) is DiskDoublerConstants.Magic or DdarMagic or Dda2Magic) {
        headerAt = at;
        break;
      }
    if (headerAt < 0)
      throw new InvalidDataException("Stream does not start with a DiskDoubler header (magic 0xABCD0054, DDAR or DDA2).");

    var start = origin + headerAt;
    switch (MagicAt(span, headerAt)) {
      case DdarMagic:
        this.ParseArchive(start);
        break;
      case Dda2Magic:
        this.ParseArchive2(start);
        break;
      default:
        this.ParseFile(start, name);
        break;
    }
  }

  private byte[] ReadAt(long offset, int count) {
    if (offset < 0 || offset + count > this._stream.Length)
      throw new InvalidDataException("DiskDoubler structure runs past the end of the file.");
    var buffer = new byte[count];
    this._stream.Position = offset;
    this._stream.ReadExactly(buffer);
    return buffer;
  }

  private static void CheckCrc(ReadOnlySpan<byte> header, int span, string what) {
    var stored = BinaryPrimitives.ReadUInt16BigEndian(header[span..]);
    if (stored != 0 && stored != Crc16Ccitt.Compute(header[..span]))
      throw new InvalidDataException($"DiskDoubler {what} fails its CRC-16.");
  }

  /// <summary>One compressed file: its 84-byte header at <paramref name="at"/>, then its forks. Returns the forks' total compressed size.</summary>
  private long ParseFile(long at, string name) {
    var hdr = this.ReadAt(at, DiskDoublerConstants.HeaderSize);
    if (BinaryPrimitives.ReadUInt32BigEndian(hdr) != DiskDoublerConstants.Magic)
      throw new InvalidDataException($"DiskDoubler file header of '{name}' lacks its magic.");
    CheckCrc(hdr, DiskDoublerConstants.HeaderCrcSpan, $"file header of '{name}'");

    var dataSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4));
    var dataCompSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(8));
    var rsrcSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(12));
    var rsrcCompSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(16));
    var info1 = hdr[22];
    var info2 = hdr[52];
    var xorKey = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
    var modified = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(24));
    var fileType = Encoding.Latin1.GetString(hdr, 32, 4);
    var creator = Encoding.Latin1.GetString(hdr, 36, 4);

    var dataStart = at + DiskDoublerConstants.HeaderSize;
    if (dataStart + (long)dataCompSize + rsrcCompSize > this._stream.Length)
      throw new InvalidDataException($"DiskDoubler file '{name}' is shorter than its header's fork sizes.");

    DiskDoublerEntry Fork(string entryName, uint size, uint compSize, int method, int checksumAt, int deltaAt, long offset, bool isData) => new() {
      Name = entryName,
      Method = method & 0x7F,
      OriginalSize = size,
      CompressedSize = compSize,
      DataOffset = offset,
      IsDataFork = isData,
      Checksum = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(checksumAt)),
      DeltaType = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(deltaAt)),
      FileType = fileType,
      Creator = creator,
      Modified = MacTime(modified),
      XorKey = xorKey,
    };

    if (dataSize > 0 || rsrcSize == 0)
      this._entries.Add(Fork(name, dataSize, dataCompSize, hdr[20], 48, 54, dataStart, true));
    if (rsrcSize > 0)
      this._entries.Add(Fork(name + ".rsrc", rsrcSize, rsrcCompSize, hdr[21], 50, 56, dataStart + dataCompSize, false));
    return (long)dataCompSize + rsrcCompSize;
  }

  private static DateTime? MacTime(uint seconds)
    => seconds == 0 ? null : DateTime.UnixEpoch.AddSeconds(seconds - DiskDoublerConstants.MacEpochOffsetSeconds);

  private static string EntryName(ReadOnlySpan<byte> raw, int length, int max) {
    var name = MacRoman.GetString(raw[..Math.Min(length, max)]).Replace('/', ':');
    return name.Length == 0 ? "unnamed" : name;
  }

  private static Encoding MacRoman {
    get {
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      return Encoding.GetEncoding(10000);
    }
  }

  private static string Join(List<string> dirs, string name) => dirs.Count == 0 ? name : string.Join('/', dirs) + "/" + name;

  /// <summary>
  /// A DDAR archive: a 78-byte header, then 124-byte entries — a directory, the end of a directory,
  /// a file stored as is (Finder flag 0x20: data then resource fork), or a compressed file whose own
  /// 84-byte header follows. Stray file headers between entries are skipped.
  /// </summary>
  private void ParseArchive(long at) {
    CheckCrc(this.ReadAt(at, DdarHeaderSize), DdarHeaderSize - 2, "archive header");
    var dirs = new List<string>();
    var pos = at + DdarHeaderSize;
    while (pos + 4 <= this._stream.Length) {
      var magic = BinaryPrimitives.ReadUInt32BigEndian(this.ReadAt(pos, 4));
      if (magic == DiskDoublerConstants.Magic) {
        pos += DiskDoublerConstants.HeaderSize;
        continue;
      }
      if (magic != DdarMagic) {
        // Real archives can end in records this reader does not know; the entries before them stand.
        if (this._entries.Count == 0)
          throw new InvalidDataException($"DiskDoubler archive entry at {pos} lacks its DDAR magic.");
        this.UnreadTailOffset = pos;
        return;
      }
      var e = this.ReadAt(pos, 124);
      var name = EntryName(e.AsSpan(9), e[8], 63);
      var isDir = e[72] != 0;
      var endDir = e[73] != 0;
      var dataSize = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(74));
      var rsrcSize = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(78));
      var modified = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(86));
      var fileType = Encoding.Latin1.GetString(e, 90, 4);
      var creator = Encoding.Latin1.GetString(e, 94, 4);
      var finderFlags = BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(98));
      var start = pos + 124;
      var path = Join(dirs, name);
      if (endDir) {
        if (dirs.Count > 0)
          dirs.RemoveAt(dirs.Count - 1);
        pos = start;
      } else if (isDir) {
        this._entries.Add(new DiskDoublerEntry { Name = path, IsDirectory = true, Modified = MacTime(modified), DataOffset = start });
        dirs.Add(name);
        pos = start;
      } else if ((finderFlags & 0x20) != 0) {
        if (start + (long)dataSize + rsrcSize > this._stream.Length)
          throw new InvalidDataException($"DiskDoubler archive entry '{path}' runs past the end of the file.");
        DiskDoublerEntry Stored(string n, uint size, long offset, bool isData) => new() {
          Name = n, Method = DiskDoublerConstants.MethodStored, OriginalSize = size, CompressedSize = size,
          DataOffset = offset, IsDataFork = isData, FileType = fileType, Creator = creator, Modified = MacTime(modified),
        };
        if (dataSize > 0 || rsrcSize == 0)
          this._entries.Add(Stored(path, dataSize, start, true));
        if (rsrcSize > 0)
          this._entries.Add(Stored(path + ".rsrc", rsrcSize, start + dataSize, false));
        pos = start + dataSize + rsrcSize;
      } else
        pos = start + DiskDoublerConstants.HeaderSize + this.ParseFile(start, path);
    }
  }

  /// <summary>
  /// A DDA2 archive: a 62-byte header, then entries of a type word (0xBBBB ends the archive), a
  /// 31-byte name, a nesting level and the entry's total size; a directory (type bit 15) or a
  /// compressed file whose 84-byte header starts 56 bytes in. Level-0 entries (the top folder) are skipped.
  /// </summary>
  private void ParseArchive2(long at) {
    CheckCrc(this.ReadAt(at, Dda2HeaderSize), Dda2HeaderSize - 2, "archive header");
    var dirs = new List<string>();
    var lastLevel = 0;
    for (var pos = at + Dda2HeaderSize; ;) {
      var lead = this.ReadAt(pos, 6);
      if (BinaryPrimitives.ReadUInt32BigEndian(lead) != Dda2Magic)
        throw new InvalidDataException($"DiskDoubler archive entry at {pos} lacks its DDA2 magic.");
      var type = BinaryPrimitives.ReadUInt16BigEndian(lead.AsSpan(4));
      if (type == 0xBBBB)
        break;
      var e = this.ReadAt(pos, 46);
      var name = EntryName(e.AsSpan(7), e[6], 31);
      var level = (int)BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(38)) - 2;
      var total = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(42));
      if (total < 46)
        throw new InvalidDataException($"DiskDoubler archive entry at {pos} is shorter than its header.");
      if (level >= 0) {
        for (var i = level; i < lastLevel && dirs.Count > 0; ++i)
          dirs.RemoveAt(dirs.Count - 1);
        lastLevel = level;
        var path = Join(dirs, name);
        if ((type & 0x8000) != 0) {
          var times = this.ReadAt(pos + 46, 16);
          this._entries.Add(new DiskDoublerEntry {
            Name = path, IsDirectory = true, Modified = MacTime(BinaryPrimitives.ReadUInt32BigEndian(times.AsSpan(12))), DataOffset = pos,
          });
          dirs.Add(name);
        } else
          this.ParseFile(pos + 56, path);
      }
      pos += total;
    }
  }
}
