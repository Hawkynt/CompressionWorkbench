using System.Buffers.Binary;
using System.Text;

namespace FileFormat.NowCompress;

/// <summary>One file or folder of a Now Compress archive.</summary>
public sealed class NowCompressEntry {
  /// <summary>Gets the path inside the archive, '/'-separated.</summary>
  public string Path { get; init; } = "";
  /// <summary>Gets whether this is a folder.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Gets the data fork size.</summary>
  public long DataSize { get; init; }
  /// <summary>Gets the resource fork size.</summary>
  public long ResourceSize { get; init; }
  /// <summary>Gets the modification time.</summary>
  public DateTime? LastModified { get; init; }
  /// <summary>Gets the decoded data fork.</summary>
  public byte[] Data { get; internal set; } = [];
  /// <summary>Gets the decoded resource fork.</summary>
  public byte[] Resource { get; internal set; } = [];

  internal uint StreamOffset { get; init; }
}

/// <summary>
/// Now Compress (Now Software, Macintosh, early 1990s): a directory of 110-byte records followed by
/// one stream per file, each a checksummed table of blocks; blocks are stored or compressed with a
/// 32 KiB LZSS, a static Huffman code, both, or a later Huffman-coded LZSS. Each file's stream
/// carries the resource fork, then the data fork. The format stores no checksum of the data itself,
/// so decoding errors surface only as structural failures.
/// </summary>
/// <remarks>Converted from XADMaster's XADNowCompressParser / XADNowCompressHandle (LGPL-2.1-or-later).</remarks>
public static class NowCompressArchive {
  private const int RecordSize = 110;
  private const string MacRomanHigh =
    "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

  /// <summary>Whether the bytes start like a Now Compress archive (magic, file count, first name, header checksum).</summary>
  public static bool IsNowCompress(ReadOnlySpan<byte> b) {
    if (b.Length < 134 || b[0] != 0x00 || b[1] != 0x02) return false;
    var count = BinaryPrimitives.ReadUInt32BigEndian(b[8..]);
    if (count == 0 || count > 0xFFFF) return false;
    int nameLength = b[24];
    if (nameLength is 0 or > 31) return false;
    for (var i = 0; i < nameLength; ++i) if (b[25 + i] < 32) return false;
    uint sum = 0;
    for (var i = 24; i < 130; ++i) sum += b[i];
    return sum == BinaryPrimitives.ReadUInt32BigEndian(b[130..]);
  }

  /// <summary>Parses the directory and decodes every fork.</summary>
  /// <exception cref="InvalidDataException">Not a Now Compress archive, or a stream is damaged.</exception>
  public static List<NowCompressEntry> Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var b = ms.ToArray();
    if (!IsNowCompress(b)) throw new InvalidDataException("Not a Now Compress archive.");

    var total = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
    var entries = new List<NowCompressEntry>();
    var p = 24;
    var seen = 0u;
    ParseDirectory(b, ref p, "", int.MaxValue, total, ref seen, entries);

    // The streams form one solid sequence: a block that starts a stream restores the history the
    // previous stream's first block left behind (XADNowCompressHandle's dictionary cache).
    var decoder = new BlockDecoder(b);
    foreach (var e in entries.Where(x => !x.IsDirectory)) {
      var bytes = decoder.DecodeFile(e.StreamOffset, e.ResourceSize + e.DataSize);
      e.Resource = bytes[..(int)e.ResourceSize];
      e.Data = bytes[(int)e.ResourceSize..];
    }
    return entries;
  }

  private static void ParseDirectory(byte[] b, ref int p, string parent, int count, uint total, ref uint seen, List<NowCompressEntry> entries) {
    for (var i = 0; i < count && seen < total; ++i, ++seen) {
      if (p + RecordSize > b.Length) throw new InvalidDataException("Now Compress directory is truncated.");
      var r = b.AsSpan(p, RecordSize);
      p += RecordSize;
      int nameLength = Math.Min((int)r[0], 31);
      var name = MacRoman(r.Slice(1, nameLength)).Replace('/', ':');
      var flags = BinaryPrimitives.ReadUInt16BigEndian(r[36..]);
      var modified = BinaryPrimitives.ReadUInt32BigEndian(r[58..]);
      var children = BinaryPrimitives.ReadUInt16BigEndian(r[82..]);
      var dataSize = BinaryPrimitives.ReadUInt32BigEndian(r[86..]);
      var rsrcSize = BinaryPrimitives.ReadUInt32BigEndian(r[90..]);
      var dataStart = BinaryPrimitives.ReadUInt32BigEndian(r[98..]);
      var path = parent.Length == 0 ? name : parent + "/" + name;
      var time = modified == 0 ? (DateTime?)null : new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(modified);
      if ((flags & 0x10) != 0) {
        entries.Add(new NowCompressEntry { Path = path, IsDirectory = true, LastModified = time });
        // As in XADMaster, the folder record itself is counted only after its children.
        ParseDirectory(b, ref p, path, children, total, ref seen, entries);
      } else {
        entries.Add(new NowCompressEntry { Path = path, DataSize = dataSize, ResourceSize = rsrcSize, LastModified = time, StreamOffset = dataStart });
      }
    }
  }

  private static string MacRoman(ReadOnlySpan<byte> raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var c in raw) sb.Append(c < 0x80 ? (char)c : MacRomanHigh[c - 0x80]);
    return sb.ToString();
  }

  // ── stream decoding ─────────────────────────────────────────────────

  private sealed class BlockDecoder(byte[] archive) {
    private readonly byte[] _dictionaryCache = new byte[0x8000];
    private readonly byte[] _out = new byte[0x10000]; // 32 KiB history + 32 KiB block

    public byte[] DecodeFile(uint headerOffset, long size) {
      if (size > int.MaxValue) throw new NotSupportedException("Now Compress file exceeds the 2 GiB in-memory limit.");
      var result = new byte[size];
      var o = 0;
      var blocks = this.ReadBlockTable(headerOffset);
      foreach (var (offset, length, flags) in blocks) {
        if (o >= size) break;
        var produced = this.DecodeBlock(offset, length, flags);
        var take = Math.Min(produced.Length, (int)(size - o));
        produced.AsSpan(0, take).CopyTo(result.AsSpan(o));
        o += take;
      }
      if (o != size) throw new InvalidDataException("Now Compress stream ended before the recorded size.");
      return result;
    }

    private static int Sum32(uint v) => (int)((v >> 24) + ((v >> 16) & 0xFF) + ((v >> 8) & 0xFF) + (v & 0xFF));
    private static int Sum16(int v) => (v >> 8) + (v & 0xFF);

    private uint U32(long at) {
      if (at < 0 || at + 4 > archive.Length) throw new InvalidDataException("Now Compress stream header runs past the archive.");
      return BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan((int)at));
    }

    private int U16(long at) {
      if (at < 0 || at + 2 > archive.Length) throw new InvalidDataException("Now Compress stream header runs past the archive.");
      return BinaryPrimitives.ReadUInt16BigEndian(archive.AsSpan((int)at));
    }

    private List<(long Offset, int Length, int Flags)> ReadBlockTable(uint headerOffset) {
      var first = U32(headerOffset);
      // The recorded offsets are sometimes shifted; XADMaster finds the delta from the header checksum.
      if (first >= headerOffset + 20 && this.TryParse(headerOffset, first, 0, out var table)) return table;
      var delta = this.FindDelta(headerOffset, first);
      if (this.TryParse(headerOffset, first, delta, out table)) return table;
      throw new InvalidDataException("Now Compress stream header fails its checksum.");
    }

    private bool TryParse(uint headerOffset, uint first, int delta, out List<(long Offset, int Length, int Flags)> table) {
      table = [];
      try {
        var count = (int)((first + delta - headerOffset - 4) / 8) - 1;
        if (count < 0 || count > 0x100000) return false;
        var p = (long)headerOffset + 4;
        var last = first;
        long checksum = Sum32(first);
        var nextIsStart = true;
        for (var i = 0; i < count; ++i) {
          var flags = U16(p);
          var padding = U16(p + 2);
          var next = U32(p + 4);
          p += 8;
          checksum += Sum16(flags) + Sum16(padding) + Sum32(next);
          if (next == last) { nextIsStart = true; continue; }
          table.Add((last + delta, (int)(next - last - padding - 4), flags | (nextIsStart ? 0x10000 : 0)));
          last = next;
          nextIsStart = false;
        }
        for (var i = 0; i < 4; ++i) checksum += archive[p + i];
        return (uint)checksum == U32(p + 4);
      } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or OverflowException) {
        return false;
      }
    }

    private int FindDelta(uint headerOffset, uint first) {
      var p = (long)headerOffset;
      uint checksum = 0;
      for (var i = 0; i < 16; ++i) checksum += archive[p++];
      for (var n = 2; n < 0x2000; ++n) {
        if (U32(p) == checksum) return (int)(headerOffset + n * 8 + 4 - first);
        for (var i = 0; i < 8; ++i) checksum += archive[p++];
      }
      return 0;
    }

    private byte[] DecodeBlock(long offset, int length, int flags) {
      if (length < 0 || length > 0x8000 || offset < 0 || offset + length > archive.Length)
        throw new InvalidDataException("Now Compress block is out of range.");
      if ((flags & 0x10000) != 0) this._dictionaryCache.CopyTo(this._out, 0);
      var input = archive.AsSpan((int)offset, length);
      var outStart = 0x8000;
      int produced;
      if ((flags & 0x20) != 0) {
        if ((flags & 0x1F) != 0) {
          var huff = UnpackHuffman(input, 0x8000, 0x100);
          produced = UnpackLzss(huff, this._out, outStart);
        } else {
          var huff = UnpackHuffman(input, 0x8000, 0x100);
          huff.CopyTo(this._out, outStart);
          produced = huff.Length;
        }
      } else if ((flags & 0x40) != 0) {
        produced = UnpackNewLzss(input, this._out, outStart);
      } else if ((flags & 0x1F) != 0) {
        produced = UnpackLzss(input, this._out, outStart);
      } else {
        input.CopyTo(this._out.AsSpan(outStart));
        produced = length;
      }
      var block = this._out.AsSpan(outStart, produced).ToArray();
      // Slide: the last 32 KiB of history + output become the next block's history.
      Buffer.BlockCopy(this._out, produced, this._out, 0, 0x8000);
      if ((flags & 0x10000) != 0) Buffer.BlockCopy(this._out, 0, this._dictionaryCache, 0, 0x8000);
      return block;
    }
  }

  private static int UnpackLzss(ReadOnlySpan<byte> src, byte[] dst, int dstStart) {
    var s = 2;
    var d = dstStart;
    int bits = 0, numBits = 0;
    while (s < src.Length) {
      if (numBits == 0) {
        bits = src[s++];
        numBits = 8;
        if (s >= src.Length) throw new InvalidDataException("Now Compress LZSS data ends early.");
      }
      if ((bits & 0x80) != 0) {
        if (d >= dst.Length) throw new InvalidDataException("Now Compress LZSS block overflows.");
        dst[d++] = src[s++];
      } else {
        int b1 = src[s++];
        if (s >= src.Length) throw new InvalidDataException("Now Compress LZSS data ends early.");
        int b2 = src[s++];
        var offset = ((b1 & 0xF8) << 5) | b2;
        var length = b1 & 0x07;
        if (length == 0) {
          if (s >= src.Length) throw new InvalidDataException("Now Compress LZSS data ends early.");
          length = src[s++];
        }
        length += 2;
        if (d - offset < dstStart) throw new InvalidDataException("Now Compress LZSS match reaches before the block.");
        for (var i = 0; i < length; ++i) {
          if (d >= dst.Length) throw new InvalidDataException("Now Compress LZSS block overflows.");
          dst[d] = dst[d - offset];
          ++d;
        }
      }
      bits <<= 1;
      --numBits;
    }
    return d - dstStart;
  }

  private static readonly int[] LengthExtra = [0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,2,2,2,2,2,3,3,3,4,4,4,4,5,5,5,5];
  private static readonly int[] LengthBase = [0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,0x10,0x12,0x14,0x18,0x1c,0x20,0x24,0x28,0x30,0x38,0x40,0x50,0x60,0x70,0x80,0xa0,0xc0,0xe0];
  private static readonly int[] OffsetExtra = [0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,8,8,8,8,8,8,8,8,9,9,9,9,10,10,10,10,11,11,11,11,12,12,12,12,13,13,13,13];
  private static readonly int[] OffsetBase = [0x0000,0x0001,0x0002,0x0003,0x0004,0x0005,0x0006,0x0007,0x0008,0x000a,0x000c,0x000e,0x0010,0x0014,0x0018,0x001c,0x0020,0x0028,0x0030,0x0038,0x0040,0x0050,0x0060,0x0070,0x0080,0x00a0,0x00c0,0x00e0,0x0100,0x0200,0x0300,0x0400,0x0500,0x0600,0x0700,0x0800,0x0900,0x0b00,0x0d00,0x0f00,0x1100,0x1500,0x1900,0x1d00,0x2100,0x2900,0x3100,0x3900,0x4100,0x5100,0x6100,0x7100,0x8100,0xa100,0xc100,0xe100];

  private static int UnpackNewLzss(ReadOnlySpan<byte> src, byte[] dst, int dstStart) {
    if (src.Length < 4) throw new InvalidDataException("Now Compress block is too short.");
    var headerSize = BinaryPrimitives.ReadUInt16BigEndian(src) - 0x2F59;
    int endBits = src[3];
    var s = 4;
    if (headerSize < 0 || s + headerSize > src.Length) throw new InvalidDataException("Now Compress block header is out of range.");
    var header = UnpackHuffman(src.Slice(s, headerSize), 0x15A, 20);
    if (header.Length != 0x15A) throw new InvalidDataException("Now Compress block code table is incomplete.");
    s += headerSize;
    if ((s & 1) != 0) ++s;
    var mainLengths = new int[0x122];
    for (var i = 0; i < 0x122; ++i) mainLengths[i] = header[i];
    var offsetLengths = new int[0x38];
    for (var i = 0; i < 0x38; ++i) offsetLengths[i] = header[i + 0x122] != 0 ? header[i + 0x122] - 2 : 0;
    var main = new Canonical(mainLengths, 20);
    var offs = new Canonical(offsetLengths, 20);
    var reader = new MsbReader(src[s..]);
    long numBits = (long)(src.Length - s) * 8;
    if (endBits != 0) numBits -= 16 - endBits;
    var d = dstStart;
    while (reader.Position < numBits) {
      var symbol = main.Decode(ref reader);
      if (symbol < 0x100) {
        if (d >= dst.Length) throw new InvalidDataException("Now Compress block overflows.");
        dst[d++] = (byte)symbol;
        continue;
      }
      var ls = symbol - 0x100;
      var length = LengthBase[ls] + (LengthExtra[ls] != 0 ? reader.Bits(LengthExtra[ls]) : 0) + 2;
      var os = offs.Decode(ref reader);
      var offset = OffsetBase[os] + (OffsetExtra[os] != 0 ? reader.Bits(OffsetExtra[os]) : 0);
      if (d + length > dst.Length || d - offset < 0) throw new InvalidDataException("Now Compress match is out of range.");
      for (var i = 0; i < length; ++i) { dst[d] = dst[d - offset]; ++d; }
    }
    return d - dstStart;
  }

  private static byte[] UnpackHuffman(ReadOnlySpan<byte> src, int maxOutput, int numValues) {
    var s = 0;
    if (s >= src.Length) throw new InvalidDataException("Now Compress Huffman block is empty.");
    int endBits = src[s++];
    var lengths = new int[numValues];
    for (var i = 0; i < numValues / 2; ++i) {
      if (s >= src.Length) throw new InvalidDataException("Now Compress Huffman table is truncated.");
      var v = src[s++];
      lengths[2 * i] = v >> 4;
      lengths[2 * i + 1] = v & 0x0F;
    }
    if (s >= src.Length) throw new InvalidDataException("Now Compress Huffman table is truncated.");
    int extra = src[s++];
    for (var i = 0; i < extra; ++i) {
      if (s >= src.Length) throw new InvalidDataException("Now Compress Huffman table is truncated.");
      var index = src[s++];
      if (index >= numValues) throw new InvalidDataException("Now Compress Huffman table index is out of range.");
      lengths[index] += 16;
    }
    var code = new Canonical(lengths, 31);
    if ((s & 1) != 0) ++s;
    var reader = new MsbReader(src[Math.Min(s, src.Length)..]);
    long numBits = (long)(src.Length - Math.Min(s, src.Length)) * 8;
    if (endBits != 0) numBits -= 16 - endBits;
    var output = new List<byte>(Math.Min(maxOutput, 0x8000));
    while (reader.Position < numBits) {
      if (output.Count >= maxOutput) throw new InvalidDataException("Now Compress Huffman block overflows.");
      output.Add((byte)code.Decode(ref reader));
    }
    if (reader.Position != numBits) throw new InvalidDataException("Now Compress Huffman block has trailing bits.");
    return [.. output];
  }

  private ref struct MsbReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    public long Position { get; private set; }

    public int Bit() {
      var i = (int)(this.Position >> 3);
      if (i >= this._data.Length) throw new InvalidDataException("Now Compress bit stream ended early.");
      var bit = (this._data[i] >> (7 - (int)(this.Position & 7))) & 1;
      ++this.Position;
      return bit;
    }

    public int Bits(int n) {
      var v = 0;
      for (var i = 0; i < n; ++i) v = (v << 1) | this.Bit();
      return v;
    }
  }

  /// <summary>Canonical prefix code, shortest code all zeros, decoded MSB first.</summary>
  private sealed class Canonical {
    private readonly int[] _count;
    private readonly int[] _symbols;
    private readonly int _maxLen;

    public Canonical(int[] lengths, int maxLen) {
      this._maxLen = maxLen;
      this._count = new int[maxLen + 1];
      var n = 0;
      foreach (var l in lengths) {
        if (l > maxLen) throw new InvalidDataException("Now Compress code length is too long.");
        if (l > 0) { ++this._count[l]; ++n; }
      }
      this._symbols = new int[n];
      var offs = new int[maxLen + 2];
      for (var len = 1; len <= maxLen; ++len) offs[len + 1] = offs[len] + this._count[len];
      for (var sym = 0; sym < lengths.Length; ++sym)
        if (lengths[sym] > 0) this._symbols[offs[lengths[sym]]++] = sym;
    }

    public int Decode(ref MsbReader r) {
      int code = 0, first = 0, index = 0;
      for (var len = 1; len <= this._maxLen; ++len) {
        code |= r.Bit();
        var count = this._count[len];
        if (code - first < count) return this._symbols[index + code - first];
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
      }
      throw new InvalidDataException("Now Compress stream uses an unassigned code.");
    }
  }
}
