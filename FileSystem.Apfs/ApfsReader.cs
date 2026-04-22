#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Reads Apple File System (APFS) images per Apple's "Apple File System
/// Reference" (public spec). Walks the NXSB → container OMAP → APSB →
/// volume OMAP → filesystem B-tree chain and extracts file data via
/// <c>FILE_EXTENT</c> records.
/// </summary>
public sealed class ApfsReader : IDisposable {
  private const uint NxMagicLE = 0x4253584E; // "NXSB" stored LE
  private const uint ApsbMagicLE = 0x42535041; // "APSB" stored LE

  private readonly byte[] _data;
  private readonly List<ApfsEntry> _entries = [];
  private uint _blockSize = DEFAULT_BLOCK_SIZE;

  public IReadOnlyList<ApfsEntry> Entries => this._entries;

  public ApfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    if (!leaveOpen) stream.Dispose();
    this.Parse();
  }

  private Span<byte> BlockSpan(long blockNum) {
    var off = blockNum * this._blockSize;
    if (off < 0 || off + this._blockSize > this._data.Length)
      throw new InvalidDataException($"APFS: block {blockNum} out of range.");
    return this._data.AsSpan((int)off, (int)this._blockSize);
  }

  private void Parse() {
    if (this._data.Length < 4096)
      throw new InvalidDataException("APFS: image too small.");

    // NX superblock at block 0.
    var nxMagic = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(32));
    if (nxMagic != NxMagicLE)
      throw new InvalidDataException("APFS: invalid container superblock magic.");

    this._blockSize = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(36));
    if (this._blockSize == 0) this._blockSize = DEFAULT_BLOCK_SIZE;

    // Read container OMAP phys address. The spec has `nx_omap_oid` at offset 160
    // (container OMAP ephemeral OID). To enable an on-disk round-trip without a
    // checkpoint-resolver, our writer also stashes the physical block at offset
    // 3072 (reader-specific). We use that when present; otherwise we fall back
    // to scanning for an OMAP object.
    var ctrOmapPhys = this.ResolveCtrOmapPhys();
    if (ctrOmapPhys == 0) {
      // Nothing to parse — image is NXSB-only.
      return;
    }

    // Container OMAP points to its B-tree root. From that tree we find APSB
    // (volume superblock) via the nx_fs_oid[0] entry at NXSB +184.
    var apsbVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(this._data.AsSpan(184));
    if (apsbVirtOid == 0) return;

    var apsbPhys = this.ResolveOidViaOmap(ctrOmapPhys, apsbVirtOid);
    if (apsbPhys == 0) return;

    var apsbBlock = this.BlockSpan((long)apsbPhys);
    if (BinaryPrimitives.ReadUInt32LittleEndian(apsbBlock[32..]) != ApsbMagicLE) return;

    // APSB → volume OMAP phys oid (apfs_omap_oid at +392), root tree virtual OID at +400.
    var volOmapPhys = BinaryPrimitives.ReadUInt64LittleEndian(apsbBlock[392..]);
    var rootTreeVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(apsbBlock[400..]);
    if (volOmapPhys == 0 || rootTreeVirtOid == 0) return;

    var rootTreePhys = this.ResolveOidViaOmap(volOmapPhys, rootTreeVirtOid);
    if (rootTreePhys == 0) return;

    // Walk FS tree leaf(s) and collect inodes / drec / file_extent.
    this.ParseFsTree((long)rootTreePhys);
  }

  // ── OMAP resolution ─────────────────────────────────────────────────────

  private ulong ResolveCtrOmapPhys() {
    // Writer-stamped physical hint at offset 3072 of NXSB (unused spec area).
    var hint = BinaryPrimitives.ReadUInt64LittleEndian(this._data.AsSpan(3072));
    if (hint > 0 && (long)hint * this._blockSize + this._blockSize <= this._data.Length) {
      // Verify it's actually an OMAP object.
      var span = this.BlockSpan((long)hint);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) & OBJECT_TYPE_MASK;
      if (type == OBJECT_TYPE_OMAP) return hint;
    }

    // Fallback: scan for any OMAP-typed block.
    var blockCount = this._data.Length / this._blockSize;
    for (long b = 0; b < blockCount; b++) {
      var span = this._data.AsSpan((int)(b * this._blockSize), (int)this._blockSize);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) & OBJECT_TYPE_MASK;
      if (type == OBJECT_TYPE_OMAP)
        return (ulong)b;
    }
    return 0;
  }

  /// <summary>
  /// Given a physical block address of an OMAP phys object, follow its tree and
  /// resolve a virtual OID to its physical block number. Returns 0 if not found.
  /// </summary>
  private ulong ResolveOidViaOmap(ulong omapPhys, ulong virtOid) {
    var omap = this.BlockSpan((long)omapPhys);
    // om_tree_oid at offset 48 (u64) = physical block number of OMAP B-tree root.
    var treePhys = BinaryPrimitives.ReadUInt64LittleEndian(omap[48..]);
    if (treePhys == 0) return 0;

    var treeBlock = this.BlockSpan((long)treePhys);
    // B-tree leaf node walk — look up (virtOid, *) and return paddr.
    var records = EnumerateBtreeLeafRecords(treeBlock, isRoot: true);
    foreach (var (key, value) in records) {
      if (key.Length < 16) continue;
      var ok = BinaryPrimitives.ReadUInt64LittleEndian(key);
      if (ok != virtOid) continue;
      if (value.Length < 16) continue;
      var paddr = BinaryPrimitives.ReadUInt64LittleEndian(value[8..]);
      return paddr;
    }
    return 0;
  }

  // ── B-tree leaf enumeration ─────────────────────────────────────────────

  /// <summary>
  /// Enumerates (key, value) pairs from a B-tree leaf node. Supports single-level
  /// root-leaf trees (what our writer produces) and plain leaf nodes.
  /// </summary>
  private static IEnumerable<(byte[] Key, byte[] Value)> EnumerateBtreeLeafRecords(ReadOnlySpan<byte> node, bool isRoot) {
    var results = new List<(byte[], byte[])>();

    var type = BinaryPrimitives.ReadUInt32LittleEndian(node[24..]) & OBJECT_TYPE_MASK;
    if (type != OBJECT_TYPE_BTREE && type != OBJECT_TYPE_BTREE_NODE && type != OBJECT_TYPE_OMAP
        && type != OBJECT_TYPE_FSTREE && type != OBJECT_TYPE_BLOCKREFTREE
        && type != OBJECT_TYPE_SNAPMETATREE)
      return results;

    // btn_flags at offset 32.
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);
    var level = BinaryPrimitives.ReadUInt16LittleEndian(node[34..]);
    var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(node[36..]);
    if (level != 0) return results; // internal node not supported for this minimal tree
    if (nkeys == 0) return results;

    // btn_table_space.off at +40; data[] starts after the btn header (at +56).
    var tableOff = BinaryPrimitives.ReadUInt16LittleEndian(node[40..]);
    var tableLen = BinaryPrimitives.ReadUInt16LittleEndian(node[42..]);
    const int btnHeaderEnd = 56;
    var tocAbs = btnHeaderEnd + tableOff;
    var keyAreaStart = tocAbs + tableLen;

    // Value area ends at node.Length (for non-root) or node.Length - 40 (for root, to skip btree_info).
    var valAreaEnd = isRoot || (flags & BTNODE_ROOT) != 0
      ? node.Length - 40
      : node.Length;

    var isFixed = (flags & BTNODE_FIXED_KV_SIZE) != 0;
    for (uint k = 0; k < nkeys; k++) {
      int keyOff, keyLen, valOff, valLen;
      if (isFixed) {
        var e = tocAbs + (int)k * 4;
        if (e + 4 > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        keyLen = 0; valLen = 0; // Fixed sizes come from btree_info; not used by us.
      } else {
        var e = tocAbs + (int)k * 8;
        if (e + 8 > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        keyLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 4)..]);
        valLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 6)..]);
      }
      if (keyLen <= 0 || valLen <= 0) continue;
      var keyAbs = keyAreaStart + keyOff;
      var valAbs = valAreaEnd - valOff;
      if (keyAbs < 0 || keyAbs + keyLen > node.Length) continue;
      if (valAbs < 0 || valAbs + valLen > node.Length) continue;

      var keyBuf = new byte[keyLen];
      node.Slice(keyAbs, keyLen).CopyTo(keyBuf);
      var valBuf = new byte[valLen];
      node.Slice(valAbs, valLen).CopyTo(valBuf);
      results.Add((keyBuf, valBuf));
    }
    return results;
  }

  // ── FS-tree parsing ─────────────────────────────────────────────────────

  private void ParseFsTree(long treePhys) {
    var tree = this.BlockSpan(treePhys);

    // Collect: inode name + size + isDir, drec: parent -> (name, child_ino), file_extent: ino -> (size, paddr).
    var inodeName = new Dictionary<ulong, string>();
    var inodeSize = new Dictionary<ulong, long>();
    var inodeIsDir = new Dictionary<ulong, bool>();
    var drec = new List<(ulong Parent, string Name, ulong ChildIno, bool IsDir)>();
    var fileExtent = new Dictionary<ulong, (long Length, ulong PhysBlock)>();
    var inodeTimestamps = new Dictionary<ulong, DateTime>();

    foreach (var (key, val) in EnumerateBtreeLeafRecords(tree, isRoot: true)) {
      if (key.Length < 8) continue;
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;

      switch (keyType) {
        case APFS_TYPE_INODE:
          if (val.Length < 88) break;
          var mode = BinaryPrimitives.ReadUInt16LittleEndian(val.AsSpan(80));
          var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(84));
          inodeIsDir[oid] = (mode & 0xF000) == S_IFDIR;
          inodeSize[oid] = size;
          var mtimeNs = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(24));
          if (mtimeNs > 0) {
            var ms = (long)(mtimeNs / 1_000_000UL);
            inodeTimestamps[oid] = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
          }
          break;

        case APFS_TYPE_DIR_REC:
          if (key.Length < 12 || val.Length < 18) break;
          var nameLenAndHash = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(8));
          var nameLen = (int)(nameLenAndHash & 0x3FF);
          if (nameLen <= 0 || 12 + nameLen > key.Length) break;
          var name = Encoding.UTF8.GetString(key, 12, nameLen).TrimEnd('\0');
          var childIno = BinaryPrimitives.ReadUInt64LittleEndian(val);
          var flags = BinaryPrimitives.ReadUInt16LittleEndian(val.AsSpan(16));
          var dirType = flags & APFS_DIR_REC_FLAGS_MASK;
          drec.Add((oid, name, childIno, dirType == DT_DIR));
          break;

        case APFS_TYPE_FILE_EXTENT:
          if (val.Length < 16) break;
          var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(val);
          var len = (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL);
          var paddr = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(8));
          fileExtent[oid] = (len, paddr);
          break;
      }
    }

    // Emit entries for each DREC.
    foreach (var (parent, name, childIno, isDir) in drec) {
      if (string.IsNullOrEmpty(name)) continue;
      long sz = inodeSize.GetValueOrDefault(childIno, 0);
      bool dir = isDir || inodeIsDir.GetValueOrDefault(childIno, false);
      DateTime? ts = inodeTimestamps.TryGetValue(childIno, out var t) ? t : null;
      ulong firstBlock = 0;
      long extentLen = 0;
      if (!dir && fileExtent.TryGetValue(childIno, out var fx)) {
        firstBlock = fx.PhysBlock;
        extentLen = fx.Length;
      }
      this._entries.Add(new ApfsEntry {
        Name = name,
        Size = sz,
        IsDirectory = dir,
        ObjectId = childIno,
        LastModified = ts,
        FirstBlock = firstBlock,
        ExtentLength = extentLen,
      });
    }
  }

  /// <summary>
  /// Extracts the raw data of a file entry by resolving its file-extent
  /// record's physical block number.
  /// </summary>
  public byte[] Extract(ApfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];
    if (entry.FirstBlock == 0) return [];
    var offset = (long)entry.FirstBlock * this._blockSize;
    if (offset < 0 || offset + entry.Size > this._data.Length)
      return [];
    var result = new byte[entry.Size];
    Buffer.BlockCopy(this._data, (int)offset, result, 0, (int)entry.Size);
    return result;
  }

  public void Dispose() { }
}
