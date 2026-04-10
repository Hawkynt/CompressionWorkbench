#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Apfs;

public sealed class ApfsReader : IDisposable {
  private const uint NxMagic = 0x4253584E; // "NXSB"
  private const uint ApsbMagic = 0x42535041; // "APSB"
  private const int ObjHeaderSize = 32;

  private readonly byte[] _data;
  private readonly List<ApfsEntry> _entries = [];
  private uint _blockSize;

  public IReadOnlyList<ApfsEntry> Entries => _entries;

  public ApfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 4096)
      throw new InvalidDataException("APFS: image too small.");

    // NX superblock at block 0
    var nxMagic = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ObjHeaderSize));
    if (nxMagic != NxMagic)
      throw new InvalidDataException("APFS: invalid container superblock magic.");

    _blockSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ObjHeaderSize + 4));
    if (_blockSize == 0) _blockSize = 4096;

    // Find first volume: nx_fs_oid[0] at offset 160 from obj header start
    // Actually APFS NX superblock layout:
    // After magic(4)+block_size(4)+block_count(8)+features(8)+...:
    // nx_omap_oid at offset 88-32=56 from magic, nx_fs_oid[] at much larger offset
    // Simplified: scan blocks for APSB magic
    var volumeBlock = -1;
    for (int b = 0; b < Math.Min(1000, _data.Length / (int)_blockSize); b++) {
      var off = b * (int)_blockSize + ObjHeaderSize;
      if (off + 4 > _data.Length) break;
      var m = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));
      if (m == ApsbMagic) { volumeBlock = b; break; }
    }

    if (volumeBlock < 0) return;

    var vsbOff = volumeBlock * (int)_blockSize;
    // Volume superblock: omap_oid at +40, root_tree_oid at +48 (relative to obj_header start)
    // Actually from volume obj: offset ObjHeaderSize + 8 = omap_oid, +16 = root_tree_oid
    // Let's read root_tree_oid
    var rootTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(vsbOff + ObjHeaderSize + 16));

    // Object map: find physical addresses for OIDs
    // Simplified: scan for B-tree nodes containing directory records
    // Instead of full OID resolution, scan all blocks for B-tree nodes with inode/drec data
    ScanForEntries();
  }

  private void ScanForEntries() {
    // Scan all blocks for B-tree leaf nodes containing directory records
    var blockCount = _data.Length / (int)_blockSize;
    var dirs = new Dictionary<ulong, string>(); // oid → name
    var fileEntries = new List<(string name, ulong parentOid, ulong oid, long size, bool isDir)>();

    for (int b = 0; b < blockCount && b < 10000; b++) {
      var off = b * (int)_blockSize;
      if (off + ObjHeaderSize + 60 > _data.Length) break;

      // Check for B-tree node (object type in header)
      var typeAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 24));
      var objType = typeAndFlags & 0xFFFF;

      // Type 2 = B-tree node (OBJECT_TYPE_BTREE_NODE)
      // Type 3 = B-tree
      if (objType != 2 && objType != 3 && objType != 0xB) continue;

      var btnFlags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + ObjHeaderSize));
      var btnLevel = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off + ObjHeaderSize + 2));
      var btnNkeys = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + ObjHeaderSize + 4));

      if (btnLevel != 0) continue; // only leaf nodes
      if (btnNkeys == 0 || btnNkeys > 1000) continue;

      // Fixed-size KV: flags bit 2 (BTNODE_FIXED_KV_SIZE)
      var isFixed = (btnFlags & 4) != 0;
      var tocOff = off + ObjHeaderSize + 24; // TOC starts after btn header (24 bytes)
      var keyAreaOff = tocOff + (int)btnNkeys * 8; // each TOC entry is 8 bytes for variable
      if (isFixed) keyAreaOff = tocOff + (int)btnNkeys * 8;

      // Scan TOC entries and try to find drec (type 9) entries
      for (uint k = 0; k < btnNkeys; k++) {
        var tocEntryOff = tocOff + (int)k * 8;
        if (tocEntryOff + 8 > _data.Length) break;

        int keyOff, valOff;
        if (isFixed) {
          keyOff = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff));
          valOff = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff + 2));
        } else {
          keyOff = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff));
          var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff + 2));
          valOff = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff + 4));
          var valLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tocEntryOff + 6));
        }

        // Key is at keyAreaOff + keyOff
        var absKeyOff = keyAreaOff + keyOff;
        if (absKeyOff + 12 > _data.Length) continue;

        var keyOidAndType = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(absKeyOff));
        var keyType = (int)(keyOidAndType >> 60);
        var keyOid = keyOidAndType & 0x0FFFFFFFFFFFFFFF;

        if (keyType == 9) {
          // Directory record
          // Key: oid_and_type(8) + name_and_hash(4) + name(variable)
          if (absKeyOff + 12 > _data.Length) continue;
          var nameAndHash = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(absKeyOff + 8));
          var nameLen = nameAndHash & 0x3FF;
          if (nameLen == 0 || absKeyOff + 12 + nameLen > _data.Length) continue;

          var name = Encoding.UTF8.GetString(_data, absKeyOff + 12, (int)nameLen);
          name = name.TrimEnd('\0');

          // Value: at end of block minus valOff
          var absValOff = off + (int)_blockSize - valOff;
          if (absValOff + 18 <= _data.Length && absValOff > off) {
            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(absValOff));
            var dateAdded = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(absValOff + 8));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(absValOff + 16));
            var isDir = (flags & 0x0002) != 0 || _data[absValOff + 16] == 4; // DT_DIR

            if (!string.IsNullOrEmpty(name) && name.All(c => c >= 0x20)) {
              fileEntries.Add((name, keyOid, fileId, 0, isDir));
            }
          }
        }
      }
    }

    // Add unique entries
    var seen = new HashSet<string>();
    foreach (var (name, parentOid, oid, size, isDir) in fileEntries) {
      if (seen.Add(name)) {
        _entries.Add(new ApfsEntry {
          Name = name,
          Size = size,
          IsDirectory = isDir,
          ObjectId = oid,
        });
      }
    }
  }

  public byte[] Extract(ApfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    // APFS file extraction requires full extent resolution which is very complex
    // Return empty for now - format is read-only listing
    return [];
  }

  public void Dispose() { }
}
