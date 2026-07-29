#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Jffs2;

/// <summary>
/// Reads a JFFS2 image and extracts actual file contents by reassembling
/// inode data nodes and matching dirent nodes. Handles the common case of
/// uncompressed, non-fragmented single-version files. Nested entries are
/// reassembled to their full path by walking each dirent's parent-inode (pino)
/// chain back to the root directory.
/// </summary>
public sealed class Jffs2FileReader {
  private const ushort Magic = 0x1985;
  private const ushort NodeTypeDirent = 0xE001;
  private const ushort NodeTypeInode = 0xE002;
  private const int InodeNodeHeaderSize = 68;
  private const int DirentNodeHeaderSize = 40;
  private const uint RootInode = 1;

  /// <summary>Bytes read per node probe: the largest fixed header plus the longest name a dirent can carry.</summary>
  private const int MaxHeaderRead = DirentNodeHeaderSize + 128;

  /// <summary>DT_DIR — directory entry type in a dirent node.</summary>
  private const byte DtDir = 4;

  /// <summary>S_IFMT mask and S_IFDIR value for inode modes.</summary>
  private const uint ModeFormatMask = 0xF000;
  private const uint ModeDirectory = 0x4000;

  /// <summary>
  /// A file or directory entry found in the JFFS2 image. <see cref="Name"/> is
  /// the full path from the root (e.g. "docs/api/reference.txt").
  /// </summary>
  public sealed record FileEntry(string Name, uint Inode, long NodeOffset, bool IsDirectory);

  private readonly ImageAccessor _accessor;
  private readonly List<FileEntry> _entries = [];

  // inode -> list of data fragments. A fragment records WHERE its bytes live in
  // the image rather than the bytes themselves, so a multi-gigabyte file costs
  // one small record per page instead of its own copy in memory.
  private readonly Dictionary<uint, List<Fragment>> _inodeData = new();

  /// <summary>One data node's contribution: <paramref name="FileOffset" /> bytes into the file,
  /// <paramref name="ImageOffset" /> bytes into the image, <paramref name="Length" /> bytes long.</summary>
  private readonly record struct Fragment(uint FileOffset, long ImageOffset, uint Length, uint DSize, uint Version);

  // inode -> latest (highest-version) isize.
  private readonly Dictionary<uint, uint> _inodeSize = new();

  // inode -> mode (S_IFDIR / S_IFREG) from the highest-version inode node,
  // used to distinguish directory inodes from regular files.
  private readonly Dictionary<uint, uint> _inodeMode = new();

  // Highest version seen per inode while scanning, so later passes can compare.
  private readonly Dictionary<uint, uint> _inodeMaxVersion = new();

  public Jffs2FileReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._accessor = new ImageAccessor(stream);
    this.Parse();
  }

  public Jffs2FileReader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this._accessor = ImageAccessor.FromBytes(image);
    this.Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._accessor.Length;

  /// <summary>The reassembled length of <paramref name="entry" />'s contents.</summary>
  public long SizeOf(FileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (this._inodeSize.TryGetValue(entry.Inode, out var sz) && sz > 0) return sz;
    return this._inodeData.TryGetValue(entry.Inode, out var frags) && frags.Count > 0
      ? frags.Max(f => (long)f.FileOffset + f.DSize)
      : 0;
  }

  /// <summary>All file entries discovered in the image.</summary>
  public IReadOnlyList<FileEntry> Entries => this._entries;

  /// <summary>Extracts the data for the given file entry.</summary>
  public byte[] Extract(FileEntry entry) {
    var size = this.SizeOf(entry);
    if (size <= 0) return [];
    if (size > Array.MaxLength)
      throw new IOException(
        $"JFFS2: '{entry.Name}' is {size:N0} bytes, past the array limit; use ExtractTo.");

    var result = new byte[size];
    using var target = new MemoryStream(result, writable: true);
    this.ExtractTo(entry, target);
    return result;
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s reassembled contents into
  /// <paramref name="destination" />, one fragment at a time. Returns the byte count.
  /// </summary>
  public long ExtractTo(FileEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);

    var fileSize = this.SizeOf(entry);
    if (fileSize <= 0) return 0;
    if (!this._inodeData.TryGetValue(entry.Inode, out var fragments) || fragments.Count == 0)
      return 0;

    // Holes between fragments read back as zeros, which is what a sparse JFFS2
    // file means: no data node covers that range.
    var buffer = new byte[64 * 1024];
    long written = 0;
    foreach (var f in fragments.OrderBy(f => f.FileOffset)) {
      if (f.FileOffset >= fileSize) break;
      var span = Math.Min(Math.Min(f.DSize, f.Length), (uint)(fileSize - f.FileOffset));
      if (span == 0) continue;
      while (written < f.FileOffset) {
        var gap = (int)Math.Min(buffer.Length, f.FileOffset - written);
        Array.Clear(buffer, 0, gap);
        destination.Write(buffer, 0, gap);
        written += gap;
      }
      if (written > f.FileOffset) continue; // overlapping fragment — first write wins
      this._accessor.CopyTo(f.ImageOffset, destination, span);
      written += span;
    }
    while (written < fileSize) {
      var gap = (int)Math.Min(buffer.Length, fileSize - written);
      Array.Clear(buffer, 0, gap);
      destination.Write(buffer, 0, gap);
      written += gap;
    }
    return written;
  }

  /// <summary>
  /// Opens <paramref name="entry" />'s contents as a seekable read-only stream over
  /// the image's data nodes. Nothing is copied: a read resolves the position to the
  /// fragment covering it and pulls those bytes straight out of the volume. Ranges no
  /// fragment covers — sparse holes — read back as zeros.
  /// </summary>
  public Stream OpenEntry(FileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var size = this.SizeOf(entry);
    var fragments = this._inodeData.TryGetValue(entry.Inode, out var f)
      ? f.Where(x => x.FileOffset < size).OrderBy(x => x.FileOffset).ToArray()
      : [];
    return new FragmentStream(this._accessor, fragments, size);
  }

  /// <summary>Read-only view of one inode's data nodes, addressed by position in the file.</summary>
  private sealed class FragmentStream(ImageAccessor accessor, Fragment[] fragments, long length) : Stream {

    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position {
      get => this._position;
      set => this._position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) {
      ArgumentNullException.ThrowIfNull(buffer);
      return this.Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer) {
      if (this._position >= length || buffer.Length == 0) return 0;
      var want = (int)Math.Min(buffer.Length, length - this._position);

      // Find the fragment covering the current position, or the next one along.
      var index = this.IndexAt(this._position);
      if (index < 0) {
        // Past the last fragment: the tail of the file is a hole.
        buffer[..want].Clear();
        this._position += want;
        return want;
      }

      var fragment = fragments[index];
      var span = Math.Min((long)fragment.DSize, fragment.Length);
      if (this._position < fragment.FileOffset) {
        // Hole before the next fragment.
        var gap = (int)Math.Min(want, fragment.FileOffset - this._position);
        buffer[..gap].Clear();
        this._position += gap;
        return gap;
      }

      var into = this._position - fragment.FileOffset;
      var take = (int)Math.Min(want, span - into);
      if (take <= 0) {
        buffer[..want].Clear();
        this._position += want;
        return want;
      }

      var read = accessor.Read(fragment.ImageOffset + into, buffer[..take]);
      this._position += read;
      return read;
    }

    /// <summary>Index of the fragment covering <paramref name="position" />, else the next one; -1 past the end.</summary>
    private int IndexAt(long position) {
      for (var i = 0; i < fragments.Length; ++i) {
        var f = fragments[i];
        var end = f.FileOffset + Math.Min((long)f.DSize, f.Length);
        if (position < end) return i;
      }
      return -1;
    }

    public override long Seek(long offset, SeekOrigin origin) {
      this.Position = origin switch {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => this._position + offset,
        SeekOrigin.End => length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
      };
      return this._position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private void Parse() {
    var length = this._accessor.Length;
    // (pino, name) -> (version, ino, type, offset). Highest-version wins;
    // a winning entry with ino==0 marks an unlink.
    var direntByKey = new Dictionary<(uint Pino, string Name), (uint Version, uint Inode, byte Type, long NodeOffset)>();
    var header = new byte[MaxHeaderRead];
    long off = 0;

    while (off + 12 <= length) {
      var want = (int)Math.Min(MaxHeaderRead, length - off);
      var read = this._accessor.Read(off, header.AsSpan(0, want));
      if (read < 12) break;
      var span = header.AsSpan(0, read);

      var magic = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
      if (magic != Magic) {
        off += 4;
        continue;
      }

      var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

      if (totLen < 12 || off + totLen > length) {
        off += 4;
        continue;
      }

      switch (nodeType) {
        case NodeTypeInode:
          this.ParseInodeNode(span, off, length);
          break;
        case NodeTypeDirent:
          var dirent = ParseDirentNode(span);
          if (dirent.HasValue) {
            var dv = dirent.Value;
            var key = (dv.ParentInode, dv.Name);
            if (!direntByKey.TryGetValue(key, out var existing) || dv.Version > existing.Version)
              direntByKey[key] = (dv.Version, dv.Inode, dv.Type, off);
          }
          break;
      }

      off += (totLen + 3) & ~3u;
    }

    // Index live dirents by their target inode so the parent chain can be
    // walked to reassemble each entry's full path. A "live" dirent is one
    // whose highest-version record has a non-zero inode.
    var direntByInode = new Dictionary<uint, (uint ParentInode, string Name)>();
    foreach (var kv in direntByKey) {
      var (pino, name) = kv.Key;
      var (_, ino, _, _) = kv.Value;
      if (ino == 0) continue; // unlink marker wins for this (pino, name)
      direntByInode[ino] = (pino, name);
    }

    // Build entries from live dirents only — skip unlinks. A dirent denotes a
    // directory when its type is DT_DIR or its target inode carries an S_IFDIR
    // mode.
    foreach (var kv in direntByKey) {
      var (parentInode, name) = kv.Key;
      var (_, inode, type, nodeOffset) = kv.Value;
      if (inode == 0) continue; // unlink marker

      var isDirectory = type == DtDir
        || (this._inodeMode.TryGetValue(inode, out var mode) && (mode & ModeFormatMask) == ModeDirectory);

      var hasData = this._inodeSize.ContainsKey(inode) || this._inodeData.ContainsKey(inode);
      if (!isDirectory && !hasData)
        continue;

      var fullPath = BuildFullPath(parentInode, name, direntByInode);
      this._entries.Add(new FileEntry(fullPath, inode, nodeOffset, isDirectory));
    }
  }

  /// <summary>
  /// Reassembles a full path by prepending each ancestor directory's name,
  /// walking the parent-inode (pino) chain up to the root. Guards against cycles
  /// from malformed images.
  /// </summary>
  private static string BuildFullPath(uint parentInode, string leafName, Dictionary<uint, (uint ParentInode, string Name)> direntByInode) {
    var segments = new List<string> { leafName };
    var current = parentInode;
    var guard = 0;
    while (current != RootInode && current != 0 && guard++ < 256) {
      if (!direntByInode.TryGetValue(current, out var parent))
        break;
      segments.Add(parent.Name);
      current = parent.ParentInode;
    }

    segments.Reverse();
    return string.Join('/', segments);
  }

  private void ParseInodeNode(ReadOnlySpan<byte> span, long off, long imageLength) {
    if (span.Length < InodeNodeHeaderSize) return;

    var ino = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
    var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
    var isize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));
    var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(44, 4));
    var csize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(48, 4));
    var dsize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(52, 4));
    var compr = span[56];

    var prevMaxVersion = this._inodeMaxVersion.GetValueOrDefault(ino, 0u);
    var startingFresh = version > prevMaxVersion;

    if (startingFresh) {
      // New high-water version: discard any data fragments belonging to lower
      // versions (the JFFS2 "newest write wins" semantic).
      this._inodeSize[ino] = isize;
      this._inodeMode[ino] = mode;
      this._inodeMaxVersion[ino] = version;
      this._inodeData[ino] = [];
    } else if (version < prevMaxVersion) {
      // Stale version — ignore. (It still lives in the byte stream so older
      // tooling can replay the log, but it's not contributing to the current
      // view of the file.)
      return;
    }
    // version == prevMaxVersion: same write, contribute additional fragments.

    if (!this._inodeData.ContainsKey(ino))
      this._inodeData[ino] = [];

    // Record where the data lives if present and uncompressed.
    if (csize > 0 && compr == 0x00 && off + InodeNodeHeaderSize + csize <= imageLength)
      this._inodeData[ino].Add(new Fragment(dataOffset, off + InodeNodeHeaderSize, csize, dsize, version));
    // csize == 0 && dsize == 0: zero-length data node (e.g. directory or empty
    // file) — no fragment to add but the bucket is already initialised above.
  }

  private static (uint ParentInode, uint Inode, string Name, byte Type, uint Version)? ParseDirentNode(ReadOnlySpan<byte> span) {
    if (span.Length < DirentNodeHeaderSize) return null;
    var parent = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
    var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
    var inode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
    var nsize = span[28];
    var type = span[29];
    if (nsize == 0 || nsize > 128) return null;
    if (DirentNodeHeaderSize + nsize > span.Length) return null;
    var name = Encoding.UTF8.GetString(span.Slice(DirentNodeHeaderSize, nsize));
    return (parent, inode, name, type, version);
  }
}
