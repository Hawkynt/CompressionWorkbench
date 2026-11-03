using System.Buffers.Binary;
using System.Text;

namespace FileSystem.SquashFs;

/// <summary>
/// Reads SquashFS 1.x, 2.x and 3.x images, little- or big-endian, with zlib compression: the
/// layouts mksquashfs wrote before version 4 made the format little-endian only.
/// </summary>
/// <remarks>
/// <para>Converted from 7-Zip's <c>SquashfsHandler.cpp</c> (7-Zip 23.01, GNU LGPL 2.1 or later,
/// Copyright (C) Igor Pavlov); see THIRD-PARTY-NOTICE.7-Zip.txt beside this file. The superblock,
/// inode, directory and fragment layouts per major version and their big-endian bit-field order
/// are taken from there.</para>
/// <para>Layout: superblock, data blocks and fragments, then the inode table and the directory
/// table as runs of metadata blocks (a 16-bit length, bit 15 = stored, one more byte when the
/// CHECK flag is set; at most 8 KiB each), then the fragment index and the uid/gid arrays.
/// Inodes and directories are addressed as (metadata block offset within the table, offset in
/// the decoded block).</para>
/// <para>The LZMA builds of the 3.x era (magic "shsq"/"qshs", or zlib method with LZMA data) are
/// refused rather than guessed.</para>
/// </remarks>
internal sealed class SquashFsLegacyReader {
  private const uint MagicLe = 0x73717368;    // "hsqs"
  private const uint MagicBe = 0x68737173;    // "sqsh"
  private const uint MagicLzmaLe = 0x71736873; // "shsq"
  private const uint MagicLzmaBe = 0x73687371; // "qshs"
  private const int HeaderSize = 0x77;
  private const int MetadataBlockSize = 8192;
  private const uint NotCompressed16 = 1u << 15;
  private const uint NotCompressed32 = 1u << 24;
  private const uint NoFragment = 0xFFFFFFFF;
  private const int FlagCheck = 1 << 2;
  private const int MaxDirectoryDepth = 1024;

  // Inode types; the "long" variants are the short type + 7.
  private const int TypeIpc = 0, TypeDir = 1, TypeFile = 2, TypeLink = 3, TypeFifo = 6, TypeSock = 7;

  private readonly Stream _stream;
  private readonly bool _be;
  private readonly int _major;
  private readonly int _minor;
  private readonly int _flags;
  private readonly uint _blockSize;
  private readonly int _blockLog;
  private readonly long _inodeTable, _dirTable, _fragTable, _uidTable, _gidTable;
  private readonly int _numUids, _numGids;
  private readonly uint _numInodes, _numFrags;
  private readonly ulong _rootInode;

  private readonly (long Start, uint Size)[] _fragments = [];
  private readonly MetadataRun _inodes;
  private readonly MetadataRun _dirs;
  private readonly Dictionary<int, Node> _nodesByPos = [];
  private readonly uint[] _uids;
  private readonly uint[] _gids;
  private readonly Dictionary<SquashFsEntry, Node> _nodeOf = new(ReferenceEqualityComparer.Instance);

  public List<SquashFsEntry> Entries { get; } = [];

  /// <summary>Gets the version as "major.minor", for messages and metadata.</summary>
  public string Version => $"{this._major}.{this._minor}";

  /// <summary>Gets whether the image is big-endian.</summary>
  public bool BigEndian => this._be;

  /// <summary>
  /// Whether the 4-byte magic at the start of <paramref name="header"/> and its version say this
  /// is an image for this reader rather than for the version 4 one.
  /// </summary>
  public static bool Handles(ReadOnlySpan<byte> header) {
    if (header.Length < 0x20)
      return false;
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (magic is MagicLzmaLe or MagicLzmaBe)
      return true; // refused with a clear reason by the constructor
    if (magic == MagicBe)
      return true;
    return magic == MagicLe && BinaryPrimitives.ReadUInt16LittleEndian(header[0x1C..]) < 4;
  }

  public SquashFsLegacyReader(Stream stream) {
    this._stream = stream;
    stream.Position = 0;
    Span<byte> h = stackalloc byte[HeaderSize];
    if (stream.ReadAtLeast(h, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
      throw new InvalidDataException("Truncated SquashFS superblock.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(h);
    switch (magic) {
      case MagicLe: break;
      case MagicBe: this._be = true; break;
      case MagicLzmaLe or MagicLzmaBe:
        throw new NotSupportedException("SquashFS 3.x LZMA build (\"shsq\"/\"qshs\" magic) is not supported.");
      default:
        throw new InvalidDataException($"Invalid SquashFS magic: 0x{magic:X8}");
    }

    this._numInodes = this.U32(h[4..]);
    this._major = this.U16(h[0x1C..]);
    this._minor = this.U16(h[0x1E..]);
    if (this._major is < 1 or > 3)
      throw new NotSupportedException($"SquashFS {this._major}.{this._minor} is not a 1.x-3.x image.");

    long size = this.U32(h[0x08..]);
    this._uidTable = this.U32(h[0x0C..]);
    this._gidTable = this.U32(h[0x10..]);
    this._inodeTable = this.U32(h[0x14..]);
    this._dirTable = this.U32(h[0x18..]);
    this._blockSize = this.U16(h[0x20..]);
    this._blockLog = this.U16(h[0x22..]);
    this._flags = h[0x24];
    this._numUids = h[0x25];
    this._numGids = h[0x26];
    this._rootInode = this.U64(h[0x2B..]);
    this._fragTable = this._uidTable;
    if (this._major >= 2) {
      this._blockSize = this.U32(h[0x33..]);
      this._numFrags = this.U32(h[0x37..]);
      this._fragTable = this.U32(h[0x3B..]);
      if (this._major == 3) {
        size = (long)this.U64(h[0x3F..]);
        this._uidTable = (long)this.U64(h[0x47..]);
        this._gidTable = (long)this.U64(h[0x4F..]);
        this._inodeTable = (long)this.U64(h[0x57..]);
        this._dirTable = (long)this.U64(h[0x5F..]);
        this._fragTable = (long)this.U64(h[0x67..]);
      }
    }

    if (!(this._inodeTable < this._dirTable && this._dirTable <= this._fragTable && this._fragTable <= size
          && this._uidTable <= size && size <= stream.Length
          && this._blockLog is >= 12 and <= 23 && this._blockSize == 1u << this._blockLog))
      throw new InvalidDataException("SquashFS superblock is inconsistent.");

    if (this._numFrags != 0)
      this._fragments = this.ReadFragments();
    this._inodes = this.ReadMetadataRun(this._inodeTable, this._dirTable);
    this._dirs = this.ReadMetadataRun(this._dirTable, this._fragTable);
    this.ParseInodes();
    this._uids = this.ReadIds(this._uidTable, this._numUids);
    this._gids = this.ReadIds(this._gidTable, this._numGids);

    var rootBlock = this._rootInode >> 16;
    if (rootBlock >= 1UL << 32)
      throw new InvalidDataException("SquashFS root inode reference is out of range.");
    var root = this.NodeAt((uint)rootBlock, (uint)(this._rootInode & 0xFFFF))
               ?? throw new InvalidDataException("SquashFS root inode not found.");
    if (!root.IsDir)
      throw new InvalidDataException("SquashFS root inode is not a directory.");
    this.WalkDirectory(root, string.Empty, 0);
  }

  // ── endian helpers ───────────────────────────────────────────────────

  private ushort U16(ReadOnlySpan<byte> p) => this._be ? BinaryPrimitives.ReadUInt16BigEndian(p) : BinaryPrimitives.ReadUInt16LittleEndian(p);
  private uint U32(ReadOnlySpan<byte> p) => this._be ? BinaryPrimitives.ReadUInt32BigEndian(p) : BinaryPrimitives.ReadUInt32LittleEndian(p);
  private ulong U64(ReadOnlySpan<byte> p) => this._be ? BinaryPrimitives.ReadUInt64BigEndian(p) : BinaryPrimitives.ReadUInt64LittleEndian(p);

  // ── metadata ─────────────────────────────────────────────────────────

  /// <summary>A table decoded block by block: its bytes, and where each block starts on both sides.</summary>
  private sealed class MetadataRun {
    public byte[] Data = [];
    public readonly List<uint> PackPos = [];
    public readonly List<int> UnpackPos = [];

    /// <summary>The decoded position of (block offset within the table, offset in the block).</summary>
    public int? Resolve(uint block, uint offset) {
      var index = this.PackPos.BinarySearch(block);
      if (index < 0)
        return null;
      var pos = (long)this.UnpackPos[index] + offset;
      return pos <= this.Data.Length ? (int)pos : null;
    }
  }

  /// <summary>Reads one metadata block at the stream position; returns its decoded bytes and its stored length.</summary>
  private (byte[] Data, int Stored) ReadMetadataBlock(long limit) {
    var headerLength = (this._flags & FlagCheck) != 0 ? 3 : 2;
    if (headerLength > limit)
      throw new InvalidDataException("SquashFS metadata block runs past its table.");
    Span<byte> head = stackalloc byte[3];
    this._stream.ReadExactly(head[..headerLength]);
    uint length = this.U16(head);
    var compressed = (length & NotCompressed16) == 0;
    if (length != NotCompressed16)
      length &= ~NotCompressed16;
    if (length > MetadataBlockSize || headerLength + length > limit)
      throw new InvalidDataException("SquashFS metadata block is larger than its table allows.");
    var raw = new byte[length];
    this._stream.ReadExactly(raw);
    var data = compressed ? this.Inflate(raw, MetadataBlockSize) : raw;
    return (data, headerLength + (int)length);
  }

  private MetadataRun ReadMetadataRun(long start, long end) {
    if (end < start || end - start >= 1L << 32)
      throw new InvalidDataException("SquashFS table bounds are inconsistent.");
    var run = new MetadataRun();
    var total = end - start;
    using var data = new MemoryStream();
    this._stream.Position = start;
    var pos = 0L;
    while (pos != total) {
      run.PackPos.Add((uint)pos);
      run.UnpackPos.Add((int)data.Length);
      var (block, stored) = this.ReadMetadataBlock(total - pos);
      data.Write(block);
      pos += stored;
    }
    run.UnpackPos.Add((int)data.Length);
    run.Data = data.ToArray();
    return run;
  }

  /// <summary>
  /// Decodes one zlib block (RFC 1950, Adler-32 checked). A block that is not zlib is the LZMA
  /// build of the format and is refused.
  /// </summary>
  private byte[] Inflate(byte[] raw, long maxOutput) {
    if (raw.Length >= 2 && ((raw[0] & 0x0F) != 8 || ((raw[0] << 8) | raw[1]) % 31 != 0))
      throw new NotSupportedException("SquashFS block is not zlib: the LZMA build of the 3.x format is not supported.");
    var data = FileFormat.Zlib.ZlibStream.Decompress(raw);
    if (data.Length > maxOutput)
      throw new InvalidDataException("SquashFS block decodes to more than its maximum size.");
    return data;
  }

  private (long, uint)[] ReadFragments() {
    if (this._numFrags > 1u << 28)
      throw new InvalidDataException("SquashFS fragment count is implausible.");
    var big = this._major > 2 ? 1 : 0;
    var perBlockLog = 13 - (3 + big);
    var numBlocks = (int)((this._numFrags + (1u << perBlockLog) - 1) >> perBlockLog);
    var index = new byte[numBlocks << (2 + big)];
    this._stream.Position = this._fragTable;
    this._stream.ReadExactly(index);

    var result = new List<(long, uint)>((int)this._numFrags);
    for (var i = 0; i < numBlocks; ++i) {
      this._stream.Position = big != 0 ? (long)this.U64(index.AsSpan(i * 8)) : this.U32(index.AsSpan(i * 4));
      var (block, _) = this.ReadMetadataBlock(MetadataBlockSize + 3);
      var expected = i == numBlocks - 1 ? (int)((this._numFrags << (3 + big)) & (MetadataBlockSize - 1)) : MetadataBlockSize;
      if (block.Length != (expected == 0 ? MetadataBlockSize : expected))
        throw new InvalidDataException("SquashFS fragment table block has the wrong size.");
      for (var j = 0; j < block.Length; j += big != 0 ? 16 : 8)
        result.Add(big != 0
          ? ((long)this.U64(block.AsSpan(j)), this.U32(block.AsSpan(j + 8)))
          : (this.U32(block.AsSpan(j)), this.U32(block.AsSpan(j + 4))));
    }
    if (result.Count != this._numFrags)
      throw new InvalidDataException("SquashFS fragment table holds the wrong number of entries.");
    return [.. result];
  }

  private uint[] ReadIds(long start, int count) {
    var ids = new uint[count];
    if (count == 0)
      return ids;
    var raw = new byte[count * 4];
    this._stream.Position = start;
    this._stream.ReadExactly(raw);
    for (var i = 0; i < count; ++i)
      ids[i] = this.U32(raw.AsSpan(i * 4));
    return ids;
  }

  // ── inodes ───────────────────────────────────────────────────────────

  private sealed class Node {
    public int Type;
    public int Mode;
    public int Uid;
    public int Gid;
    public uint Frag = NoFragment;
    public uint Offset;
    public ulong FileSize;
    public ulong StartBlock;
    public int Pos;     // decoded position in the inode table
    public int MTimeAt; // offset of the mtime field within the inode, or 0 when it has none

    public bool IsDir => this.Type is TypeDir or TypeDir + 7;
    public bool IsLink => this.Type is TypeLink or TypeLink + 7;
    public bool IsFile => this.Type is TypeFile or TypeFile + 7;
    public bool HasFragment => this.Frag != NoFragment;
  }

  private void ParseInodes() {
    var data = this._inodes.Data;
    if (this._numInodes > data.Length / 4)
      throw new InvalidDataException("SquashFS inode count does not fit its table.");
    var pos = 0;
    for (var i = 0u; i < this._numInodes; ++i) {
      var node = new Node { Pos = pos };
      var span = data.AsSpan(pos);
      var size = this._major switch {
        1 => this.Parse1(node, span),
        2 => this.Parse2(node, span),
        _ => this.Parse3(node, span),
      };
      if (size == 0)
        throw new InvalidDataException($"SquashFS inode {i} does not parse.");
      this._nodesByPos[pos] = node;
      pos += size;
    }
    if (pos != data.Length)
      throw new InvalidDataException("SquashFS inode table holds more than its inode count.");
  }

  private ulong NumBlocks(Node n)
    => (n.FileSize >> this._blockLog) + (!n.HasFragment && (n.FileSize & (this._blockSize - 1)) != 0 ? 1UL : 0UL);

  /// <summary>1.x inodes: 4-bit uid/gid indexes, the type also extends the uid index.</summary>
  private int Parse1(Node n, ReadOnlySpan<byte> p) {
    if (p.Length < 4)
      return 0;
    var t = this.U16(p);
    if (this._be) {
      n.Type = t >> 12;
      n.Mode = t & 0xFFF;
      n.Uid = p[2] >> 4;
      n.Gid = p[2] & 0xF;
    } else {
      n.Type = t & 0xF;
      n.Mode = t >> 4;
      n.Uid = p[2] & 0xF;
      n.Gid = p[2] >> 4;
    }

    if (n.Type == TypeIpc) {
      var b = p[3];
      n.Type = this._be ? b >> 4 : b & 0xF;
      n.Offset = (uint)(this._be ? b & 0xF : b >> 4);
      return n.Type is TypeFifo or TypeSock ? 4 : 0;
    }

    n.Type--;
    n.Uid += n.Type / 5 * 16;
    n.Type = n.Type % 5 + 1;

    switch (n.Type) {
      case TypeFile: {
        if (p.Length < 15)
          return 0;
        n.MTimeAt = 3;
        n.StartBlock = this.U32(p[7..]);
        n.FileSize = this.U32(p[11..]);
        var pos = (long)this.NumBlocks(n) * 2 + 15;
        return pos <= p.Length ? (int)pos : 0;
      }
      case TypeDir: {
        if (p.Length < 14)
          return 0;
        this.ReadDirSizeOffset(n, this.U32(p[3..]), 19, 13);
        n.MTimeAt = 7;
        n.StartBlock = this.Start24(p[10..]);
        return 14;
      }
    }
    if (p.Length < 5)
      return 0;
    if (n.Type == TypeLink) {
      n.FileSize = this.U16(p[3..]);
      var len = (int)n.FileSize + 5;
      return len <= p.Length ? len : 0;
    }
    return 5; // block or character device
  }

  private int Parse2(Node n, ReadOnlySpan<byte> p) {
    if (p.Length < 4)
      return 0;
    this.TypeMode(n, p);
    n.Uid = p[2];
    n.Gid = p[3];

    if (n.Type == TypeFile) {
      if (p.Length < 24)
        return 0;
      n.MTimeAt = 4;
      n.StartBlock = this.U32(p[8..]);
      n.Frag = this.U32(p[12..]);
      n.Offset = this.U32(p[16..]);
      n.FileSize = this.U32(p[20..]);
      var pos = (long)this.NumBlocks(n) * 4 + 24;
      return pos <= p.Length ? (int)pos : 0;
    }

    switch (n.Type) {
      case TypeDir:
        if (p.Length < 15)
          return 0;
        this.ReadDirSizeOffset(n, this.U32(p[4..]), 19, 13);
        n.MTimeAt = 8;
        n.StartBlock = this.Start24(p[11..]);
        return 15;
      case TypeDir + 7: {
        if (p.Length < 18)
          return 0;
        this.ReadLongDirSizeOffset(n, this.U32(p[4..]), this.U16(p[7..]));
        n.MTimeAt = 9;
        n.StartBlock = this.Start24(p[12..]);
        int count = this.U16(p[16..]);
        var pos = 18;
        for (var i = 0; i < count; ++i) {
          if (pos + 8 > p.Length)
            return 0;
          pos += 8 + p[pos + 7] + 1;
          if (pos > p.Length)
            return 0;
        }
        return pos;
      }
      case TypeFifo or TypeSock:
        return 4;
    }
    if (p.Length < 6)
      return 0;
    if (n.Type == TypeLink) {
      n.FileSize = this.U16(p[4..]);
      var len = (int)n.FileSize + 6;
      return len <= p.Length ? len : 0;
    }
    return n.Type is 4 or 5 ? 6 : 0;
  }

  private int Parse3(Node n, ReadOnlySpan<byte> p) {
    if (p.Length < 12)
      return 0;
    this.TypeMode(n, p);
    n.Uid = p[2];
    n.Gid = p[3];
    n.MTimeAt = 4;

    if (n.Type is TypeFile or TypeFile + 7) {
      int at;
      if (n.Type == TypeFile) {
        if (p.Length < 32)
          return 0;
        n.StartBlock = this.U64(p[12..]);
        n.Frag = this.U32(p[20..]);
        n.Offset = this.U32(p[24..]);
        n.FileSize = this.U32(p[28..]);
        at = 32;
      } else {
        if (p.Length < 40)
          return 0;
        n.StartBlock = this.U64(p[16..]);
        n.Frag = this.U32(p[24..]);
        n.Offset = this.U32(p[28..]);
        n.FileSize = this.U64(p[32..]);
        at = 40;
      }
      var pos = this.NumBlocks(n) * 4 + (ulong)at;
      return pos <= (ulong)p.Length ? (int)pos : 0;
    }

    if (p.Length < 16)
      return 0;
    switch (n.Type) {
      case TypeDir:
        if (p.Length < 28)
          return 0;
        this.ReadDirSizeOffset(n, this.U32(p[16..]), 19, 13);
        n.StartBlock = this.U32(p[20..]);
        return 28;
      case TypeDir + 7: {
        if (p.Length < 31)
          return 0;
        this.ReadLongDirSizeOffset(n, this.U32(p[16..]), this.U16(p[19..]));
        n.StartBlock = this.U32(p[21..]);
        int count = this.U16(p[25..]);
        var pos = 31;
        for (var i = 0; i < count; ++i) {
          if (pos + 9 > p.Length)
            return 0;
          pos += 9 + p[pos + 8] + 1;
          if (pos > p.Length)
            return 0;
        }
        return pos;
      }
      case TypeFifo or TypeSock:
        return 16;
    }
    if (p.Length < 18)
      return 0;
    if (n.Type == TypeLink) {
      n.FileSize = this.U16(p[16..]);
      var len = (int)n.FileSize + 18;
      return len <= p.Length ? len : 0;
    }
    return n.Type is 4 or 5 ? 18 : 0;
  }

  private void TypeMode(Node n, ReadOnlySpan<byte> p) {
    var t = this.U16(p);
    (n.Type, n.Mode) = this._be ? (t >> 12, t & 0xFFF) : (t & 0xF, t >> 4);
  }

  /// <summary>A 19-bit size and 13-bit offset packed in 32 bits (bit-field order follows the byte order).</summary>
  private void ReadDirSizeOffset(Node n, uint t, int sizeBits, int offsetBits) {
    if (this._be) {
      n.FileSize = t >> offsetBits;
      n.Offset = t & ((1u << offsetBits) - 1);
    } else {
      n.FileSize = t & ((1u << sizeBits) - 1);
      n.Offset = t >> sizeBits;
    }
  }

  /// <summary>An extended directory: a 27-bit size, then a 13-bit offset straddling the next bytes.</summary>
  private void ReadLongDirSizeOffset(Node n, uint t, uint t2) {
    if (this._be) {
      n.FileSize = t >> 5;
      n.Offset = t2 & 0x1FFF;
    } else {
      n.FileSize = t & 0x7FFFFFF;
      n.Offset = t2 >> 3;
    }
  }

  /// <summary>A 24-bit start block in the 32 bits that also hold an 8-bit field before it.</summary>
  private uint Start24(ReadOnlySpan<byte> p) => this._be ? this.U32(p) & 0xFFFFFF : this.U32(p) >> 8;

  private Node? NodeAt(uint block, uint offset) {
    var pos = this._inodes.Resolve(block, offset);
    return pos is { } at && this._nodesByPos.TryGetValue(at, out var node) ? node : null;
  }

  // ── directories ──────────────────────────────────────────────────────

  private void WalkDirectory(Node dir, string prefix, int depth) {
    if (depth > MaxDirectoryDepth)
      throw new InvalidDataException("SquashFS directory tree is nested too deeply.");
    var start = this._dirs.Resolve((uint)dir.StartBlock, dir.Offset)
                ?? throw new InvalidDataException("SquashFS directory points outside the directory table.");
    var data = this._dirs.Data;
    var size = (long)dir.FileSize;
    // 3.x directory sizes count three bytes more than the listing holds.
    if (this._major >= 3) {
      if (size < 3)
        throw new InvalidDataException("SquashFS directory size is too small.");
      size -= 3;
    }
    if (size > data.Length - start)
      throw new InvalidDataException("SquashFS directory runs past the directory table.");

    var p = data.AsSpan(start, (int)size);
    var nameOffset = this._major <= 2 ? 3 : 5;
    var children = new List<(string Name, Node Node)>();
    while (!p.IsEmpty) {
      uint count, inodeBlock;
      if (this._major <= 2) {
        if (p.Length < 4)
          throw new InvalidDataException("SquashFS directory header is truncated.");
        count = p[0];
        inodeBlock = this.Start24(p);
        p = p[4..];
      } else {
        if (p.Length < 9)
          throw new InvalidDataException("SquashFS directory header is truncated.");
        count = p[0];
        inodeBlock = this.U32(p[1..]);
        p = p[9..];
      }
      for (var i = 0u; i <= count; ++i) {
        if (p.Length < nameOffset)
          throw new InvalidDataException("SquashFS directory entry is truncated.");
        var t = this.U16(p);
        var offset = this._be ? (uint)(t >> 3) : t & 0x1FFFu;
        var nameLength = p[2] + 1;
        p = p[nameOffset..];
        if (p.Length < nameLength)
          throw new InvalidDataException("SquashFS directory entry name is truncated.");
        var nameBytes = p[..nameLength];
        var zero = nameBytes.IndexOf((byte)0);
        if (zero >= 0)
          nameBytes = nameBytes[..zero];
        var name = Encoding.UTF8.GetString(nameBytes);
        p = p[nameLength..];
        if (name.Length == 0 || name is "." or ".." || name.Contains('/'))
          throw new InvalidDataException($"SquashFS directory entry has an invalid name '{name}'.");
        var node = this.NodeAt(inodeBlock, offset)
                   ?? throw new InvalidDataException($"SquashFS entry '{name}' points to no inode.");
        children.Add((name, node));
      }
    }

    foreach (var (name, node) in children) {
      var path = prefix.Length == 0 ? name : prefix + "/" + name;
      var entry = this.MakeEntry(name, path, node);
      this.Entries.Add(entry);
      this._nodeOf[entry] = node;
      if (node.IsDir)
        this.WalkDirectory(node, path, depth + 1);
    }
  }

  private SquashFsEntry MakeEntry(string name, string path, Node node) {
    var inode = this._inodes.Data.AsSpan(node.Pos);
    string? target = null;
    if (node.IsLink) {
      var at = this._major switch { 1 => 5, 2 => 6, _ => 18 };
      target = Encoding.UTF8.GetString(inode.Slice(at, (int)node.FileSize));
    }
    var mtime = node.MTimeAt != 0 ? DateTimeOffset.FromUnixTimeSeconds(this.U32(inode[node.MTimeAt..])).UtcDateTime : DateTime.UnixEpoch;
    var uid = node.Uid < this._uids.Length ? this._uids[node.Uid] : 0u;
    // The "special" gid index means: the group is the user's id.
    var gidSpecial = this._major <= 1 ? 0xF : 0xFF;
    var gid = node.Gid == gidSpecial ? uid : node.Gid < this._gids.Length ? this._gids[node.Gid] : 0u;
    return new SquashFsEntry {
      Name = name,
      FullPath = path,
      Size = node.IsDir ? 0 : node.IsLink ? (long)node.FileSize : node.IsFile ? (long)node.FileSize : 0,
      IsDirectory = node.IsDir,
      IsSymlink = node.IsLink,
      SymlinkTarget = target,
      ModifiedTime = mtime,
      Permissions = node.Mode & 0xFFF,
      Uid = uid,
      Gid = gid,
    };
  }

  // ── file data ────────────────────────────────────────────────────────

  public byte[] Extract(SquashFsEntry entry) {
    if (!this._nodeOf.TryGetValue(entry, out var node))
      throw new ArgumentException("The entry does not belong to this image.", nameof(entry));
    if (!node.IsFile)
      return [];
    if (node.FileSize > int.MaxValue)
      throw new NotSupportedException("SquashFS file is too large to extract into memory.");
    var size = (int)node.FileSize;
    var result = new byte[size];
    if (size == 0)
      return result;

    var inode = this._inodes.Data.AsSpan(node.Pos);
    var numBlocks = (int)this.NumBlocks(node);
    var listAt = this._major switch {
      1 => 15,
      2 => 24,
      _ => node.Type == TypeFile ? 32 : 40,
    };
    var blockOffset = (long)node.StartBlock;
    var written = 0;
    for (var i = 0; i < numBlocks; ++i) {
      uint stored;
      bool compressed;
      if (this._major == 1) {
        uint t = this.U16(inode[(listAt + i * 2)..]);
        compressed = (t & NotCompressed16) == 0;
        stored = t == NotCompressed16 ? t : t & ~NotCompressed16;
      } else {
        var t = this.U32(inode[(listAt + i * 4)..]);
        compressed = (t & NotCompressed32) == 0;
        stored = t & ~NotCompressed32;
      }
      if (stored > this._blockSize)
        throw new InvalidDataException("SquashFS data block is larger than the block size.");
      var expected = Math.Min((int)this._blockSize, size - written);
      if (stored == 0) {
        written += expected; // a sparse block: zeros
        continue;
      }
      var block = this.ReadBlock(blockOffset, stored, compressed);
      if (block.Length != expected)
        throw new InvalidDataException($"SquashFS data block {i} of '{entry.FullPath}' decodes to {block.Length} bytes, expected {expected}.");
      block.CopyTo(result, written);
      written += expected;
      blockOffset += stored;
    }

    if (written < size) {
      if (!node.HasFragment || node.Frag >= this._fragments.Length)
        throw new InvalidDataException($"SquashFS file '{entry.FullPath}' ends in a fragment that does not exist.");
      var (start, raw) = this._fragments[node.Frag];
      var stored = raw & ~NotCompressed32;
      if (stored > this._blockSize)
        throw new InvalidDataException("SquashFS fragment block is larger than the block size.");
      var fragment = this.ReadBlock(start, stored, (raw & NotCompressed32) == 0);
      var tail = size - written;
      if ((long)node.Offset + tail > fragment.Length)
        throw new InvalidDataException($"SquashFS file '{entry.FullPath}' reaches past the end of its fragment.");
      fragment.AsSpan((int)node.Offset, tail).CopyTo(result.AsSpan(written));
    }
    return result;
  }

  private byte[] ReadBlock(long offset, uint stored, bool compressed) {
    if (offset < 0 || offset + stored > this._stream.Length)
      throw new InvalidDataException("SquashFS data block lies outside the image.");
    var raw = new byte[stored];
    this._stream.Position = offset;
    this._stream.ReadExactly(raw);
    return compressed ? this.Inflate(raw, this._blockSize) : raw;
  }
}
