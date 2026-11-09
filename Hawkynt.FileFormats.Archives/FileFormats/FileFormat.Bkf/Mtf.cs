using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// Microsoft Tape Format 1.00a (Seagate Software, 1997/2000) structures shared by the BKF
/// reader, writer and editor: the 52-byte Common Block Header (MTF_DB_HDR, §5.1), the 22-byte
/// Stream Header (MTF_STREAM_HDR, §6.1), MTF_TAPE_ADDRESS strings (§4.2), MTF_DATE_TIME
/// (§4.3) and the checksums.
/// </summary>
internal static class Mtf {
  public const int HeaderSize = 52;
  public const int StreamHeaderSize = 22;

  // Common Block Header field offsets (§5.1).
  public const int OffType = 0;
  public const int OffAttributes = 4;
  public const int OffFirstEvent = 8;
  public const int OffOsId = 10;
  public const int OffOsVersion = 11;
  public const int OffDisplayableSize = 12;
  public const int OffFormatLogicalAddress = 20;
  public const int OffControlBlockId = 36;
  public const int OffOsSpecificData = 44;
  public const int OffStringType = 48;
  public const int OffHeaderChecksum = 50;

  public const byte NoStrings = 0;
  public const byte AnsiStrings = 1;
  public const byte UnicodeStrings = 2;

  /// <summary>OS ID 14 (Windows NT, Appendix A).</summary>
  public const byte OsWindowsNt = 14;

  public const uint TapeSoftFilemarkBit = 1u << 0;   // TAPE Attributes BIT0
  public const uint SsetNormalBit = 1u << 2;          // SSET/ESET Attributes BIT2
  public const uint VolbDevDriveBit = 1u << 2;        // VOLB Attributes BIT2
  public const uint DirbEmptyBit = 1u << 16;
  public const uint DirbPathInStreamBit = 1u << 17;
  public const uint FileNameInStreamBit = 1u << 17;
  public const uint BlockContinuationBit = 1u << 0;   // any DBLK, BIT0
  public const uint SsetEncryptionBit = 1u << 17;     // SSET block attributes

  public const ushort StreamChecksummed = 1 << 5;     // Stream Media Format Attributes BIT5
  public const ushort StreamCompressed = 1 << 4;
  public const ushort StreamEncrypted = 1 << 3;
  public const ushort StreamVariable = 1 << 1;
  public const ushort StreamIsSparse = 1 << 3;        // Stream File System Attributes BIT3

  public static uint Id(string fourCc) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(fourCc));

  public static string IdText(ReadOnlySpan<byte> four) => Encoding.ASCII.GetString(four[..4]);

  /// <summary>Word-wise (16-bit, little-endian) XOR of the given bytes — the header checksums.</summary>
  public static ushort Xor16(ReadOnlySpan<byte> bytes) {
    ushort x = 0;
    for (var i = 0; i + 1 < bytes.Length; i += 2) x ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
    return x;
  }

  /// <summary>
  /// The CSUM stream's 32-bit XOR of the stream data (§6.2.1.4): little-endian words, the
  /// tail padded with zeros, so any segmentation gives the same sum.
  /// </summary>
  public static uint Xor32(ReadOnlySpan<byte> data) {
    uint x = 0;
    var i = 0;
    for (; i + 4 <= data.Length; i += 4) x ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
    if (i < data.Length) {
      Span<byte> tail = stackalloc byte[4];
      tail.Clear();
      data[i..].CopyTo(tail);
      x ^= BinaryPrimitives.ReadUInt32LittleEndian(tail);
    }
    return x;
  }

  public static bool HeaderChecksumOk(ReadOnlySpan<byte> header)
    => header.Length >= HeaderSize && Xor16(header[..OffHeaderChecksum]) == BinaryPrimitives.ReadUInt16LittleEndian(header[OffHeaderChecksum..]);

  public static void SealHeader(Span<byte> header)
    => BinaryPrimitives.WriteUInt16LittleEndian(header[OffHeaderChecksum..], Xor16(header[..OffHeaderChecksum]));

  public static bool StreamChecksumOk(ReadOnlySpan<byte> streamHeader)
    => streamHeader.Length >= StreamHeaderSize && Xor16(streamHeader[..20]) == BinaryPrimitives.ReadUInt16LittleEndian(streamHeader[20..]);

  /// <summary>A sealed 22-byte Stream Header.</summary>
  public static byte[] StreamHeader(string id, long length, ushort fsAttributes = 0, ushort mediaAttributes = 0) {
    var h = new byte[StreamHeaderSize];
    Encoding.ASCII.GetBytes(id, h);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), fsAttributes);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), mediaAttributes);
    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), (ulong)length);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), Xor16(h.AsSpan(0, 20)));
    return h;
  }

  /// <summary>MTF_DATE_TIME: 14-bit year, 4-bit month, 5-bit day, 5-bit hour, 6-bit minute, 6-bit second, most significant first.</summary>
  public static void WriteDate(Span<byte> five, DateTime? when) {
    if (when is not { } t) {
      five[..5].Clear();
      return;
    }
    var v = ((ulong)(uint)t.Year << 26) | ((ulong)(uint)t.Month << 22) | ((ulong)(uint)t.Day << 17)
          | ((ulong)(uint)t.Hour << 12) | ((ulong)(uint)t.Minute << 6) | (uint)t.Second;
    for (var i = 4; i >= 0; --i) {
      five[i] = (byte)v;
      v >>= 8;
    }
  }

  public static DateTime? ReadDate(ReadOnlySpan<byte> five) {
    ulong v = 0;
    for (var i = 0; i < 5; ++i) v = (v << 8) | five[i];
    if (v == 0) return null;
    try {
      return new DateTime((int)(v >> 26), (int)((v >> 22) & 15), (int)((v >> 17) & 31),
        (int)((v >> 12) & 31), (int)((v >> 6) & 63), (int)(v & 63), DateTimeKind.Unspecified);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  public static byte[] EncodeString(string s, byte stringType)
    => stringType == AnsiStrings ? Encoding.Latin1.GetBytes(s) : Encoding.Unicode.GetBytes(s);

  public static string DecodeString(ReadOnlySpan<byte> bytes, byte stringType)
    => stringType == AnsiStrings ? Encoding.Latin1.GetString(bytes) : Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);

  /// <summary>The string an MTF_TAPE_ADDRESS (size, offset from the DBLK start) names; "" when outside the block.</summary>
  public static string ReadString(ReadOnlySpan<byte> dblk, int addressOffset, byte stringType) {
    if (addressOffset + 4 > dblk.Length) return "";
    var size = BinaryPrimitives.ReadUInt16LittleEndian(dblk[addressOffset..]);
    var offset = BinaryPrimitives.ReadUInt16LittleEndian(dblk[(addressOffset + 2)..]);
    if (size == 0 || offset + size > dblk.Length) return "";
    return DecodeString(dblk.Slice(offset, size), stringType);
  }

  public static int Align(long value, int alignment) => (int)((value + alignment - 1) / alignment * alignment);

  public static long AlignLong(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
}

/// <summary>
/// A DBLK as stored: its own bytes (Common Block Header, fixed fields, OS data and strings,
/// up to the Offset To First Event) and its data streams in order, without the closing SPAD.
/// </summary>
internal sealed class MtfBlock {
  public required byte[] Dblk;
  public List<MtfStream> Streams { get; } = [];
  public long Offset { get; init; }

  public string Type => Mtf.IdText(this.Dblk);
  public uint Attributes => BinaryPrimitives.ReadUInt32LittleEndian(this.Dblk.AsSpan(Mtf.OffAttributes));
  public byte StringType => this.Dblk[Mtf.OffStringType];
  public uint U32(int offset) => offset + 4 <= this.Dblk.Length ? BinaryPrimitives.ReadUInt32LittleEndian(this.Dblk.AsSpan(offset)) : 0;
  public string String(int addressOffset) => Mtf.ReadString(this.Dblk, addressOffset, this.StringType);
  public MtfStream? Stream(string id) => this.Streams.FirstOrDefault(s => s.Id == id);
}

/// <summary>A data stream: its 22-byte header and where its data lies (or the data itself).</summary>
internal sealed class MtfStream {
  public required byte[] Header;
  public long DataOffset { get; init; }
  public byte[]? Data { get; set; }

  public string Id => Mtf.IdText(this.Header);
  public ushort FileSystemAttributes => BinaryPrimitives.ReadUInt16LittleEndian(this.Header.AsSpan(4));
  public ushort MediaAttributes => BinaryPrimitives.ReadUInt16LittleEndian(this.Header.AsSpan(6));
  public long Length => (long)BinaryPrimitives.ReadUInt64LittleEndian(this.Header.AsSpan(8));
}

/// <summary>One Data Set: its SSET, the blocks between (VOLB, DIRB, FILE, CFIL…) and its ESET.</summary>
internal sealed class MtfDataSet {
  public required MtfBlock Sset;
  public List<MtfBlock> Blocks { get; } = [];
  public MtfBlock? Eset { get; set; }

  /// <summary>The second ESET a Type 1 Media Based Catalog writes after its streams (§7.3.1).</summary>
  public MtfBlock? CatalogEset { get; set; }
  public int ExtraEsets { get; set; }
}

/// <summary>A parsed MTF medium: the TAPE DBLK and its Data Sets.</summary>
internal sealed class MtfMedia {
  public required MtfBlock Tape;
  public int FormatLogicalBlockSize { get; init; }
  public List<MtfDataSet> Sets { get; } = [];
  public bool HasEndOfTapeMarker { get; set; }

  public ushort MediaBasedCatalogType => BinaryPrimitives.ReadUInt16LittleEndian(this.Tape.Dblk.AsSpan(66));
  public ushort SoftFilemarkBlockSize => BinaryPrimitives.ReadUInt16LittleEndian(this.Tape.Dblk.AsSpan(64));

  /// <summary>
  /// Walks the medium Format Logical Block by Format Logical Block (§3.4.2, §3.5): a DBLK
  /// starts on a block boundary with a valid header checksum, its streams follow at the
  /// Offset To First Event on 4-byte boundaries, and its SPAD stream ends on the next block
  /// boundary. Soft filemarks (SFMB) occupy whole blocks and carry no streams. Anything else
  /// on a boundary — padding, a damaged block — is stepped over, as the spec's error
  /// recovery asks.
  /// </summary>
  /// <param name="keepData">Load every stream's data (for an edit that writes the medium anew).</param>
  public static MtfMedia Read(Stream stream, bool keepData) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("MTF reading needs a seekable stream.", nameof(stream));
    var length = stream.Length;
    var head = ReadAt(stream, 0, Math.Min(1024, (int)Math.Min(length, 1024)));
    if (head.Length < 94 || Mtf.IdText(head) != "TAPE")
      throw new InvalidDataException("BKF: the medium does not start with an MTF TAPE descriptor block.");
    if (!Mtf.HeaderChecksumOk(head))
      throw new InvalidDataException("BKF: the TAPE descriptor block's header checksum does not match.");
    var flb = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(84));
    if (flb is not (512 or 1024))
      throw new InvalidDataException($"BKF: Format Logical Block Size {flb} is not 512 or 1024 (MTF 1.00a §5.2.1).");
    if (head[93] != 1)
      throw new InvalidDataException($"BKF: MTF major version {head[93]} is not 1.");

    MtfMedia? media = null;
    MtfDataSet? set = null;
    long pos = 0;
    while (pos + Mtf.HeaderSize <= length) {
      var header = ReadAt(stream, pos, Mtf.HeaderSize);
      if (!IsFourCc(header) || !Mtf.HeaderChecksumOk(header)) {
        pos += flb;
        continue;
      }
      var type = Mtf.IdText(header);
      var firstEvent = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(Mtf.OffFirstEvent));
      if (type == "SFMB") {
        pos += Math.Max(flb, Mtf.Align(Math.Max(1, (int)(media?.SoftFilemarkBlockSize ?? 2)) * 512, flb));
        continue;
      }

      var dblkLength = Math.Max(Mtf.HeaderSize, (int)Math.Min(firstEvent, length - pos));
      var block = new MtfBlock { Dblk = ReadAt(stream, pos, dblkLength), Offset = pos };
      var next = pos + flb;
      var at = pos + firstEvent;
      while (firstEvent >= Mtf.HeaderSize && at + Mtf.StreamHeaderSize <= length) {
        var sh = ReadAt(stream, at, Mtf.StreamHeaderSize);
        if (!Mtf.StreamChecksumOk(sh)) break;
        var s = new MtfStream { Header = sh, DataOffset = at + Mtf.StreamHeaderSize };
        if (s.Length < 0 || s.DataOffset + s.Length > length)
          throw new InvalidDataException($"BKF: a {s.Id} stream of the {type} block at {pos} runs past the end of the medium.");
        if (s.Id == "SPAD") {
          next = Math.Max(next, Mtf.AlignLong(s.DataOffset + s.Length, flb));
          break;
        }
        if (keepData) s.Data = ReadAt(stream, s.DataOffset, checked((int)s.Length));
        block.Streams.Add(s);
        at = Mtf.AlignLong(s.DataOffset + s.Length, 4);
        next = Math.Max(next, Mtf.AlignLong(at, flb));
      }

      switch (type) {
        case "TAPE" when media == null:
          media = new MtfMedia { Tape = block, FormatLogicalBlockSize = flb };
          break;
        case "SSET":
          set = new MtfDataSet { Sset = block };
          (media ?? throw new InvalidDataException("BKF: a Data Set before the TAPE block.")).Sets.Add(set);
          break;
        case "ESET":
          var owner = media?.Sets.LastOrDefault();
          if (owner == null) break;
          if (owner.Eset == null) owner.Eset = block;
          else if (owner.CatalogEset == null) owner.CatalogEset = block;  // a Type 1 catalog repeats the ESET
          else ++owner.ExtraEsets;
          set = null;
          break;
        case "EOTM":
          if (media != null) media.HasEndOfTapeMarker = true;
          break;
        default:
          set?.Blocks.Add(block);
          break;
      }
      pos = next;
    }
    return media ?? throw new InvalidDataException("BKF: no TAPE descriptor block.");
  }

  private static bool IsFourCc(ReadOnlySpan<byte> b) {
    for (var i = 0; i < 4; ++i)
      if (b[i] is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')) return false;
    return true;
  }

  public static byte[] ReadAt(Stream stream, long offset, int count) {
    var buffer = new byte[count];
    stream.Position = offset;
    stream.ReadExactly(buffer);
    return buffer;
  }
}
