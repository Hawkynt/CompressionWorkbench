using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileSystem.Fossil;

/// <summary>Venti block types as clients name them (libventi): data and directory blocks with their pointer depth, and roots.</summary>
public static class VentiType {
  public const int Data = 0, Dir = 8, Root = 16;
}

/// <summary>One block stored in a Venti arena: where its clump lies and what it says about itself.</summary>
/// <param name="Score">SHA-1 of the uncompressed block.</param>
/// <param name="Type">Client block type (<see cref="VentiType" />).</param>
/// <param name="Offset">Byte offset of the clump's data in the partition.</param>
/// <param name="StoredSize">Bytes stored on disk.</param>
/// <param name="Size">Bytes of the uncompressed block.</param>
/// <param name="Compressed">The data is whack-compressed.</param>
/// <param name="Arena">Name of the arena holding it.</param>
/// <param name="Time">Clump write time (UNIX seconds).</param>
public sealed record VentiClump(byte[] Score, int Type, long Offset, int StoredSize, int Size, bool Compressed, string Arena, uint Time);

/// <summary>
/// Reads blocks out of a Venti arena partition (as <c>fmtarenas</c> lays it out and the
/// <c>venti</c> server fills it): the partition header at 256 KiB, the arena map, and in each
/// arena a log of clumps (header + data, whack-compressed or plain) found by their SHA-1 score.
/// The index sections are not needed: every arena's clump log is scanned once.
/// </summary>
/// <remarks>
/// Converted from plan9port's MIT-licensed Venti server (<c>src/cmd/venti/srv</c>: <c>conv.c</c>,
/// <c>arenas.c</c>, <c>arena.c</c>, <c>unwhack.c</c>) and libventi (<c>dtype.c</c>,
/// <c>zero.c</c>); see <c>THIRD-PARTY-NOTICE.Fossil.txt</c> and <c>docs/FOSSIL-ON-DISK.md</c>.
/// Every block handed out is checked against its score.
/// </remarks>
public sealed class VentiArenaStore {
  public const uint ArenaPartMagic = 0xA9E4A5E7, ArenaHeadMagic = 0xD15C4EAD, ArenaMagic = 0xF2A14EAD, OldClumpMagic = 0xD15CB10C;
  public const int PartBlank = 256 * 1024, HeadSize = 512, ClumpSize = 38, ScoreSize = 20, MaxLump = 56 * 1024;

  /// <summary>SHA-1 of the empty block: Venti answers it without storing anything.</summary>
  public static readonly byte[] ZeroScore = Convert.FromHexString("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709");

  // the on-disk ("old") type numbers, mapped to client types; 0xFF is corrupt
  private static readonly int[] FromDiskType = [0xFF, VentiType.Root, VentiType.Dir, VentiType.Dir + 1, VentiType.Dir + 2,
    VentiType.Dir + 3, VentiType.Dir + 4, VentiType.Dir + 5, VentiType.Dir + 6, VentiType.Dir + 7, 0xFF, 0xFF, 0xFF, VentiType.Data];

  private readonly Stream _stream;
  private readonly Dictionary<string, VentiClump> _byScore = new(StringComparer.Ordinal);

  /// <summary>Arena names in partition order.</summary>
  public IReadOnlyList<string> Arenas { get; }

  /// <summary>Every clump, in log order.</summary>
  public IReadOnlyList<VentiClump> Clumps { get; }

  /// <summary>Partition block size.</summary>
  public int BlockSize { get; }

  /// <summary>Whether <paramref name="header" /> (the bytes from 256 KiB on) starts a Venti arena partition.</summary>
  public static bool IsArenaPartition(ReadOnlySpan<byte> header) => header.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(header) == ArenaPartMagic;

  /// <summary>Opens an arena partition and indexes its clump logs.</summary>
  public VentiArenaStore(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    if (stream.Length < PartBlank + HeadSize) throw new InvalidDataException("Image too short for a Venti arena partition header at 256 KiB.");
    var h = this.Read(PartBlank, HeadSize);
    if (U32(h, 0) != ArenaPartMagic) throw new InvalidDataException("Not a Venti arena partition: magic 0xa9e4a5e7 missing at 256 KiB.");
    if (U32(h, 4) != 3) throw new InvalidDataException($"Venti arena partition version {U32(h, 4)} is not 3.");
    var blockSize = U32(h, 8);
    var arenaBase = U32(h, 12);
    if (blockSize < 512 || blockSize > 64 * 1024 || (blockSize & (blockSize - 1)) != 0)
      throw new InvalidDataException($"Venti arena partition block size {blockSize} is not a power of two from 512 to 64 KiB.");
    this.BlockSize = (int)blockSize;
    var tabBase = (PartBlank + HeadSize + blockSize - 1) & ~(blockSize - 1);
    if (tabBase >= arenaBase || arenaBase > stream.Length) throw new InvalidDataException("Venti arena table overlaps the arenas or lies outside the image.");
    var map = ParseMap(this.Read(tabBase, (int)Math.Min(arenaBase - tabBase, 1 << 20)));
    var partitionEnd = stream.Length & ~(long)(blockSize - 1);
    var last = (long)arenaBase;
    var names = new List<string>();
    var clumps = new List<VentiClump>();
    foreach (var (name, start, stop) in map) {
      if (start < last || stop < start || stop > partitionEnd) throw new InvalidDataException($"Venti arena '{name}' lies outside or overlaps the arena space.");
      last = stop;
      names.Add(name);
      this.ScanArena(name, start, stop, clumps);
    }
    this.Arenas = names;
    this.Clumps = clumps;
    foreach (var c in clumps) this._byScore.TryAdd(Convert.ToHexString(c.Score), c);
  }

  private void ScanArena(string name, long start, long stop, List<VentiClump> into) {
    var bs = this.BlockSize;
    var head = this.Read(start, HeadSize);
    if (U32(head, 0) != ArenaHeadMagic) throw new InvalidDataException($"Venti arena '{name}' has no arena header.");
    var version = U32(head, 4);
    if (version is not (4 or 5)) throw new InvalidDataException($"Venti arena '{name}' has version {version}.");
    var headName = CString(head.AsSpan(8, 64));
    if (headName != name) throw new InvalidDataException($"Venti arena header names '{headName}', the map '{name}'.");
    var clumpMagic = version == 5 ? U32(head, 8 + 64 + 4 + 8) : OldClumpMagic;

    // the trailer's "used" bounds the clump log
    var tail = this.Read(stop - bs, Math.Min(bs, HeadSize));
    if (U32(tail, 0) != ArenaMagic) throw new InvalidDataException($"Venti arena '{name}' has no trailer.");
    var o = 8 + 64;
    var diskClumps = U32(tail, o);
    o += 16 + (version == 5 ? 4 : 0);
    var used = (long)U64(tail, o);
    o += 17;
    var clumps = diskClumps;
    if (tail[o] == 1) {                       // memstats extension: what the log really holds
      clumps = U32(tail, o + 1);
      used = (long)U64(tail, o + 9);
    }
    var dataBase = start + bs;
    var dataSize = stop - start - 2L * bs;
    if (used < 0 || used > dataSize) throw new InvalidDataException($"Venti arena '{name}' claims more data than it holds.");

    long aa = 0;
    for (var n = 0u; n < clumps; ++n) {
      if (aa + ClumpSize > used) throw new InvalidDataException($"Venti arena '{name}': clump {n} runs past the log.");
      var c = this.Read(dataBase + aa, ClumpSize);
      if (U32(c, 0) != clumpMagic) throw new InvalidDataException($"Venti arena '{name}': clump {n} has the wrong magic.");
      var diskType = c[4];
      var type = diskType < FromDiskType.Length ? FromDiskType[diskType] : 0xFF;
      int size = U16(c, 5), unc = U16(c, 7);
      var encoding = c[29];
      if (type == 0xFF) throw new InvalidDataException($"Venti arena '{name}': clump {n} has disk type {diskType}.");
      if (encoding == 1 ? size != unc : encoding != 2 || size >= unc)
        throw new InvalidDataException($"Venti arena '{name}': clump {n} has encoding {encoding} with sizes {size}/{unc}.");
      if (aa + ClumpSize + size > used) throw new InvalidDataException($"Venti arena '{name}': clump {n} runs past the log.");
      into.Add(new VentiClump(c[9..29], type, dataBase + aa + ClumpSize, size, unc, encoding == 2, name, U32(c, 34)));
      aa += ClumpSize + size;
    }
  }

  private static List<(string Name, long Start, long Stop)> ParseMap(byte[] table) {
    var end = Array.IndexOf(table, (byte)0);
    var lines = Encoding.ASCII.GetString(table, 0, end < 0 ? table.Length : end).Split('\n');
    if (lines.Length == 0 || !uint.TryParse(lines[0], out var count) || count > 31 * 1024)
      throw new InvalidDataException("Venti arena map has no element count.");
    var map = new List<(string, long, long)>();
    for (var i = 1; i <= count; ++i) {
      var f = i < lines.Length ? lines[i].Split('\t') : [];
      if (f.Length != 3 || !long.TryParse(f[1], out var start) || !long.TryParse(f[2], out var stop))
        throw new InvalidDataException($"Venti arena map line {i} is malformed.");
      map.Add((f[0], start, stop));
    }
    return map;
  }

  /// <summary>Whether a block with this score is stored (or is the empty block).</summary>
  public bool Contains(ReadOnlySpan<byte> score) => score.SequenceEqual(ZeroScore) || this._byScore.ContainsKey(Convert.ToHexString(score));

  /// <summary>The clump that holds <paramref name="score" />, if any.</summary>
  public VentiClump? Find(ReadOnlySpan<byte> score) => this._byScore.GetValueOrDefault(Convert.ToHexString(score));

  /// <summary>
  /// The block a score names, as stored (Venti drops trailing zeros), checked against the score.
  /// </summary>
  /// <exception cref="KeyNotFoundException">No arena holds the score.</exception>
  public byte[] Read(ReadOnlySpan<byte> score) {
    if (score.SequenceEqual(ZeroScore)) return [];
    var clump = this.Find(score) ?? throw new KeyNotFoundException($"Venti score {Convert.ToHexStringLower(score)} is in none of the arenas.");
    var stored = this.Read(clump.Offset, clump.StoredSize);
    byte[] data;
    if (clump.Compressed) {
      data = new byte[clump.Size];
      var n = Unwhack(data, stored);
      if (n != clump.Size) throw new InvalidDataException($"Venti clump {Convert.ToHexStringLower(score)} decompresses to {n} bytes, not {clump.Size}.");
    } else data = stored;
    if (!SHA1.HashData(data).AsSpan().SequenceEqual(score))
      throw new InvalidDataException($"Venti clump {Convert.ToHexStringLower(score)} does not hash to its score.");
    return data;
  }

  /// <summary>
  /// The block a score names, zero-extended to <paramref name="size" /> the way libventi does:
  /// data and directory blocks with zero bytes, pointer blocks with the empty block's score.
  /// </summary>
  public byte[] Read(ReadOnlySpan<byte> score, int type, int size) {
    var data = this.Read(score);
    if (data.Length > size) throw new InvalidDataException($"Venti block {Convert.ToHexStringLower(score)} holds {data.Length} bytes, more than {size}.");
    var full = new byte[size];
    data.CopyTo(full, 0);
    if ((type & 7) != 0 && type != VentiType.Root)
      // a pointer block: Venti dropped whole empty-block scores from its end
      for (var at = (data.Length + ScoreSize - 1) / ScoreSize * ScoreSize; at + ScoreSize <= size; at += ScoreSize)
        ZeroScore.CopyTo(full, at);
    return full;
  }

  private const int MinDecode = 8, DMaxFastLen = 7, DBigLenCode = 0x3C, DBigLenBits = 6, DBigLenBase = 1;
  private static readonly byte[] LenVal = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 6, 255, 255];
  private static readonly byte[] LenBits = [0, 0, 0, 2, 3, 5, 5];
  private static readonly byte[] OffBits = [5, 5, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 12, 13];
  private static readonly ushort[] OffBase = [0, 0x20, 0x40, 0x60, 0x80, 0xC0, 0x100, 0x180, 0x200, 0x300, 0x400, 0x600, 0x800, 0xC00, 0x1000, 0x2000];

  /// <summary>Venti's "whack" LZ77 decoder (unwhack.c): returns the bytes written to <paramref name="dst" />.</summary>
  public static int Unwhack(Span<byte> dst, ReadOnlySpan<byte> src) {
    var d = 0;
    var s = 0;
    ulong bits = 0;
    int nbits = 0, overbits = 0;
    ulong litHist = ulong.MaxValue;
    while (s < src.Length || nbits - overbits >= MinDecode) {
      while (nbits <= 24) {
        bits <<= 8;
        if (s < src.Length) bits |= src[s++];
        else overbits += 8;
        nbits += 8;
      }
      int len = LenVal[(int)(bits >> (nbits - 5)) & 0x1F];
      if (len == 0) {
        byte lit;
        if ((litHist & 0xF) != 0) {
          nbits -= 9;
          lit = (byte)(bits >> nbits);
        } else {
          nbits -= 8;
          var l = (int)(bits >> nbits) & 0x7F;
          if (l < 32) {
            if (l < 24) {
              nbits -= 2;
              l = l << 2 | (int)(bits >> nbits) & 3;
            } else {
              nbits -= 3;
              l = l << 3 | (int)(bits >> nbits) & 7;
            }
            l -= 64;
          }
          lit = (byte)l;
        }
        if (d >= dst.Length) throw new InvalidDataException("Venti whack data decodes to more than the block.");
        dst[d++] = lit;
        litHist = litHist << 1 | (lit < 32 || lit > 127 ? 1UL : 0UL);
        continue;
      }

      if (len < 255)
        nbits -= LenBits[len];
      else {
        nbits -= DBigLenBits;
        var code = ((int)(bits >> nbits) & ((1 << DBigLenBits) - 1)) - DBigLenCode;
        len = DMaxFastLen;
        var use = DBigLenBase;
        var odd = (DBigLenBits & 1) ^ 1;
        while (code >= use) {
          len += use;
          code -= use;
          code <<= 1;
          if (--nbits < 0) throw new InvalidDataException("Venti whack length is out of range.");
          code |= (int)(bits >> nbits) & 1;
          use <<= odd;
          odd ^= 1;
        }
        len += code;
        while (nbits <= 24) {
          bits <<= 8;
          if (s < src.Length) bits |= src[s++];
          else overbits += 8;
          nbits += 8;
        }
      }

      nbits -= 4;
      var sel = (int)(bits >> nbits) & 0xF;
      var off = (int)OffBase[sel];
      var ob = OffBits[sel];
      nbits -= ob;
      off |= (int)(bits >> nbits) & ((1 << ob) - 1);
      ++off;
      if (off > d) throw new InvalidDataException("Venti whack offset reaches before the block.");
      if (d + len > dst.Length) throw new InvalidDataException("Venti whack length runs past the block.");
      for (var i = 0; i < len; ++i) dst[d + i] = dst[d - off + i];
      d += len;
    }
    if (nbits < overbits) throw new InvalidDataException("Venti whack data is truncated.");
    return d;
  }

  private byte[] Read(long offset, int count) {
    if (offset < 0 || offset + count > this._stream.Length) throw new InvalidDataException($"Venti read at {offset} lies past the end of the image.");
    var buf = new byte[count];
    this._stream.Position = offset;
    this._stream.ReadExactly(buf);
    return buf;
  }

  private static string CString(ReadOnlySpan<byte> b) {
    var end = b.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? b : b[..end]);
  }

  private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
  private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64BigEndian(b[at..]);
  private static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b[at..]);
}
