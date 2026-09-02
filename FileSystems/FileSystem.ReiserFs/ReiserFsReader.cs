#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.ReiserFs;

/// <summary>
/// Reads a ReiserFS v3 filesystem image. Field offsets follow the Linux kernel
/// <c>struct reiserfs_super_block</c> (see <see cref="ReiserFsWriter"/>
/// for the full offset table).
/// </summary>
public sealed class ReiserFsReader : IDisposable {
  private const int SuperblockOffset = 65536;

  // Spec offsets within the superblock
  private const int Off_BlockCount = 0;
  private const int Off_FreeBlocks = 4;
  private const int Off_RootBlock = 8;
  private const int Off_BlockSize = 44;
  private const int Off_Magic = 52;
  private const int Off_Label = 100; // s_label[16]

  private static readonly byte[][] Magics = [
    "ReIsErFs"u8.ToArray(),   // 3.5
    "ReIsEr2Fs"u8.ToArray(),  // 3.6
    "ReIsEr3Fs"u8.ToArray(),  // 3.6 w/ non-standard journal
  ];

  private const uint RootParentObjectId = 1; // dir_id of "/"
  private const uint RootObjectId = 2;        // objectid of "/"

  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<ReiserFsEntry> _entries = [];
  private int _blockSize;
  private int _rootBlock;

  // All STAT_DATA items indexed by (dirId, objectId) → sd_mode (for dir/file
  // classification). Populated by the leaf scan before the directory walk.
  private readonly Dictionary<(uint DirId, uint ObjId), ushort> _statMode = [];
  // All DIRENTRY items indexed by (dirId, objectId). dirId here is the parent
  // of the directory; objectId is the directory's own id.
  private readonly Dictionary<(uint DirId, uint ObjId), List<DirEntry>> _dirEntries = [];

  private readonly record struct DirEntry(string Name, uint PointedDirId, uint PointedObjId);

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<ReiserFsEntry> Entries => _entries;

  /// <summary>Volume label from the superblock <c>s_label</c> field (16 bytes, NUL-trimmed ASCII).</summary>
  public string Label { get; private set; } = "";

    /// <summary>
  /// Initializes a new instance of <see cref="ReiserFsReader"/>.
  /// </summary>
public ReiserFsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: the S+tree is a small prefix however many
    // gigabytes of INDIRECT file bodies follow it.
    _img = new ImageAccessor(stream, leaveOpen);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private ushort U16(long off) => this._len >= off + 2 ? this._img.ReadUInt16(off) : (ushort)0;
  private uint U32(long off) => this._len >= off + 4 ? this._img.ReadUInt32(off) : 0u;
  private ulong U64(long off) => this._len >= off + 8 ? this._img.ReadUInt64(off) : 0UL;
  private byte B(long off) => off >= 0 && off < this._len ? this._img.ReadByte(off) : (byte)0;
  private byte[] Read(long off, int len) => this._img.Read(off, len);
  private string Str(long off, int len) => Encoding.UTF8.GetString(this._img.Read(off, len));

  private void Parse() {
    if (_len < SuperblockOffset + 128)
      throw new InvalidDataException("ReiserFS: image too small.");

    var magicSpan = Read(SuperblockOffset + Off_Magic, 10).AsSpan();
    bool found = false;
    foreach (var m in Magics) {
      if (magicSpan[..m.Length].SequenceEqual(m)) { found = true; break; }
    }
    if (!found)
      throw new InvalidDataException("ReiserFS: invalid magic.");

    _blockSize = U16((SuperblockOffset + Off_BlockSize));
    if (_blockSize == 0) _blockSize = 4096;

    var labelSpan = Read(SuperblockOffset + Off_Label, 16).AsSpan();
    var labelLen = labelSpan.IndexOf((byte)0);
    if (labelLen < 0) labelLen = 16;
    this.Label = labelLen == 0 ? "" : System.Text.Encoding.ASCII.GetString(labelSpan[..labelLen]);
    _rootBlock = (int)U32((SuperblockOffset + Off_RootBlock));

    // Pass 1: scan every leaf, indexing stat-data modes and directory entries.
    ScanTree(_rootBlock);
    // Pass 2: walk the directory graph from the root, materialising full paths.
    var visited = new HashSet<(uint, uint)>();
    WalkDirectory(RootParentObjectId, RootObjectId, "", visited);
  }

  private void ScanTree(int blockNum) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _len) return;
    var boff = (int)blockOff;

    var level = U16((boff));
    var nrItems = U16((boff + 2));

    if (level > 1) {
      // Internal node: (nrItems+1) block-number pointers after the keys.
      var ptrsOff = boff + 24 + nrItems * 16;
      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _len) break;
        var childBlock = (int)U32((ptrOff));
        if (childBlock > 0 && childBlock < _len / _blockSize)
          ScanTree(childBlock);
      }
      return;
    }

    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _len) break;

      var keyDirId = U32((ihOff + 0));
      var keyObjId = U32((ihOff + 4));
      var ihCount = U16((ihOff + 16));
      var ihLength = U16((ihOff + 18));
      var ihLocation = U16((ihOff + 20));

      var dataOff = boff + ihLocation;
      if (dataOff < 0 || dataOff + ihLength > _len) continue;

      var itemType = ResolveItemType(ihOff);

      if (itemType == 0) {
        // STAT_DATA — record sd_mode (le16 at body +0) for dir/file detection.
        if (ihLength >= 2)
          _statMode[(keyDirId, keyObjId)] = U16((dataOff));
        continue;
      }

      if (itemType == 3 && ihCount > 0 && ihCount < 0x4000 && ihLength >= ihCount * 16) {
        var list = ReadDirEntries(dataOff, ihLength, ihCount);
        // A directory's entries can span multiple DIRENTRY items; merge.
        if (_dirEntries.TryGetValue((keyDirId, keyObjId), out var existing))
          existing.AddRange(list);
        else
          _dirEntries[(keyDirId, keyObjId)] = list;
      }
    }
  }

  private List<DirEntry> ReadDirEntries(int dataOff, int ihLength, int ihCount) {
    var result = new List<DirEntry>(ihCount);
    // Names are packed at the END of the item and grow backward: entry[0]'s
    // name ends at item_end; entry[i]'s name ends at entry[i-1]'s deh_location.
    for (int e = 0; e < ihCount; e++) {
      var dehOff = dataOff + e * 16;
      if (dehOff + 16 > _len) break;

      var pointedDirId = U32((dehOff + 4));
      var pointedObjId = U32((dehOff + 8));
      var nameLoc = U16((dehOff + 12));
      var state = U16((dehOff + 14));

      if ((state & 4) == 0) continue; // not visible
      var nameOff = dataOff + nameLoc;
      if (nameOff < dataOff || nameOff >= dataOff + ihLength) continue;

      int nameEndInItem;
      if (e == 0) {
        nameEndInItem = ihLength;
      } else {
        var prevLoc = U16((dataOff + (e - 1) * 16 + 12));
        nameEndInItem = prevLoc;
      }
      var nameEnd = dataOff + nameEndInItem;
      // Trailing NULs are slot padding (ROUND_UP8); stop at the first one.
      for (var k = nameOff; k < nameEnd && k < _len; k++) {
        if (B(k) == 0) { nameEnd = k; break; }
      }
      if (nameEnd <= nameOff) continue;

      var name = Str(nameOff, nameEnd - nameOff);
      if (name == "." || name == "..") continue;
      if (!name.All(c => c >= 0x20 && c < 0x7F)) continue;
      result.Add(new DirEntry(name, pointedDirId, pointedObjId));
    }
    return result;
  }

  /// <summary>
  /// Recursively materialises every visible entry under the directory whose key
  /// is (parentDirId, dirObjId). Files are added at their full path; directory
  /// children are emitted as directory entries and recursed into.
  /// </summary>
  private void WalkDirectory(uint parentDirId, uint dirObjId, string basePath, HashSet<(uint, uint)> visited) {
    if (!visited.Add((parentDirId, dirObjId))) return; // guard against cycles
    if (!_dirEntries.TryGetValue((parentDirId, dirObjId), out var entries)) return;

    foreach (var entry in entries) {
      var childKey = (entry.PointedDirId, entry.PointedObjId);
      var fullPath = string.IsNullOrEmpty(basePath) ? entry.Name : $"{basePath}/{entry.Name}";
      var isDir = _statMode.TryGetValue(childKey, out var mode) && (mode & 0xF000) == 0x4000;

      _entries.Add(new ReiserFsEntry {
        Name = fullPath,
        Size = 0, // overwritten for files during Extract from the DIRECT item length
        IsDirectory = isDir,
        DirId = entry.PointedDirId,
        ObjectId = entry.PointedObjId,
      });

      if (isDir)
        WalkDirectory(entry.PointedDirId, entry.PointedObjId, fullPath, visited);
    }
  }

  /// <summary>
  /// Resolves an item's TYPE from its key. ReiserFS encodes the type either in
  /// offset_v1.k_uniqueness (KEY_FORMAT_1, when bits 60-63 of the offset_v2
  /// union are 0 or 15) or directly in bits 60-63 of offset_v2 (KEY_FORMAT_2).
  /// Returns 0=SD, 1=INDIRECT, 2=DIRECT, 3=DIRENTRY, -1=unknown.
  /// </summary>
  private int ResolveItemType(int ihOff) {
    var keyOffsetV2 = U64((ihOff + 8));
    var typeV2 = (uint)(keyOffsetV2 >> 60);
    if (typeV2 == 0 || typeV2 == 15) {
      var uniqueness = U32((ihOff + 12));
      return uniqueness switch {
        0u => 0, 0xfffffffeu => 1, 0xffffffffu => 2, 500u => 3, _ => -1,
      };
    }
    return (int)typeV2;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(ReiserFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    // Pass 1: scan every leaf, collect this file's SD (for sd_size) + every
    // DIRECT and INDIRECT body item keyed at the same (dirId, objectId).
    //   - DIRECT body bytes are stored inline at the item's body location.
    //   - INDIRECT bodies are arrays of __le32 block pointers; each points at
    //     a 4 KB data block outside the tree.
    // The item key offset_v2 carries the byte offset within the file (1 for
    // the first body item); sort by it and concatenate in order. Truncate to
    // sd_size (a partial last INDIRECT block must shrink to the actual file
    // tail).
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"ReiserFS: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s body into <paramref name="destination" />,
  /// one item at a time. Returns the byte count.
  /// </summary>
  public long ExtractTo(ReiserFsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;

    var bodyParts = new List<BodyPart>();
    var sdSize = -1L;
    CollectFileItems(_rootBlock, entry.ObjectId, entry.DirId, bodyParts, ref sdSize);
    if (bodyParts.Count == 0 || sdSize == 0) return 0;

    bodyParts.Sort(static (a, b) => a.KeyOffset.CompareTo(b.KeyOffset));

    // Bytes past sd_size are the last block's zero padding and are dropped.
    var limit = sdSize >= 0 ? sdSize : long.MaxValue;
    var hole = new byte[_blockSize];
    long written = 0;
    foreach (var part in bodyParts) {
      if (written >= limit) break;
      if (!part.Indirect) {
        var take = (int)Math.Min(part.Length, limit - written);
        if (take <= 0) continue;
        destination.Write(this._img.Read(part.Offset, take));
        written += take;
        continue;
      }

      var ptrCount = part.Length / 4;
      for (var p = 0; p < ptrCount && written < limit; ++p) {
        var ptr = U32(part.Offset + p * 4);
        var take = (int)Math.Min(_blockSize, limit - written);
        var src = (long)ptr * _blockSize;
        // A zero pointer is a hole, and so is a pointer past the image.
        if (ptr == 0 || src < 0 || src + _blockSize > _len)
          destination.Write(hole, 0, take);
        else
          this._img.CopyTo(src, destination, take);
        written += take;
      }
    }
    return written;
  }

  /// <summary>
  /// Where on disk <paramref name="entry" />'s bytes actually sit, as runs of
  /// whole blocks, along with the byte offset of the first pointer that names
  /// each run.
  /// </summary>
  /// <remarks>
  /// <para>Only the indirect items are reported. A DIRECT item holds the file's
  /// tail inside a tree leaf, which is the volume's own bookkeeping — it cannot
  /// be moved without moving the leaf, and it is not a run of its own.</para>
  ///
  /// <para>The block bitmap says which blocks are taken and nothing about by
  /// whom. Reporting a layout without that leaves anything trying to move a
  /// file with nothing to repoint, so the runs are read from the pointer arrays
  /// that name them.</para>
  /// </remarks>
  public IEnumerable<(long Offset, long Length, long PointerOffset)> EnumerateDataExtents(ReiserFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) yield break;

    var bodyParts = new List<BodyPart>();
    var sdSize = -1L;
    CollectFileItems(_rootBlock, entry.ObjectId, entry.DirId, bodyParts, ref sdSize);
    if (bodyParts.Count == 0 || sdSize == 0) yield break;
    bodyParts.Sort(static (a, b) => a.KeyOffset.CompareTo(b.KeyOffset));

    foreach (var part in bodyParts) {
      if (!part.Indirect) continue;

      var ptrCount = part.Length / 4;
      var runFirstBlock = 0L;
      var runPointer = -1L;
      var runBlocks = 0;

      for (var p = 0; p < ptrCount; ++p) {
        var pointerOffset = part.Offset + p * 4;
        var block = (long)U32(pointerOffset);
        var usable = block != 0 && block * _blockSize >= 0
                  && block * _blockSize + _blockSize <= _len;

        // A run continues only while the blocks stay consecutive and so do the
        // pointers naming them: a move rewrites the pointers in order, so a gap
        // in either would put the wrong blocks under the wrong pointers.
        if (usable && runPointer >= 0 && block == runFirstBlock + runBlocks) {
          ++runBlocks;
          continue;
        }

        if (runPointer >= 0)
          yield return (runFirstBlock * _blockSize, (long)runBlocks * _blockSize, runPointer);

        if (usable) {
          runFirstBlock = block;
          runPointer = pointerOffset;
          runBlocks = 1;
        } else {
          runPointer = -1;
          runBlocks = 0;
        }
      }

      if (runPointer >= 0)
        yield return (runFirstBlock * _blockSize, (long)runBlocks * _blockSize, runPointer);
    }
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>One body item: where it lives in the image, and whether it is a pointer array.</summary>
  private readonly record struct BodyPart(ulong KeyOffset, long Offset, int Length, bool Indirect);

  /// <summary>
  /// Walks every leaf below <paramref name="blockNum"/> and collects every
  /// item belonging to the (<paramref name="dirId"/>, <paramref name="objectId"/>)
  /// file: DIRECT bodies are added verbatim, INDIRECT pointer arrays are
  /// resolved to concatenated 4 KB data blocks, and the trailing StatData
  /// <c>sd_size</c> is recorded in <paramref name="sdSize"/> so the caller can
  /// truncate the assembled body. Pure read; no allocation outside the parts
  /// list.
  /// </summary>
  private void CollectFileItems(
    int blockNum, uint objectId, uint dirId,
    List<BodyPart> bodyParts, ref long sdSize) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _len) return;
    var boff = (int)blockOff;

    var level = U16((boff));
    var nrItems = U16((boff + 2));

    if (level > 1) {
      var keysOff = boff + 24;
      var ptrsOff = keysOff + nrItems * 16;
      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _len) break;
        var childBlock = (int)U32((ptrOff));
        if (childBlock > 0 && childBlock < _len / _blockSize)
          CollectFileItems(childBlock, objectId, dirId, bodyParts, ref sdSize);
      }
      return;
    }

    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _len) break;

      var keyDirId = U32((ihOff + 0));
      var keyObjId = U32((ihOff + 4));
      // dirId may be 0 (older callers use objectId only); when non-zero,
      // restrict the match to the exact (dir_id, objectid) pair.
      if (keyObjId != objectId) continue;
      if (dirId != 0 && keyDirId != dirId) continue;

      var keyOffsetV2 = U64((ihOff + 8));
      var ihLength = U16((ihOff + 18));
      var ihLocation = U16((ihOff + 20));
      var dataOff = boff + ihLocation;
      if (dataOff < 0 || dataOff + ihLength > _len || ihLength <= 0) continue;

      var itemType = ResolveItemType(ihOff);
      if (itemType == 0) {
        // STAT_DATA — pick up sd_size (le64 @ body +8). If multiple SD items
        // exist (shouldn't, per spec) the last one wins; benign.
        if (ihLength >= 16)
          sdSize = (long)U64((dataOff + 8));
        continue;
      }

      if (itemType == 2) {
        // DIRECT — body is the inline bytes.
        bodyParts.Add(new BodyPart(keyOffsetV2 & 0x0FFFFFFFFFFFFFFFUL, dataOff, ihLength, Indirect: false));
        continue;
      }

      if (itemType == 1) {
        // INDIRECT — body is an array of __le32 block pointers. Each pointer
        // references one full filesystem block of file payload, which is read
        // when the body is written out rather than assembled here.
        bodyParts.Add(new BodyPart(keyOffsetV2 & 0x0FFFFFFFFFFFFFFFUL, dataOff, ihLength, Indirect: true));
        continue;
      }
      // itemType == 3 (DIRENTRY) or -1 (unknown) — skip; not file body.
    }
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._img.Dispose();
}
