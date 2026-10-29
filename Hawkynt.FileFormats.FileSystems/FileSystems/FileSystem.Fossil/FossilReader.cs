using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fossil;

/// <summary>One file or directory of a Fossil volume.</summary>
/// <param name="Path">Slash-separated path from the Fossil root (<c>active/…</c>, <c>snapshot/…</c>).</param>
/// <param name="IsDirectory">True for a directory.</param>
/// <param name="Size">File size from the data source's entry.</param>
/// <param name="Mode">Plan 9 mode word: permission bits plus <c>ModeDir</c> (1&lt;&lt;15) and friends.</param>
/// <param name="Uid">Owner.</param>
/// <param name="Gid">Group.</param>
/// <param name="MTime">Modification time (UNIX seconds).</param>
/// <param name="Source">The entry of the file's data source.</param>
public sealed record FossilEntry(string Path, bool IsDirectory, long Size, uint Mode, string Uid, string Gid, uint MTime, FossilSourceEntry Source);

/// <summary>A source ("stream") entry as stored in a directory block (40 bytes).</summary>
public readonly record struct FossilSourceEntry(uint Gen, ushort PSize, ushort DSize, byte Flags, byte Depth, long Size, uint Address, bool IsLocal, bool IsZero, uint Tag, uint Snap) {
  /// <summary>Entry in use.</summary>
  public bool IsActive => (this.Flags & 0x01) != 0;
  /// <summary>Source holds entries (a directory stream).</summary>
  public bool IsDir => (this.Flags & 0x02) != 0;
}

/// <summary>
/// Reads a Fossil volume (Plan 9's file server, as <c>flfmt</c> lays it out and the
/// <c>fossil</c> server writes it): header at 128 KiB, superblock, label and data
/// partitions; files are trees of local blocks addressed by their block number. Blocks
/// already archived to Venti are refused — this reader has no Venti. See
/// <c>docs/FOSSIL-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// Converted from plan9port's MIT-licensed fossil sources (<c>pack.c</c>, <c>vac.c</c>,
/// <c>source.c</c>, <c>file.c</c>); see <c>THIRD-PARTY-NOTICE.Fossil.txt</c>.
/// </remarks>
public sealed class FossilReader {
  public const uint HeaderMagic = 0x3776AE89, SuperMagic = 0x2340A3B1, MetaMagic = 0x5656FC7A, DirMagic = 0x1C4D9072;
  public const int HeaderOffset = 128 * 1024, ScoreSize = 20, EntrySize = 40, MaxDepth = 7;
  public const uint ModeDir = 1u << 15, ModeLink = 1u << 14, ModeSnapshot = 1u << 20;

  /// <summary>SHA-1 of the empty string: Venti's score of a zero-length block.</summary>
  private static readonly byte[] ZeroScore = Convert.FromHexString("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709");

  private readonly Stream _stream;

  /// <summary>Bytes per block.</summary>
  public int BlockSize { get; }

  /// <summary>First block of the data partition.</summary>
  public uint DataStart { get; }

  /// <summary>End of the data partition.</summary>
  public uint DataEnd { get; }

  /// <summary>Superblock epochs.</summary>
  public (uint Low, uint High) Epochs { get; }

  /// <summary>Block address of the active root.</summary>
  public uint Active { get; }

  /// <summary>Superblock name (the volume label).</summary>
  public string Name { get; }

  /// <summary>Every file and directory, parents first.</summary>
  public IReadOnlyList<FossilEntry> Entries { get; }

  /// <summary>Opens a Fossil volume; throws <see cref="InvalidDataException"/> for anything it cannot read faithfully.</summary>
  public FossilReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    if (stream.Length < HeaderOffset + 512) throw new InvalidDataException("Image too short for a Fossil header at 128 KiB.");
    var h = this.Read(HeaderOffset, 512);
    if (U32(h, 0) != HeaderMagic) throw new InvalidDataException("Not a Fossil volume: header magic 0x3776ae89 missing at 128 KiB.");
    if (U16(h, 4) != 1) throw new InvalidDataException($"Fossil header version {U16(h, 4)} is not 1.");
    this.BlockSize = U16(h, 6);
    var super = U32(h, 8);
    this.DataStart = U32(h, 16);
    this.DataEnd = U32(h, 20);
    if (this.BlockSize < 512 || this.BlockSize % 512 != 0 || this.DataEnd <= this.DataStart)
      throw new InvalidDataException("Fossil header geometry is invalid.");

    var s = this.Read((long)super * this.BlockSize, 512);
    if (U32(s, 0) != SuperMagic || U16(s, 4) != 1) throw new InvalidDataException("Fossil superblock magic or version is wrong.");
    this.Epochs = (U32(s, 6), U32(s, 10));
    this.Active = U32(s, 22);
    this.Name = CString(s.AsSpan(54, 128));

    // The active root block holds one entry: the root source. Its first three entries are
    // the root directory's entry stream, its meta stream, and the meta block naming the root.
    var rootBlock = this.ReadBlock(this.Active);
    var root = ParseEntry(rootBlock, 0);
    if (!root.IsActive || !root.IsDir) throw new InvalidDataException("Fossil active root source is not an active directory.");
    var dirSource = this.SourceEntry(root, 0);
    var metaSource = this.SourceEntry(root, 1);

    var entries = new List<FossilEntry>();
    this.Walk(dirSource, metaSource, "", entries, 0);
    this.Entries = entries;
  }

  /// <summary>Reads a file's bytes; refuses blocks that are not local or lie outside the data partition.</summary>
  public byte[] Extract(FossilEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    return this.ReadSource(entry.Source);
  }

  private void Walk(FossilSourceEntry dir, FossilSourceEntry meta, string prefix, List<FossilEntry> entries, int depth) {
    // A directory loop shows up as unbounded nesting.
    if (depth > 64) throw new InvalidDataException("Fossil directory nesting beyond 64 levels (a loop?).");
    var metaData = this.ReadSource(meta);
    for (var off = 0; off < metaData.Length; off += meta.DSize) {
      var mb = metaData.AsSpan(off, Math.Min(meta.DSize, metaData.Length - off));
      foreach (var de in ParseMetaBlock(mb)) {
        var path = prefix.Length == 0 ? de.Elem : prefix + "/" + de.Elem;
        var isDir = (de.Mode & ModeDir) != 0;
        var src = this.SourceEntry(dir, de.Entry);
        if (!src.IsActive || src.Gen != de.Gen)
          throw new InvalidDataException($"Fossil '{path}': its source entry {de.Entry} is inactive or of another generation.");
        entries.Add(new FossilEntry(path, isDir, isDir ? 0 : src.Size, de.Mode, de.Uid, de.Gid, de.MTime, src));
        if (!isDir) continue;
        var msrc = this.SourceEntry(dir, de.MEntry);
        if (!msrc.IsActive || msrc.Gen != de.MGen)
          throw new InvalidDataException($"Fossil '{path}': its meta source entry {de.MEntry} is inactive or of another generation.");
        this.Walk(src, msrc, path, entries, depth + 1);
      }
    }
  }

  private sealed record DirEntry(string Elem, uint Entry, uint Gen, uint MEntry, uint MGen, string Uid, string Gid, uint MTime, uint Mode);

  /// <summary>DirEntries of one MetaBlock, in index order (vac.c mbUnpack/meUnpack/deUnpack).</summary>
  private static List<DirEntry> ParseMetaBlock(ReadOnlySpan<byte> p) {
    var list = new List<DirEntry>();
    if (p.Length < 12 || p.IndexOfAnyExcept((byte)0) < 0) return list;   // an all-zero block holds no entries
    var magic = U32(p, 0);
    if (magic != MetaMagic && magic != MetaMagic - 1) throw new InvalidDataException("Fossil meta block magic is wrong.");
    int size = U16(p, 4), maxIndex = U16(p, 8), nIndex = U16(p, 10);
    var omin = 12 + maxIndex * 4;
    if (size > p.Length || p.Length < omin || nIndex > maxIndex) throw new InvalidDataException("Fossil meta block header is inconsistent.");
    for (var i = 0; i < nIndex; ++i) {
      int eo = U16(p, 12 + i * 4), en = U16(p, 14 + i * 4);
      if (eo < omin || eo + en > size || en < 8) throw new InvalidDataException("Fossil meta block index points outside the block.");
      list.Add(ParseDirEntry(p.Slice(eo, en)));
    }
    return list;
  }

  private static DirEntry ParseDirEntry(ReadOnlySpan<byte> p) {
    if (U32(p, 0) != DirMagic) throw new InvalidDataException("Fossil directory entry magic is wrong.");
    var version = U16(p, 4);
    if (version is < 7 or > 9) throw new InvalidDataException($"Fossil directory entry version {version} is unknown.");
    var o = 6;
    var elem = Str(p, ref o);
    var entry = U32(p, o); o += 4;
    uint gen = 0, mentry = entry + 1, mgen = 0;
    if (version >= 9) { gen = U32(p, o); mentry = U32(p, o + 4); mgen = U32(p, o + 8); o += 12; }
    o += 8;                                   // qid
    if (version == 7) o += ScoreSize;         // replacement score
    var uid = Str(p, ref o);
    var gid = Str(p, ref o);
    _ = Str(p, ref o);                        // mid
    if (o + 20 > p.Length) throw new InvalidDataException("Fossil directory entry is truncated.");
    var mtime = U32(p, o);
    var mode = U32(p, o + 16);
    if (elem.Length == 0 || elem is "." or ".." || elem.Contains('/')) throw new InvalidDataException($"Fossil directory entry has an unusable name '{elem}'.");
    return new(elem, entry, gen, mentry, mgen, uid, gid, mtime, mode);
  }

  private static string Str(ReadOnlySpan<byte> p, ref int o) {
    if (o + 2 > p.Length) throw new InvalidDataException("Fossil directory entry string is truncated.");
    int n = U16(p, o);
    if (o + 2 + n > p.Length) throw new InvalidDataException("Fossil directory entry string is truncated.");
    var s = Encoding.UTF8.GetString(p.Slice(o + 2, n));
    o += 2 + n;
    return s;
  }

  /// <summary>Entry <paramref name="index"/> of a directory source.</summary>
  private FossilSourceEntry SourceEntry(FossilSourceEntry dir, uint index) {
    if (!dir.IsDir) throw new InvalidDataException("Fossil: entry lookup in a source that is not a directory.");
    var epb = (uint)(dir.DSize / EntrySize);
    if (epb == 0) throw new InvalidDataException("Fossil directory source has a data block smaller than one entry.");
    var block = this.SourceBlock(dir, index / epb);
    return ParseEntry(block, (int)(index % epb));
  }

  private static FossilSourceEntry ParseEntry(ReadOnlySpan<byte> b, int index) {
    var p = b.Slice(index * EntrySize, EntrySize);
    var flags = p[8];
    var depth = (byte)((flags >> 2) & 7);
    var size = (long)U16(p, 14) << 32 | U32(p, 16);
    var local = (flags & 0x20) != 0;
    if (local) return new(U32(p, 0), U16(p, 4), U16(p, 6), (byte)(flags & ~0x1C), depth, size, U32(p, 36), true, false, U32(p, 32), U32(p, 28));
    var score = p.Slice(20, ScoreSize);
    var zero = score.SequenceEqual(ZeroScore) || score.IndexOfAnyExcept((byte)0) < 0;
    // A non-local score with 16 leading zeros is still a local block address (globalToLocal).
    var addr = score[..16].IndexOfAnyExcept((byte)0) < 0 ? U32(score, 16) : uint.MaxValue;
    return new(U32(p, 0), U16(p, 4), U16(p, 6), (byte)(flags & ~0x1C), depth, size, addr, addr != uint.MaxValue && !zero, zero, 0, 0);
  }

  /// <summary>All bytes of a source (its entry's size).</summary>
  private byte[] ReadSource(FossilSourceEntry e) {
    if (e.Size < 0 || e.Size > int.MaxValue) throw new InvalidDataException("Fossil source is too large to buffer.");
    var data = new byte[e.Size];
    if (e.Size == 0) return data;
    if (e.DSize == 0) throw new InvalidDataException("Fossil source with data has a zero block size.");
    var blocks = (e.Size + e.DSize - 1) / e.DSize;
    for (long bn = 0; bn < blocks; ++bn) {
      var block = this.SourceBlock(e, (uint)bn);
      var at = bn * e.DSize;
      var n = (int)Math.Min(e.DSize, e.Size - at);
      block.AsSpan(0, Math.Min(n, block.Length)).CopyTo(data.AsSpan((int)at));
    }
    return data;
  }

  /// <summary>Block <paramref name="bn"/> of a source, walking its pointer tree (source.c _sourceBlock).</summary>
  private byte[] SourceBlock(FossilSourceEntry e, uint bn) {
    var np = e.PSize / ScoreSize;
    if (e.Depth > MaxDepth || (e.Depth > 0 && np == 0)) throw new InvalidDataException("Fossil source pointer tree is malformed.");
    var index = new int[MaxDepth + 1];
    var levels = 0;
    for (var b = bn; b > 0; b /= (uint)np) {
      if (levels >= e.Depth) throw new InvalidDataException($"Fossil block {bn} lies beyond the source's depth {e.Depth}.");
      index[levels++] = (int)(b % (uint)np);
    }
    if (e.IsZero) return new byte[e.DSize];
    if (!e.IsLocal) throw new InvalidDataException("Fossil source lives on Venti; this reader has no Venti connection.");
    var block = this.ReadBlock(e.Address);
    for (var level = e.Depth - 1; level >= 0; --level) {
      var score = block.AsSpan(index[level] * ScoreSize, ScoreSize);
      if (score.SequenceEqual(ZeroScore) || score.IndexOfAnyExcept((byte)0) < 0)
        return new byte[level == 0 ? e.DSize : e.PSize];
      if (score[..16].IndexOfAnyExcept((byte)0) >= 0)
        throw new InvalidDataException("Fossil pointer refers to a Venti block; this reader has no Venti connection.");
      block = this.ReadBlock(U32(score, 16));
    }
    return block;
  }

  private byte[] ReadBlock(uint addr) {
    if (addr >= this.DataEnd - this.DataStart) throw new InvalidDataException($"Fossil block {addr} lies outside the data partition.");
    return this.Read(((long)this.DataStart + addr) * this.BlockSize, this.BlockSize);
  }

  private byte[] Read(long offset, int count) {
    if (offset + count > this._stream.Length) throw new InvalidDataException($"Fossil read at {offset} lies past the end of the image.");
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
  private static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b[at..]);
}
