using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fossil;

/// <summary>A file or directory to write into a Fossil volume's active tree.</summary>
/// <param name="Name">The path element (UTF-8, 1 to 255 bytes, no '/').</param>
/// <param name="IsDirectory">Whether this is a directory.</param>
public sealed record FossilWriteNode(string Name, bool IsDirectory) {
  /// <summary>Gets the file contents (ignored for a directory).</summary>
  public byte[] Data { get; init; } = [];
  /// <summary>Gets the permission bits (Plan 9 rwx for owner, group and other).</summary>
  public uint Permissions { get; init; } = 0x1B4; // 0664
  /// <summary>Gets the owner (and group and last modifier).</summary>
  public string Owner { get; init; } = "adm";
  /// <summary>Gets the modification time, also used as the change and access time (UNIX seconds).</summary>
  public uint MTime { get; init; }
  /// <summary>Gets a directory's entries.</summary>
  public IReadOnlyList<FossilWriteNode> Children { get; init; } = [];
}

/// <summary>
/// Writes a Fossil volume as plan9port's <c>flfmt</c> formats it and its <c>fossil</c> server lays
/// out files: the 128 KiB gap, the header, the superblock, the label partition and the data
/// partition. Every block lives locally (no Venti) in epoch 1. The Fossil root holds
/// <c>active</c>, <c>archive</c> and <c>snapshot</c>; the given tree is <c>active</c>.
/// </summary>
/// <remarks>
/// The packing follows plan9port's MIT-licensed fossil sources (<c>pack.c</c>, <c>vac.c</c>,
/// <c>flfmt.c</c>, <c>source.c</c>, <c>file.c</c>), converted; see THIRD-PARTY-NOTICE.Fossil.txt
/// and <c>docs/FOSSIL-ON-DISK.md</c>.
/// </remarks>
public static class FossilWriter {
  private const int HeaderOffset = FossilReader.HeaderOffset;
  private const uint HeaderMagic = FossilReader.HeaderMagic, SuperMagic = FossilReader.SuperMagic;
  private const uint MetaMagic = FossilReader.MetaMagic, DirMagic = FossilReader.DirMagic;
  private const int ScoreSize = 20, EntrySize = 40, LabelSize = 14, MetaHeaderSize = 12, MetaIndexSize = 4;
  private const int BytesPerEntry = 100;
  private const byte BtData = 0, BtDir = 1 << 3;
  private const byte BsAlloc = 1;
  private const byte EntryActive = 0x01, EntryDir = 0x02, EntryLocal = 0x20;
  private const int DepthShift = 2;
  private const uint RootTag = 1, FirstTag = 32;
  private const uint ModeDir = FossilReader.ModeDir;
  private const uint NilBlock = 0xFFFFFFFF;

  /// <summary>The block size <c>flfmt</c> uses by default.</summary>
  public const int DefaultBlockSize = 8192;

  private static readonly byte[] ZeroScore = Convert.FromHexString("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709");

  /// <summary>Writes a volume whose active tree holds <paramref name="active"/>.</summary>
  /// <param name="output">The stream to write the volume to.</param>
  /// <param name="active">The entries of <c>/active</c>.</param>
  /// <param name="blockSize">The block size, a multiple of 512 from 512 to 56 KiB.</param>
  /// <param name="time">The time stamped on the three top-level directories and the root.</param>
  /// <param name="label">The volume's name in the superblock.</param>
  /// <exception cref="ArgumentException">A name cannot be stored or is used twice in one directory.</exception>
  public static void Write(Stream output, IReadOnlyList<FossilWriteNode> active, int blockSize = DefaultBlockSize, uint time = 0, string label = "main") {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(active);
    if (blockSize < 512 || blockSize % 512 != 0 || blockSize > 56 * 1024)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "Fossil blocks are a multiple of 512 bytes, at most 56 KiB.");

    var volume = new Volume(blockSize);
    volume.Build(active, time);
    volume.WriteTo(output, label);
  }

  /// <summary>A source as its parent stores it: its entry fields.</summary>
  private readonly record struct Source(byte Flags, byte Depth, long Size, uint Address, uint Tag, ushort PSize, ushort DSize);

  private sealed class Volume(int blockSize) {
    private readonly List<(byte[] Data, byte Type, uint Tag)> _blocks = [];
    private uint _nextTag = FirstTag;
    private ulong _nextQid = 1;
    private uint _active;

    private int EntriesPerBlock => blockSize / EntrySize;
    private ushort PSize => (ushort)(blockSize / ScoreSize * ScoreSize);

    public void Build(IReadOnlyList<FossilWriteNode> active, uint time) {
      // the root directory: active, archive and snapshot, made as flfmt's topLevel makes them
      var rootQid = this._nextQid++;
      var top = new List<FossilWriteNode> {
        new("active", true) { Permissions = 0x16D, MTime = time, Children = active },
        new("archive", true) { Permissions = 0x16D, MTime = time },
        new("snapshot", true) { Permissions = 0x16D, MTime = time },
      };
      var (rootEntries, rootMeta) = this.Directory(top);

      // the root of the root: one meta block naming the root directory itself
      var rootSelf = MetaBlock([new DirEntry("root", 0, 1, rootQid, "adm", ModeDir | 0x16D, time)], blockSize);
      var selfSource = this.WriteSource(rootSelf, BtData, this.Tag());

      var block = new byte[blockSize];
      PackEntry(block, 0, rootEntries);
      PackEntry(block, 1, rootMeta);
      PackEntry(block, 2, selfSource);
      var rootDirTag = this.Tag();
      var rootDirAddress = this.Alloc(block, BtDir, rootDirTag);

      var top0 = new byte[blockSize];
      PackEntry(top0, 0, new Source((byte)(EntryActive | EntryDir | EntryLocal), 0, 3 * EntrySize, rootDirAddress, rootDirTag, this.PSize, (ushort)blockSize));
      this._active = this.Alloc(top0, BtDir, RootTag);
    }

    private uint Tag() => this._nextTag++;

    private uint Alloc(byte[] data, byte type, uint tag) {
      this._blocks.Add((data, type, tag));
      return (uint)(this._blocks.Count - 1);
    }

    /// <summary>A directory's entry source and meta source, its children written first.</summary>
    private (Source Entries, Source Meta) Directory(IReadOnlyList<FossilWriteNode> children) {
      var seen = new HashSet<string>(StringComparer.Ordinal);
      var entries = new List<Source>();
      var dirEntries = new List<DirEntry>();
      foreach (var child in children) {
        var name = Encoding.UTF8.GetByteCount(child.Name);
        if (name is 0 or > 255 || child.Name.Contains('/') || child.Name is "." or ".." || child.Name.Contains('\0'))
          throw new ArgumentException($"'{child.Name}' cannot be a Fossil name: it needs 1 to 255 UTF-8 bytes and no '/'.");
        if (!seen.Add(child.Name))
          throw new ArgumentException($"'{child.Name}' is used twice in one directory.");
        var qid = this._nextQid++;
        var index = (uint)entries.Count;
        var mode = (child.Permissions & 0x1FF) | (child.IsDirectory ? ModeDir : 0);
        if (child.IsDirectory) {
          var (e, m) = this.Directory(child.Children);
          entries.Add(e);
          entries.Add(m);
          dirEntries.Add(new DirEntry(child.Name, index, index + 1, qid, child.Owner, mode, child.MTime));
        } else {
          entries.Add(this.WriteSource(child.Data, BtData, this.Tag()));
          dirEntries.Add(new DirEntry(child.Name, index, 0, qid, child.Owner, mode, child.MTime));
        }
      }

      // the entry source: EntriesPerBlock entries per block, the tail of each block unused
      var perBlock = this.EntriesPerBlock;
      var entryBytes = new byte[(entries.Count + perBlock - 1) / perBlock * blockSize];
      for (var i = 0; i < entries.Count; ++i)
        PackEntry(entryBytes.AsSpan(i / perBlock * blockSize), i % perBlock, entries[i]);
      var entrySize = (long)blockSize * (entries.Count / perBlock) + EntrySize * (entries.Count % perBlock);
      var entrySource = this.WriteSource(entryBytes, BtDir, this.Tag(), entrySize);
      entrySource = entrySource with { Flags = (byte)(entrySource.Flags | EntryDir) };

      // the meta source: meta blocks of DirEntries, each block's index sorted by name
      var meta = new MemoryStream();
      var pending = new List<DirEntry>();
      var used = MetaHeaderSize + blockSize / BytesPerEntry * MetaIndexSize;
      foreach (var de in dirEntries) {
        var size = de.Size;
        if (pending.Count > 0 && (pending.Count == blockSize / BytesPerEntry || used + size > blockSize)) {
          meta.Write(MetaBlock(pending, blockSize));
          pending.Clear();
          used = MetaHeaderSize + blockSize / BytesPerEntry * MetaIndexSize;
        }
        pending.Add(de);
        used += size;
      }
      if (pending.Count > 0)
        meta.Write(MetaBlock(pending, blockSize));
      var metaSource = this.WriteSource(meta.ToArray(), BtData, this.Tag());
      return (entrySource, metaSource);
    }

    /// <summary>
    /// Stores <paramref name="content"/> as a source: data blocks of the block size (the last
    /// zero-filled), under as many levels of pointer blocks as needed.
    /// </summary>
    private Source WriteSource(byte[] content, byte type, uint tag, long? size = null) {
      var length = size ?? content.LongLength;
      var dsize = (ushort)blockSize;
      if (content.Length == 0)
        return new Source(EntryActive, 0, 0, NilBlock, 0, this.PSize, dsize);

      var level = new List<uint>();
      for (var at = 0; at < content.Length; at += blockSize) {
        var block = new byte[blockSize];
        content.AsSpan(at, Math.Min(blockSize, content.Length - at)).CopyTo(block);
        level.Add(this.Alloc(block, type, tag));
      }
      var perPointer = this.PSize / ScoreSize;
      byte depth = 0;
      while (level.Count > 1) {
        ++depth;
        var parents = new List<uint>();
        for (var at = 0; at < level.Count; at += perPointer) {
          var block = new byte[blockSize];
          var slot = 0;
          for (; slot < perPointer && at + slot < level.Count; ++slot)
            LocalScore(block.AsSpan(slot * ScoreSize), level[at + slot]);
          for (; slot < blockSize / ScoreSize; ++slot)
            ZeroScore.CopyTo(block, slot * ScoreSize);
          parents.Add(this.Alloc(block, (byte)(type | depth), tag));
        }
        level = parents;
      }
      return new Source((byte)(EntryActive | EntryLocal), depth, length, level[0], tag, this.PSize, dsize);
    }

    public void WriteTo(Stream output, string label) {
      // the partitions as flfmt's partition() cuts a file of nblock blocks
      var perLabelBlock = blockSize / LabelSize;
      var super = (uint)((HeaderOffset + 2 * blockSize) / blockSize);
      var labelStart = super + 1;
      var needed = (long)this._blocks.Count + 64; // room for the server's own first writes
      var nblock = Math.Max(labelStart + needed + (needed + perLabelBlock - 1) / perLabelBlock, HeaderOffset * 10L / blockSize);
      long data, ndata;
      for (;; ++nblock) {
        ndata = (long)perLabelBlock * (nblock - labelStart) / (perLabelBlock + 1);
        if (ndata >= needed) break;
      }
      var nlabel = (ndata + perLabelBlock - 1) / perLabelBlock;
      data = labelStart + nlabel;
      var end = data + ndata;

      var image = new byte[nblock * blockSize];
      var h = image.AsSpan(HeaderOffset);
      BinaryPrimitives.WriteUInt32BigEndian(h, HeaderMagic);
      BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1);
      BinaryPrimitives.WriteUInt16BigEndian(h[6..], (ushort)blockSize);
      BinaryPrimitives.WriteUInt32BigEndian(h[8..], super);
      BinaryPrimitives.WriteUInt32BigEndian(h[12..], labelStart);
      BinaryPrimitives.WriteUInt32BigEndian(h[16..], (uint)data);
      BinaryPrimitives.WriteUInt32BigEndian(h[20..], (uint)end);

      var s = image.AsSpan((int)(super * blockSize));
      BinaryPrimitives.WriteUInt32BigEndian(s, SuperMagic);
      BinaryPrimitives.WriteUInt16BigEndian(s[4..], 1);
      BinaryPrimitives.WriteUInt32BigEndian(s[6..], 1); // epoch low
      BinaryPrimitives.WriteUInt32BigEndian(s[10..], 1); // epoch high
      BinaryPrimitives.WriteUInt64BigEndian(s[14..], this._nextQid);
      BinaryPrimitives.WriteUInt32BigEndian(s[22..], this._active);
      BinaryPrimitives.WriteUInt32BigEndian(s[26..], NilBlock); // next
      BinaryPrimitives.WriteUInt32BigEndian(s[30..], NilBlock); // current
      ZeroScore.CopyTo(s[34..]); // the last archived score: none
      var name = Encoding.UTF8.GetBytes(label);
      name.AsSpan(0, Math.Min(name.Length, 127)).CopyTo(s[54..]);

      for (var a = 0; a < this._blocks.Count; ++a) {
        var (blockData, type, tag) = this._blocks[a];
        blockData.CopyTo(image, (data + a) * blockSize);
        var l = image.AsSpan((int)((labelStart + a / perLabelBlock) * blockSize + a % perLabelBlock * LabelSize), LabelSize);
        l[0] = BsAlloc;
        l[1] = type;
        BinaryPrimitives.WriteUInt32BigEndian(l[2..], 1); // epoch
        BinaryPrimitives.WriteUInt32BigEndian(l[6..], 0xFFFFFFFF); // not closed
        BinaryPrimitives.WriteUInt32BigEndian(l[10..], tag);
      }
      output.Write(image);
    }
  }

  private static void PackEntry(Span<byte> block, int index, Source e) {
    var p = block.Slice(index * EntrySize, EntrySize);
    p.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(p, 0); // gen
    BinaryPrimitives.WriteUInt16BigEndian(p[4..], e.PSize);
    BinaryPrimitives.WriteUInt16BigEndian(p[6..], e.DSize);
    p[8] = (byte)(e.Flags | (e.Depth << DepthShift));
    BinaryPrimitives.WriteUInt16BigEndian(p[14..], (ushort)(e.Size >> 32));
    BinaryPrimitives.WriteUInt32BigEndian(p[16..], (uint)e.Size);
    if ((e.Flags & EntryLocal) != 0) {
      // archive 0, snapshot epoch 0, tag, the block address
      BinaryPrimitives.WriteUInt32BigEndian(p[32..], e.Tag);
      BinaryPrimitives.WriteUInt32BigEndian(p[36..], e.Address);
    } else
      ZeroScore.CopyTo(p[20..]);
  }

  private static void LocalScore(Span<byte> score, uint address) {
    score[..16].Clear();
    BinaryPrimitives.WriteUInt32BigEndian(score[16..], address);
  }

  /// <summary>A directory entry in a meta block (version 9, no optional sections).</summary>
  private sealed record DirEntry(string Elem, uint Entry, uint MEntry, ulong Qid, string Owner, uint Mode, uint Time) {
    public int Size => 4 + 2 + 4 + 4 + 4 + 4 + 8 + 4 + 4 + 4 + 4 + 4
      + 2 + Encoding.UTF8.GetByteCount(this.Elem) + 3 * (2 + Encoding.UTF8.GetByteCount(this.Owner));

    public void Pack(Span<byte> p) {
      BinaryPrimitives.WriteUInt32BigEndian(p, DirMagic);
      BinaryPrimitives.WriteUInt16BigEndian(p[4..], 9);
      var at = 6;
      at += PackString(p[at..], this.Elem);
      BinaryPrimitives.WriteUInt32BigEndian(p[at..], this.Entry);
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 4)..], 0); // gen
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 8)..], this.MEntry);
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 12)..], 0); // mgen
      BinaryPrimitives.WriteUInt64BigEndian(p[(at + 16)..], this.Qid);
      at += 24;
      for (var i = 0; i < 3; ++i) // uid, gid, mid
        at += PackString(p[at..], this.Owner);
      BinaryPrimitives.WriteUInt32BigEndian(p[at..], this.Time); // mtime
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 4)..], 0); // mcount
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 8)..], this.Time); // ctime
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 12)..], this.Time); // atime
      BinaryPrimitives.WriteUInt32BigEndian(p[(at + 16)..], this.Mode);
    }

    private static int PackString(Span<byte> p, string s) {
      var n = Encoding.UTF8.GetBytes(s, p[2..]);
      BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)n);
      return 2 + n;
    }
  }

  /// <summary>One meta block: header, index (sorted by name) of up to blockSize/100 entries, then the entries.</summary>
  private static byte[] MetaBlock(List<DirEntry> entries, int blockSize) {
    var block = new byte[blockSize];
    var maxIndex = blockSize / BytesPerEntry;
    var at = MetaHeaderSize + maxIndex * MetaIndexSize;
    var placed = new List<(byte[] Name, int Offset, int Size)>();
    foreach (var e in entries) {
      e.Pack(block.AsSpan(at));
      placed.Add((Encoding.UTF8.GetBytes(e.Elem), at, e.Size));
      at += e.Size;
    }
    placed.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
    for (var i = 0; i < placed.Count; ++i) {
      BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(MetaHeaderSize + i * MetaIndexSize), (ushort)placed[i].Offset);
      BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(MetaHeaderSize + i * MetaIndexSize + 2), (ushort)placed[i].Size);
    }
    BinaryPrimitives.WriteUInt32BigEndian(block, MetaMagic);
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(4), (ushort)at); // size
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(6), 0); // free
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(8), (ushort)maxIndex);
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(10), (ushort)placed.Count);
    return block;
  }
}
