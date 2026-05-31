#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

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

  private readonly byte[] _image;
  private readonly List<FileEntry> _entries = [];

  // inode -> list of data fragments (offset in file, data bytes)
  private readonly Dictionary<uint, List<(uint Offset, byte[] Data, uint DSize, uint Version)>> _inodeData = new();

  // inode -> latest inode node info (size)
  private readonly Dictionary<uint, uint> _inodeSize = new();

  // inode -> mode (S_IFDIR / S_IFREG) from the latest inode node, used to
  // distinguish directory inodes from regular files.
  private readonly Dictionary<uint, uint> _inodeMode = new();

  public Jffs2FileReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._image = ms.ToArray();
    this.Parse();
  }

  public Jffs2FileReader(byte[] image) {
    this._image = image;
    this.Parse();
  }

  /// <summary>All file entries discovered in the image.</summary>
  public IReadOnlyList<FileEntry> Entries => this._entries;

  /// <summary>Extracts the data for the given file entry.</summary>
  public byte[] Extract(FileEntry entry) {
    if (!this._inodeData.TryGetValue(entry.Inode, out var fragments) || fragments.Count == 0)
      return [];

    // Determine file size from the latest inode node
    var fileSize = this._inodeSize.TryGetValue(entry.Inode, out var sz) ? sz : 0u;
    if (fileSize == 0) {
      // Fallback: sum of fragment sizes
      fileSize = (uint)fragments.Max(f => f.Offset + f.DSize);
    }

    var result = new byte[fileSize];
    // Sort fragments by version then offset to handle multi-version correctly
    foreach (var (offset, data, dsize, _) in fragments.OrderBy(f => f.Offset)) {
      var copyLen = (int)Math.Min(dsize, fileSize - offset);
      if (copyLen > 0 && data.Length > 0)
        Array.Copy(data, 0, result, offset, Math.Min(copyLen, data.Length));
    }
    return result;
  }

  private void Parse() {
    var span = this._image.AsSpan();
    var dirents = new List<(uint ParentInode, uint Inode, string Name, byte Type, int NodeOffset)>();
    var off = 0;

    while (off + 12 <= span.Length) {
      var magic = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off, 2));
      if (magic != Magic) {
        off += 4;
        continue;
      }

      var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + 2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 4, 4));

      if (totLen < 12 || totLen > span.Length || off + (int)totLen > span.Length) {
        off += 4;
        continue;
      }

      switch (nodeType) {
        case NodeTypeInode:
          this.ParseInodeNode(span, off, totLen);
          break;
        case NodeTypeDirent:
          var dirent = ParseDirentNode(span, off, totLen);
          if (dirent.HasValue)
            dirents.Add((dirent.Value.ParentInode, dirent.Value.Inode, dirent.Value.Name, dirent.Value.Type, off));
          break;
      }

      off += ((int)totLen + 3) & ~3;
    }

    // Index dirents by their target inode so the parent chain can be walked to
    // reassemble each entry's full path. The latest dirent for an inode wins.
    var direntByInode = new Dictionary<uint, (uint ParentInode, string Name)>();
    foreach (var (parentInode, inode, name, _, _) in dirents) {
      if (inode == 0) continue; // unlink marker
      direntByInode[inode] = (parentInode, name);
    }

    // Build entries from dirents, skipping unlinks. A dirent denotes a directory
    // when its type is DT_DIR or its target inode carries an S_IFDIR mode.
    foreach (var (parentInode, inode, name, type, nodeOffset) in dirents) {
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

  private void ParseInodeNode(ReadOnlySpan<byte> span, int off, uint totLen) {
    if (off + InodeNodeHeaderSize > span.Length) return;

    var ino = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 12, 4));
    var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 20, 4));
    var isize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 28, 4));
    var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 44, 4));
    var csize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 48, 4));
    var dsize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 52, 4));
    var compr = span[off + 56];

    // Track file size and mode (latest version wins)
    if (!this._inodeSize.ContainsKey(ino) || version > 0) {
      this._inodeSize[ino] = isize;
      this._inodeMode[ino] = mode;
    }

    // Extract data if present and uncompressed
    if (csize > 0 && compr == 0x00 && off + InodeNodeHeaderSize + (int)csize <= span.Length) {
      var data = span.Slice(off + InodeNodeHeaderSize, (int)csize).ToArray();
      if (!this._inodeData.ContainsKey(ino))
        this._inodeData[ino] = [];
      this._inodeData[ino].Add((dataOffset, data, dsize, version));
    } else if (csize == 0 && dsize == 0) {
      // Zero-length data node (e.g., directory or empty file)
      if (!this._inodeData.ContainsKey(ino))
        this._inodeData[ino] = [];
    }
  }

  private static (uint ParentInode, uint Inode, string Name, byte Type)? ParseDirentNode(ReadOnlySpan<byte> span, int off, uint totLen) {
    if (off + DirentNodeHeaderSize > span.Length) return null;
    var parent = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 12, 4));
    var inode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 20, 4));
    var nsize = span[off + 28];
    var type = span[off + 29];
    if (nsize == 0 || nsize > 128) return null;
    if (off + DirentNodeHeaderSize + nsize > span.Length) return null;
    var name = Encoding.UTF8.GetString(span.Slice(off + DirentNodeHeaderSize, nsize));
    return (parent, inode, name, type);
  }
}
