#pragma warning disable CS1591
using System.Buffers.Binary;
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

  private readonly byte[] _data;
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

  public IReadOnlyList<ReiserFsEntry> Entries => _entries;

  /// <summary>Volume label from the superblock <c>s_label</c> field (16 bytes, NUL-trimmed ASCII).</summary>
  public string Label { get; private set; } = "";

  public ReiserFsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 128)
      throw new InvalidDataException("ReiserFS: image too small.");

    var magicSpan = _data.AsSpan(SuperblockOffset + Off_Magic, 10);
    bool found = false;
    foreach (var m in Magics) {
      if (magicSpan[..m.Length].SequenceEqual(m)) { found = true; break; }
    }
    if (!found)
      throw new InvalidDataException("ReiserFS: invalid magic.");

    _blockSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(SuperblockOffset + Off_BlockSize));
    if (_blockSize == 0) _blockSize = 4096;

    var labelSpan = _data.AsSpan(SuperblockOffset + Off_Label, 16);
    var labelLen = labelSpan.IndexOf((byte)0);
    if (labelLen < 0) labelLen = 16;
    this.Label = labelLen == 0 ? "" : System.Text.Encoding.ASCII.GetString(labelSpan[..labelLen]);
    _rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + Off_RootBlock));

    // Pass 1: scan every leaf, indexing stat-data modes and directory entries.
    ScanTree(_rootBlock);
    // Pass 2: walk the directory graph from the root, materialising full paths.
    var visited = new HashSet<(uint, uint)>();
    WalkDirectory(RootParentObjectId, RootObjectId, "", visited);
  }

  private void ScanTree(int blockNum) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _data.Length) return;
    var boff = (int)blockOff;

    var level = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff));
    var nrItems = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff + 2));

    if (level > 1) {
      // Internal node: (nrItems+1) block-number pointers after the keys.
      var ptrsOff = boff + 24 + nrItems * 16;
      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _data.Length) break;
        var childBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ptrOff));
        if (childBlock > 0 && childBlock < _data.Length / _blockSize)
          ScanTree(childBlock);
      }
      return;
    }

    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _data.Length) break;

      var keyDirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 0));
      var keyObjId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 4));
      var ihCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 16));
      var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
      var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));

      var dataOff = boff + ihLocation;
      if (dataOff < 0 || dataOff + ihLength > _data.Length) continue;

      var itemType = ResolveItemType(ihOff);

      if (itemType == 0) {
        // STAT_DATA — record sd_mode (le16 at body +0) for dir/file detection.
        if (ihLength >= 2)
          _statMode[(keyDirId, keyObjId)] = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dataOff));
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
      if (dehOff + 16 > _data.Length) break;

      var pointedDirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 4));
      var pointedObjId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 8));
      var nameLoc = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 12));
      var state = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 14));

      if ((state & 4) == 0) continue; // not visible
      var nameOff = dataOff + nameLoc;
      if (nameOff < dataOff || nameOff >= dataOff + ihLength) continue;

      int nameEndInItem;
      if (e == 0) {
        nameEndInItem = ihLength;
      } else {
        var prevLoc = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dataOff + (e - 1) * 16 + 12));
        nameEndInItem = prevLoc;
      }
      var nameEnd = dataOff + nameEndInItem;
      // Trailing NULs are slot padding (ROUND_UP8); stop at the first one.
      for (var k = nameOff; k < nameEnd && k < _data.Length; k++) {
        if (_data[k] == 0) { nameEnd = k; break; }
      }
      if (nameEnd <= nameOff) continue;

      var name = Encoding.UTF8.GetString(_data, nameOff, nameEnd - nameOff);
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
    var keyOffsetV2 = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ihOff + 8));
    var typeV2 = (uint)(keyOffsetV2 >> 60);
    if (typeV2 == 0 || typeV2 == 15) {
      var uniqueness = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 12));
      return uniqueness switch {
        0u => 0, 0xfffffffeu => 1, 0xffffffffu => 2, 500u => 3, _ => -1,
      };
    }
    return (int)typeV2;
  }

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
    var bodyParts = new List<(ulong KeyOffset, byte[] Bytes)>();
    var sdSize = -1L;
    CollectFileItems(_rootBlock, entry.ObjectId, entry.DirId, bodyParts, ref sdSize);
    if (bodyParts.Count == 0 && sdSize <= 0) return [];

    bodyParts.Sort(static (a, b) => a.KeyOffset.CompareTo(b.KeyOffset));
    var totalLen = 0;
    foreach (var part in bodyParts) totalLen += part.Bytes.Length;
    var assembled = new byte[totalLen];
    var pos = 0;
    foreach (var part in bodyParts) {
      Buffer.BlockCopy(part.Bytes, 0, assembled, pos, part.Bytes.Length);
      pos += part.Bytes.Length;
    }
    // Truncate to the StatData-declared size (drops last-block zero padding).
    if (sdSize >= 0 && sdSize < assembled.Length)
      Array.Resize(ref assembled, (int)sdSize);
    return assembled;
  }

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
    List<(ulong KeyOffset, byte[] Bytes)> bodyParts, ref long sdSize) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _data.Length) return;
    var boff = (int)blockOff;

    var level = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff));
    var nrItems = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff + 2));

    if (level > 1) {
      var keysOff = boff + 24;
      var ptrsOff = keysOff + nrItems * 16;
      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _data.Length) break;
        var childBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ptrOff));
        if (childBlock > 0 && childBlock < _data.Length / _blockSize)
          CollectFileItems(childBlock, objectId, dirId, bodyParts, ref sdSize);
      }
      return;
    }

    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _data.Length) break;

      var keyDirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 0));
      var keyObjId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 4));
      // dirId may be 0 (older callers use objectId only); when non-zero,
      // restrict the match to the exact (dir_id, objectid) pair.
      if (keyObjId != objectId) continue;
      if (dirId != 0 && keyDirId != dirId) continue;

      var keyOffsetV2 = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ihOff + 8));
      var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
      var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));
      var dataOff = boff + ihLocation;
      if (dataOff < 0 || dataOff + ihLength > _data.Length || ihLength <= 0) continue;

      var itemType = ResolveItemType(ihOff);
      if (itemType == 0) {
        // STAT_DATA — pick up sd_size (le64 @ body +8). If multiple SD items
        // exist (shouldn't, per spec) the last one wins; benign.
        if (ihLength >= 16)
          sdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(dataOff + 8));
        continue;
      }

      if (itemType == 2) {
        // DIRECT — body is the inline bytes.
        var directBytes = _data.AsSpan(dataOff, ihLength).ToArray();
        bodyParts.Add((keyOffsetV2 & 0x0FFFFFFFFFFFFFFFUL, directBytes));
        continue;
      }

      if (itemType == 1) {
        // INDIRECT — body is an array of __le32 block pointers. Each pointer
        // references one full filesystem block of file payload.
        var ptrCount = ihLength / 4;
        var assembled = new byte[ptrCount * _blockSize];
        for (var p = 0; p < ptrCount; p++) {
          var ptr = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dataOff + p * 4));
          if (ptr == 0) continue; // hole — leave as zeros
          var src = (long)ptr * _blockSize;
          if (src < 0 || src + _blockSize > _data.Length) continue;
          Buffer.BlockCopy(_data, (int)src, assembled, p * _blockSize, _blockSize);
        }
        bodyParts.Add((keyOffsetV2 & 0x0FFFFFFFFFFFFFFFUL, assembled));
        continue;
      }
      // itemType == 3 (DIRENTRY) or -1 (unknown) — skip; not file body.
    }
  }

  public void Dispose() { }
}
