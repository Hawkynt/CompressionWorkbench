using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileSystem.Axfs;

/// <summary>One inode of an AXFS image.</summary>
/// <param name="Path">Slash-separated path from the root.</param>
/// <param name="Index">Inode index (the root is 0).</param>
/// <param name="Mode">Full Unix mode word.</param>
/// <param name="Uid">Owner.</param>
/// <param name="Gid">Group.</param>
/// <param name="Size">File size, symlink target length, or (special files) the encoded device number.</param>
public sealed record AxfsEntry(string Path, ulong Index, uint Mode, uint Uid, uint Gid, ulong Size) {
  /// <summary>True for a directory.</summary>
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  /// <summary>True for a regular file.</summary>
  public bool IsFile => (this.Mode & 0xF000) == 0x8000;
  /// <summary>True for a symbolic link.</summary>
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
}

/// <summary>
/// Reads an AXFS ("Advanced XIP File System") image as <c>mkfs.axfs</c> writes it:
/// a big-endian superblock pointing at region descriptors, byte tables that map
/// inodes to names, modes and nodes, and data held in an XIP, a byte-aligned or a
/// zlib-compressed region. See <c>docs/AXFS-ON-DISK.md</c>.
/// </summary>
public sealed class AxfsReader {
  /// <summary>Superblock magic, big-endian at offset 0.</summary>
  public const uint Magic = 0x48A0E4CD;

  /// <summary>Superblock signature at offset 4.</summary>
  public static ReadOnlySpan<byte> Signature => "Advanced XIP FS\0"u8;

  // Node types.
  private const ulong Xip = 0, Compressed = 1, ByteAligned = 2;

  private sealed record Region(ulong FsOffset, ulong Size, ulong CompressedSize, ulong MaxIndex, byte Depth, byte Incore);

  private readonly Stream _stream;
  private readonly Region _xip, _byteAligned, _compressed;
  private readonly Table _nodeType, _nodeIndex, _cnodeOffset, _cnodeIndex, _banodeOffset, _cblockOffset;
  private readonly Table _fileSize, _nameOffset, _numEntries, _modeIndex, _arrayIndex, _modes, _uids, _gids;
  private readonly byte[] _strings;
  private byte[]? _byteAlignedData;

  /// <summary>Version as major.minor.sub.</summary>
  public (byte Major, byte Minor, byte Sub) Version { get; }

  /// <summary>Page (node) size: <c>1 &lt;&lt; page_shift</c>.</summary>
  public int PageSize { get; }

  /// <summary>Largest uncompressed cblock.</summary>
  public uint CblockSize { get; }

  /// <summary>Inodes in the image.</summary>
  public ulong Files { get; }

  /// <summary>Distinct nodes the files and symlinks use, by kind (XIP, byte-aligned, compressed).</summary>
  public (ulong Xip, ulong ByteAligned, ulong Compressed) CountNodes() {
    ulong x = 0, b = 0, c = 0;
    var seen = new HashSet<ulong>();
    foreach (var e in this.Entries.Where(e => e.IsFile || e.IsSymlink)) {
      var pages = (e.Size + (ulong)this.PageSize - 1) / (ulong)this.PageSize;
      for (ulong p = 0; p < pages; ++p) {
        var node = this._arrayIndex[e.Index] + p;
        if (!seen.Add(node)) continue;
        switch (this._nodeType[node]) {
          case Xip: ++x; break;
          case ByteAligned: ++b; break;
          case Compressed: ++c; break;
        }
      }
    }
    return (x, b, c);
  }

  /// <summary>Build time (UNIX seconds; <c>mkfs.axfs</c> writes 0).</summary>
  public ulong Timestamp { get; }

  /// <summary>Every inode below the root, parents before children.</summary>
  public IReadOnlyList<AxfsEntry> Entries { get; }

  /// <summary>Opens <paramref name="stream"/>; throws <see cref="InvalidDataException"/> for anything it cannot read faithfully.</summary>
  public AxfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var sb = new byte[264];
    stream.Position = 0;
    if (stream.Length < sb.Length) throw new InvalidDataException("Image too short for an AXFS superblock.");
    stream.ReadExactly(sb);
    if (BinaryPrimitives.ReadUInt32BigEndian(sb) != Magic || !sb.AsSpan(4, 16).SequenceEqual(Signature))
      throw new InvalidDataException("Not an AXFS image: magic 0x48A0E4CD / \"Advanced XIP FS\" missing.");

    this.CblockSize = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(60));
    this.Files = U64(sb, 64);
    this.Version = (sb[240], sb[241], sb[242]);
    if (this.Version.Major != 2)
      throw new InvalidDataException($"AXFS version {this.Version.Major}.{this.Version.Minor}.{this.Version.Sub} is not supported (only 2.x is verified).");
    if (sb[243] != 0)
      throw new InvalidDataException($"AXFS compression type {sb[243]} is not zlib (0).");
    this.Timestamp = U64(sb, 248);
    var pageShift = sb[256];
    if (pageShift is < 9 or > 16) throw new InvalidDataException($"AXFS page shift {pageShift} is out of range.");
    this.PageSize = 1 << pageShift;
    if (this.CblockSize == 0 || this.CblockSize > 1 << 24) throw new InvalidDataException($"AXFS cblock size {this.CblockSize} is out of range.");

    Region R(int at) => this.ReadRegion(U64(sb, at));
    var strings = R(96);
    this._xip = R(104);
    this._byteAligned = R(112);
    this._compressed = R(120);
    this._nodeType = this.LoadTable(R(128));
    this._nodeIndex = this.LoadTable(R(136));
    this._cnodeOffset = this.LoadTable(R(144));
    this._cnodeIndex = this.LoadTable(R(152));
    this._banodeOffset = this.LoadTable(R(160));
    this._cblockOffset = this.LoadTable(R(168));
    this._fileSize = this.LoadTable(R(176));
    this._nameOffset = this.LoadTable(R(184));
    this._numEntries = this.LoadTable(R(192));
    this._modeIndex = this.LoadTable(R(200));
    this._arrayIndex = this.LoadTable(R(208));
    this._modes = this.LoadTable(R(216));
    this._uids = this.LoadTable(R(224));
    this._gids = this.LoadTable(R(232));
    this._strings = this.LoadRegion(strings);

    if (this.Files == 0 || (this.Mode(0) & 0xF000) != 0x4000)
      throw new InvalidDataException("AXFS root inode 0 is not a directory.");
    var entries = new List<AxfsEntry>();
    this.Walk(0, "", entries, [0]);
    this.Entries = entries;
  }

  /// <summary>Returns a regular file's bytes, or a symlink's target bytes.</summary>
  public byte[] Extract(AxfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.IsFile && !entry.IsSymlink) return [];
    if (entry.Size > int.MaxValue) throw new InvalidDataException($"AXFS file '{entry.Path}' is too large to buffer.");
    var size = (int)entry.Size;
    var data = new byte[size];
    var pages = (size + this.PageSize - 1) / this.PageSize;
    var first = this._arrayIndex[entry.Index];
    byte[]? cblock = null;
    ulong cblockIndex = ulong.MaxValue;
    for (var p = 0; p < pages; ++p) {
      var node = first + (ulong)p;
      var type = this._nodeType[node];
      var index = this._nodeIndex[node];
      var want = Math.Min(this.PageSize, size - p * this.PageSize);
      var dest = data.AsSpan(p * this.PageSize, want);
      switch (type) {
        case Compressed: {
          var ci = this._cnodeIndex[index];
          var offset = this._cnodeOffset[index];
          if (ci != cblockIndex) {
            var start = this._cblockOffset[ci];
            var end = this._cblockOffset[ci + 1];
            if (end < start || end > this._compressed.Size)
              throw new InvalidDataException($"AXFS cblock {ci} lies outside the compressed region.");
            cblock = Inflate(this.ReadRaw(this._compressed.FsOffset + start, (int)(end - start)), (int)this.CblockSize);
            cblockIndex = ci;
          }
          CopyFrom(cblock!, offset, dest, entry.Path);
          break;
        }
        case ByteAligned:
          this._byteAlignedData ??= this.LoadRegion(this._byteAligned);
          CopyFrom(this._byteAlignedData, this._banodeOffset[index], dest, entry.Path);
          break;
        case Xip: {
          var at = (ulong)index * (ulong)this.PageSize;
          if (at + (ulong)want > this._xip.Size)
            throw new InvalidDataException($"AXFS XIP node {index} lies outside the XIP region.");
          this.ReadRaw(this._xip.FsOffset + at, want).CopyTo(dest);
          break;
        }
        default:
          throw new InvalidDataException($"AXFS node {node} of '{entry.Path}' has unknown type {type}.");
      }
    }
    return data;
  }

  private static void CopyFrom(byte[] source, ulong offset, Span<byte> dest, string path) {
    if (offset > (ulong)source.Length || (ulong)source.Length - offset < (ulong)dest.Length)
      throw new InvalidDataException($"AXFS node of '{path}' points outside its region.");
    source.AsSpan((int)offset, dest.Length).CopyTo(dest);
  }

  private uint Mode(ulong ino) => (uint)this._modes[this._modeIndex[ino]];

  private void Walk(ulong dir, string prefix, List<AxfsEntry> entries, HashSet<ulong> visited) {
    var count = this._numEntries[dir];
    var first = this._arrayIndex[dir];
    if (count > this.Files || first + count > this.Files)
      throw new InvalidDataException($"AXFS directory inode {dir} lists children beyond the inode count.");
    for (ulong i = 0; i < count; ++i) {
      var ino = first + i;
      var mi = this._modeIndex[ino];
      var name = this.Name(ino);
      if (name.Length == 0 || name is "." or ".." || name.Contains('/'))
        throw new InvalidDataException($"AXFS inode {ino} has an unusable name '{name}'.");
      var path = prefix.Length == 0 ? name : prefix + "/" + name;
      var e = new AxfsEntry(path, ino, (uint)this._modes[mi], (uint)this._uids[mi], (uint)this._gids[mi], this._fileSize[ino]);
      entries.Add(e);
      if (e.IsDirectory) {
        if (!visited.Add(ino)) throw new InvalidDataException($"AXFS directory inode {ino} appears twice in the tree.");
        this.Walk(ino, path, entries, visited);
      }
    }
  }

  private string Name(ulong ino) {
    var offset = this._nameOffset[ino];
    if (offset >= (ulong)this._strings.Length) throw new InvalidDataException($"AXFS name of inode {ino} lies outside the strings region.");
    var span = this._strings.AsSpan((int)offset);
    var end = span.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
  }

  private Region ReadRegion(ulong at) {
    if (at + 34 > (ulong)this._stream.Length) throw new InvalidDataException($"AXFS region descriptor at {at} lies outside the image.");
    var raw = this.ReadRaw(at, 34);
    return new(U64(raw, 0), U64(raw, 8), U64(raw, 16), U64(raw, 24), raw[32], raw[33]);
  }

  /// <summary>A region's bytes; a region with a compressed size is one zlib stream inflating to its size.</summary>
  private byte[] LoadRegion(Region r) {
    if (r.Size > int.MaxValue) throw new InvalidDataException("AXFS region is too large to load.");
    if (r.CompressedSize == 0) return r.Size == 0 ? [] : this.ReadRaw(r.FsOffset, (int)r.Size);
    if (r.CompressedSize > int.MaxValue) throw new InvalidDataException("AXFS compressed region is too large to load.");
    return Inflate(this.ReadRaw(r.FsOffset, (int)r.CompressedSize), (int)r.Size, exact: true);
  }

  private Table LoadTable(Region r) {
    var data = this.LoadRegion(r);
    if (r.Depth > 8) throw new InvalidDataException($"AXFS byte table depth {r.Depth} exceeds 8.");
    if ((ulong)data.Length < r.MaxIndex * r.Depth) throw new InvalidDataException("AXFS byte table is shorter than its entry count.");
    return new Table(data, r.Depth, r.MaxIndex);
  }

  private byte[] ReadRaw(ulong offset, int length) {
    if (offset + (ulong)length > (ulong)this._stream.Length)
      throw new InvalidDataException($"AXFS data at {offset}+{length} lies outside the image.");
    var buf = new byte[length];
    this._stream.Position = (long)offset;
    this._stream.ReadExactly(buf);
    return buf;
  }

  private static byte[] Inflate(byte[] zlib, int expected, bool exact = false) {
    byte[] data;
    try {
      var (deflate, adler) = ZlibRawHelper.Unwrap(zlib);
      data = DeflateDecompressor.Decompress(deflate);
      if (Adler32.Compute(data) != adler) throw new InvalidDataException("AXFS zlib block fails its Adler-32 check.");
    } catch (Exception ex) when (ex is not InvalidDataException) {
      throw new InvalidDataException("AXFS zlib block does not decode.", ex);
    }
    if (exact ? data.Length != expected : data.Length > expected)
      throw new InvalidDataException($"AXFS zlib block inflated to {data.Length} bytes, expected {(exact ? "" : "at most ")}{expected}.");
    return data;
  }

  private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64BigEndian(b[at..]);

  /// <summary>A byte table: entry <c>i</c> is <c>depth</c> big-endian bytes at <c>i × depth</c>.</summary>
  private sealed class Table(byte[] data, byte depth, ulong count) {
    public ulong this[ulong index] {
      get {
        if (index >= count) throw new InvalidDataException($"AXFS byte table index {index} beyond its {count} entries.");
        ulong v = 0;
        var at = (long)(index * depth);
        for (var i = 0; i < depth; ++i) v = (v << 8) | data[at + i];
        return v;
      }
    }
  }
}
