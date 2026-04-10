#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ReiserFs;

public sealed class ReiserFsReader : IDisposable {
  private const int SuperblockOffset = 65536;
  private static readonly byte[][] Magics = [
    "ReIsErFs"u8.ToArray(),
    "ReIsEr2Fs"u8.ToArray(),
    "ReIsEr3Fs"u8.ToArray(),
  ];

  private readonly byte[] _data;
  private readonly List<ReiserFsEntry> _entries = [];
  private int _blockSize;
  private int _rootBlock;

  public IReadOnlyList<ReiserFsEntry> Entries => _entries;

  public ReiserFsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 70)
      throw new InvalidDataException("ReiserFS: image too small.");

    // Check magic at offset 52 within superblock
    var magicSpan = _data.AsSpan(SuperblockOffset + 52, 12);
    bool found = false;
    foreach (var m in Magics) {
      if (magicSpan[..m.Length].SequenceEqual(m)) { found = true; break; }
    }
    if (!found)
      throw new InvalidDataException("ReiserFS: invalid magic.");

    _blockSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(SuperblockOffset + 44));
    if (_blockSize == 0) _blockSize = 4096;
    _rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 20));

    // Traverse S+tree from root to find directory entries
    ReadTree(_rootBlock, "");
  }

  private void ReadTree(int blockNum, string basePath) {
    var blockOff = (long)blockNum * _blockSize;
    if (blockOff < 0 || blockOff + 24 > _data.Length) return;
    var boff = (int)blockOff;

    var level = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff));
    var nrItems = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(boff + 2));

    if (level > 1) {
      // Internal node: keys and pointers
      // Keys start at offset 24, each 16 bytes
      // Pointers: (nrItems+1) pointers after keys, each 8 bytes (block_nr uint32 + size uint16 + reserved uint16)
      var keysOff = boff + 24;
      var ptrsOff = keysOff + nrItems * 16;

      for (int i = 0; i <= nrItems && i < 1000; i++) {
        var ptrOff = ptrsOff + i * 8;
        if (ptrOff + 4 > _data.Length) break;
        var childBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ptrOff));
        if (childBlock > 0 && childBlock < _data.Length / _blockSize)
          ReadTree(childBlock, basePath);
      }
    } else {
      // Leaf node: item headers followed by item data
      // Item headers start at offset 24, each is 24 bytes:
      // key(16) + count(2) + length(2) + location(2) + version(2)

      for (int i = 0; i < nrItems && i < 1000; i++) {
        var ihOff = boff + 24 + i * 24;
        if (ihOff + 24 > _data.Length) break;

        var dirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff));
        var objId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 4));
        var offsetVal = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ihOff + 8));

        var ihCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 16));
        var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
        var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));
        var ihVersion = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 22));

        var dataOff = boff + ihLocation;
        if (dataOff < 0 || dataOff + ihLength > _data.Length) continue;

        // Try to parse as directory entries (deh_t array)
        // deh_t: offset(uint32 LE) + dir_id(uint32 LE) + objectid(uint32 LE) + location(uint16 LE) + state(uint16 LE) = 16 bytes
        if (ihCount > 0 && ihCount < 200 && ihLength >= ihCount * 16) {
          for (int e = 0; e < ihCount; e++) {
            var dehOff = dataOff + e * 16;
            if (dehOff + 16 > _data.Length) break;

            var childDirId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 4));
            var childObjId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dehOff + 8));
            var nameLoc = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 12));
            var state = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dehOff + 14));

            if ((state & 4) == 0) continue; // not visible

            // Name is at dataOff + nameLoc
            var nameOff = dataOff + nameLoc;
            if (nameOff < dataOff || nameOff >= dataOff + ihLength) continue;

            // Find name end
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
              DirId = childDirId,
              ObjectId = childObjId,
            });
          }
        }
      }
    }
  }

  public byte[] Extract(ReiserFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    // Search for direct/indirect items with matching object ID
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
    } else {
      // Look for direct items with this objectId
      for (int i = 0; i < nrItems && i < 1000; i++) {
        var ihOff = boff + 24 + i * 24;
        if (ihOff + 24 > _data.Length) break;

        var objId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ihOff + 4));
        if (objId != objectId) continue;

        var ihLength = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 18));
        var ihLocation = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 20));
        var ihCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ihOff + 16));

        // Direct item (not a directory — ihCount should be 0 or 0xFFFF for direct/indirect)
        if (ihCount > 0 && ihCount < 200) continue; // skip directory items

        var dataOff = boff + ihLocation;
        if (dataOff >= 0 && dataOff + ihLength <= _data.Length && ihLength > 0) {
          return _data.AsSpan(dataOff, ihLength).ToArray();
        }
      }
    }
    return [];
  }

  public void Dispose() { }
}
