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

  private static readonly byte[][] Magics = [
    "ReIsErFs"u8.ToArray(),   // 3.5
    "ReIsEr2Fs"u8.ToArray(),  // 3.6
    "ReIsEr3Fs"u8.ToArray(),  // 3.6 w/ non-standard journal
  ];

  private readonly byte[] _data;
  private readonly List<ReiserFsEntry> _entries = [];
  private int _blockSize;
  private int _rootBlock;

  public IReadOnlyList<ReiserFsEntry> Entries => _entries;

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
    _rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + Off_RootBlock));

    ReadTree(_rootBlock, "");
  }

  private void ReadTree(int blockNum, string basePath) {
    // block_head = 24 bytes: blk_level(2) + blk_nr_item(2) + blk_free_space(2)
    //   + blk_reserved(2) + blk_right_delim_key(16)
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _data.Length) return;
    var boff = (int)blockOff;

    var level = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff));
    var nrItems = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff + 2));

    if (level > 1) {
      // Internal node: (nrItems+1) block-number pointers after keys.
      // Keys start at offset 24, 16 bytes each; pointers each 8 bytes.
      var keysOff = boff + 24;
      var ptrsOff = keysOff + nrItems * 16;

      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _data.Length) break;
        var childBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ptrOff));
        if (childBlock > 0 && childBlock < _data.Length / _blockSize)
          ReadTree(childBlock, basePath);
      }
      return;
    }

    // Leaf node
    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _data.Length) break;

      var ihCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 16));
      var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
      var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));

      var dataOff = boff + ihLocation;
      if (dataOff < 0 || dataOff + ihLength > _data.Length) continue;

      // Resolve item type from the key (see FindFileData for the algorithm).
      // Treat TYPE_DIRENTRY (3) as a directory item.
      var keyOffsetV2 = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ihOff + 8));
      var typeV2 = (uint)(keyOffsetV2 >> 60);
      int itemType;
      if (typeV2 == 0 || typeV2 == 15) {
        var uniqueness = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 12));
        itemType = uniqueness switch {
          0u => 0, 0xfffffffeu => 1, 0xffffffffu => 2, 500u => 3, _ => -1,
        };
      } else {
        itemType = (int)typeV2;
      }

      if (itemType == 3 && ihCount > 0 && ihCount < 0x4000 && ihLength >= ihCount * 16) {
        // Per kernel layout, names are packed at the END of the item and grow
        // backward: entry[0]'s name sits at the highest offset (item_end - len[0]);
        // entry[i]'s name ends at entry[i-1]'s location (strictly decreasing).
        // Read each name from deh_location to deh_location of previous entry
        // (or to end-of-item for the first entry). Don't rely on null terminators
        // — stock mkreiserfs does not write any.
        for (int e = 0; e < ihCount; e++) {
          var dehOff = dataOff + e * 16;
          if (dehOff + 16 > _data.Length) break;

          var childDirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 4));
          var childObjId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 8));
          var nameLoc = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 12));
          var state = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 14));

          if ((state & 4) == 0) continue; // not visible
          var nameOff = dataOff + nameLoc;
          if (nameOff < dataOff || nameOff >= dataOff + ihLength) continue;

          // Determine name end: the item_end for the first entry, or the previous
          // entry's deh_location (names are in REVERSE order inside item).
          int nameEndInItem;
          if (e == 0) {
            nameEndInItem = ihLength;
          } else {
            var prevLoc = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dataOff + (e - 1) * 16 + 12));
            nameEndInItem = prevLoc;
          }
          var nameEnd = dataOff + nameEndInItem;
          // Also stop at null for backward compatibility with readers that padded with \0.
          for (var k = nameOff; k < nameEnd && k < _data.Length; k++) {
            if (_data[k] == 0) { nameEnd = k; break; }
          }
          if (nameEnd <= nameOff) continue;

          var name = Encoding.UTF8.GetString(_data, nameOff, nameEnd - nameOff);
          if (name == "." || name == "..") continue;
          if (!name.All(c => c >= 0x20 && c < 0x7F)) continue;

          var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
          _entries.Add(new ReiserFsEntry {
            Name = fullPath,
            Size = ihLength, // approximation; overwritten during Extract from direct-item length
            DirId = childDirId,
            ObjectId = childObjId,
          });
        }
      }
    }
  }

  public byte[] Extract(ReiserFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    return FindFileData(_rootBlock, entry.ObjectId);
  }

  private byte[] FindFileData(int blockNum, uint objectId) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _data.Length) return [];
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
        if (childBlock > 0 && childBlock < _data.Length / _blockSize) {
          var result = FindFileData(childBlock, objectId);
          if (result.Length > 0) return result;
        }
      }
      return [];
    }

    for (int i = 0; i < nrItems && i < 1000; i++) {
      var ihOff = boff + 24 + i * 24;
      if (ihOff + 24 > _data.Length) break;

      var objId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 4));
      if (objId != objectId) continue;

      var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
      var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));

      // Determine the item's TYPE from the key. ReiserFS encodes the type
      // either in offset_v1.k_uniqueness (KEY_FORMAT_1, when bits 60-63 of
      // the offset_v2 union are 0 or 15) or directly in bits 60-63 of
      // offset_v2 (KEY_FORMAT_2). We accept only TYPE_DIRECT (=2) here.
      // TYPE_STAT_DATA (=0) and TYPE_DIRENTRY (=3) and TYPE_INDIRECT (=1)
      // must be skipped — otherwise we'd hand back the SD's 44 bytes.
      var keyOffsetV2 = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ihOff + 8));
      var typeV2 = (uint)(keyOffsetV2 >> 60);
      int itemType;
      if (typeV2 == 0 || typeV2 == 15) {
        // KEY_FORMAT_1 — type encoded in uniqueness field.
        var uniqueness = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 12));
        itemType = uniqueness switch {
          0u => 0,           // V1_SD_UNIQUENESS = TYPE_STAT_DATA
          0xfffffffeu => 1,  // V1_INDIRECT_UNIQUENESS
          0xffffffffu => 2,  // V1_DIRECT_UNIQUENESS
          500u => 3,         // V1_DIRENTRY_UNIQUENESS
          _ => -1,
        };
      } else {
        // KEY_FORMAT_2 — type is bits 60-63.
        itemType = (int)typeV2;
      }
      if (itemType != 2) continue; // not TYPE_DIRECT

      var dataOff = boff + ihLocation;
      if (dataOff >= 0 && dataOff + ihLength <= _data.Length && ihLength > 0)
        return _data.AsSpan(dataOff, ihLength).ToArray();
    }
    return [];
  }

  public void Dispose() { }
}
