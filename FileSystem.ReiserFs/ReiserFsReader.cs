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

      // Directory item heuristic: count is a plausible entry count, not 0xFFFF.
      if (ihCount > 0 && ihCount < 0x4000 && ihLength >= ihCount * 16) {
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

          var nameEnd = nameOff;
          while (nameEnd < dataOff + ihLength && nameEnd < _data.Length && _data[nameEnd] != 0)
            nameEnd++;
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
      var ihCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 16));

      // Directory items: skip. ihCount for direct items is 0xFFFF.
      if (ihCount < 0x4000 && ihCount > 0) continue;

      var dataOff = boff + ihLocation;
      if (dataOff >= 0 && dataOff + ihLength <= _data.Length && ihLength > 0)
        return _data.AsSpan(dataOff, ihLength).ToArray();
    }
    return [];
  }

  public void Dispose() { }
}
