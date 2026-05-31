#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs;

/// <summary>
/// Reads IBM JFS1 aggregate images produced by <see cref="JfsWriter"/> or by
/// real <c>mkfs.jfs</c>. Decodes the superblock, FILESYSTEM_I aggregate inode
/// (#16), fileset inode table, and the inline dtree root directory (UCS-2 names).
/// </summary>
public sealed class JfsReader : IDisposable {
  private const uint JfsMagic = 0x3153464A; // "JFS1"
  private const int SuperblockOffset = 0x8000;
  private const int InodeSize = 512;
  private const int FilesetIno = 16;
  private const int RootIno = 2;
  private const int XtreeDataOffset = 224;
  private const int DiDataSize = 288;

  private readonly byte[] _data;
  private readonly List<JfsEntry> _entries = [];
  private int _blockSize;
  private long _filesetInodeTableOffset;

  public IReadOnlyList<JfsEntry> Entries => _entries;

  public JfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 200)
      throw new InvalidDataException("JFS: image too small.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset));
    if (magic != JfsMagic)
      throw new InvalidDataException("JFS: invalid superblock magic.");

    // s_bsize is at superblock offset 16 (le32).
    _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 16));
    if (_blockSize <= 0 || _blockSize > 64 * 1024) _blockSize = 4096;

    // Kernel jfs_filsys.h fixed physical block address: AITBL_OFF = 0xB000 (block 11 @ 4 KB).
    // s_ait2 is SECONDARY (recovery); the primary AIT lives at this fixed byte offset.
    // Fall back to the secondary pxd if the primary bytes look empty (e.g. test images
    // written by older versions of this library that mis-used s_ait2 as primary).
    ulong aitAddr = 0xB000UL / (ulong)_blockSize;
    // Safety: if this fixed location is outside the image, try the secondary pxd as a fallback.
    if ((long)aitAddr * _blockSize >= _data.Length) {
      aitAddr = ReadPxdAddress(_data.AsSpan(SuperblockOffset + 48));
      if (aitAddr == 0 || (long)aitAddr * _blockSize >= _data.Length)
        aitAddr = 9; // legacy fallback
    }

    var aitByteOff = (long)aitAddr * _blockSize;
    // FILESYSTEM_I = inode 16 of the aggregate inode table.
    var fsinoOff = aitByteOff + FilesetIno * InodeSize;
    if (fsinoOff + InodeSize > _data.Length)
      throw new InvalidDataException("JFS: aggregate inode table truncated.");

    // FILESYSTEM_I's xtree root at di_data offset 224. First xad_t points to
    // the fileset inode allocation map (AIM = 2 blocks: dinomap page + first IAG).
    // The IAG's inoext[0] (offset 3072 in the IAG page) holds the pxd_t address
    // of the fileset inode table (4 blocks). Walk through:
    //   FILESYSTEM_I.xtree[0] → fileset AIM block
    //   fileset AIM block + 1 (IAG #0) at offset 3072 → inoext[0] pxd → FSIT block
    var xtRootOff = (int)fsinoOff + XtreeDataOffset;
    var filesetAimByteOff = ReadFirstExtentByteOffset(_data.AsSpan(xtRootOff), _blockSize);
    if (filesetAimByteOff <= 0 || filesetAimByteOff + 2L * _blockSize > _data.Length) {
      // Legacy images where FILESYSTEM_I directly addresses the FSIT.
      _filesetInodeTableOffset = filesetAimByteOff;
    } else {
      // Try indirect path (real mkfs.jfs layout): IAG #0 at AIM + 1 block.
      var iagOff = filesetAimByteOff + _blockSize;
      // inoext[0] at IAG offset 3072.
      var inoextPxd = _data.AsSpan((int)iagOff + 3072, 8);
      var inoextLen = ReadPxdLength(inoextPxd);
      var inoextAddr = ReadPxdAddress(inoextPxd);
      if (inoextLen >= 4 && inoextAddr > 0 && (long)inoextAddr * _blockSize < _data.Length) {
        _filesetInodeTableOffset = (long)inoextAddr * _blockSize;
      } else {
        // Fall back to legacy direct-pointer behaviour.
        _filesetInodeTableOffset = filesetAimByteOff;
      }
    }
    if (_filesetInodeTableOffset <= 0 || _filesetInodeTableOffset >= _data.Length)
      throw new InvalidDataException("JFS: fileset inode table not reachable.");

    ReadDirectory(RootIno, "");
  }

  private long InodeOffset(int ino) => _filesetInodeTableOffset + (long)ino * InodeSize;

  // dtree flag bits (jfs_btree.h).
  private const byte BtRoot = 0x01;
  private const byte BtLeaf = 0x02;
  private const byte BtInternal = 0x04;

  private void ReadDirectory(int ino, string basePath) {
    var inodeOff = InodeOffset(ino);
    if (inodeOff < 0 || inodeOff + InodeSize > _data.Length) return;
    var ioff = (int)inodeOff;

    // di_mode (le32) at 52
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ioff + 52));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    // Directory data: inline dtroot at di_data offset +224. First 32 bytes = header:
    //   DASD(16) + flag(1) + nextindex(1) + freecnt(1) + freelist(1) + idotdot(le32) + stbl[8]
    var dtOff = ioff + XtreeDataOffset;
    if (dtOff + 32 > _data.Length) return;

    var flag = _data[dtOff + 16];
    var nextIndex = _data[dtOff + 17];

    if ((flag & BtInternal) != 0 && (flag & BtLeaf) == 0) {
      // Router root: each stbl idtentry addresses an external dtree page.
      // idtentry: pxd xd(8) + next(s8) + namlen(u8) + name[11]. Follow the
      // pxd to the child page and walk the subtree.
      var stblOff = dtOff + 24;
      for (var i = 0; i < nextIndex && i < 8; i++) {
        var slotIdx = (sbyte)_data[stblOff + i];
        if (slotIdx <= 0 || slotIdx > 8) continue;
        var slotOff = dtOff + slotIdx * 32;
        if (slotOff + 8 > _data.Length) continue;
        var childBlock = (long)ReadPxdAddress(_data.AsSpan(slotOff));
        ReadExternalDtreePage(childBlock, basePath);
      }
      return;
    }

    // Inline leaf dtroot: stbl slots are ldtentry heads directly in di_data.
    var inlineStblOff = dtOff + 24;
    for (var i = 0; i < nextIndex && i < 8; i++) {
      var slotIdx = (sbyte)_data[inlineStblOff + i];
      if (slotIdx <= 0 || slotIdx > 8) continue;
      var slotOff = dtOff + slotIdx * 32;
      if (slotOff + 32 > _data.Length) continue;
      AddLeafEntry(dtOff, slotOff, basePath);
    }
  }

  // Walks one external dtree page (and, for internal pages, the subtree it
  // routes to). Leaf pages add their ldtentry children; internal pages follow
  // each idtentry's pxd to the next page. The stbl is located via the page
  // header's stblindex field.
  private void ReadExternalDtreePage(long pageBlock, string basePath) {
    var pageOff = pageBlock * _blockSize;
    if (pageOff <= 0 || pageOff + _blockSize > _data.Length) return;
    var p = (int)pageOff;

    var flag = _data[p + 16];
    var nextIndex = _data[p + 17];
    var stblIndex = _data[p + 21];
    var stblOff = p + stblIndex * 32;
    var isLeaf = (flag & BtLeaf) != 0;

    var guard = 0;
    for (var i = 0; i < nextIndex && i < 128 && guard < 4096; i++, guard++) {
      var slotIdx = (byte)_data[stblOff + i];
      if (slotIdx == 0 || slotIdx >= 128) continue;
      var slotOff = p + slotIdx * 32;
      if (slotOff + 32 > _data.Length) continue;

      if (isLeaf) {
        AddLeafEntry(p, slotOff, basePath);
      } else {
        // idtentry: pxd xd(8) at slot start → child page block.
        var childBlock = (long)ReadPxdAddress(_data.AsSpan(slotOff));
        if (childBlock > 0) ReadExternalDtreePage(childBlock, basePath);
      }
    }
  }

  // Reads one ldtentry head slot (inline dtroot or external leaf page), adds
  // the entry, and recurses into subdirectories. <paramref name="pageBase"/>
  // is the byte offset of the slot array's owning page/area so name
  // continuation slots resolve as pageBase + index*32.
  private void AddLeafEntry(int pageBase, int slotOff, string basePath) {
    // ldtentry (head): inumber(le32) + next(s8) + namlen(u8) +
    //   name[DTLHDRDATALEN=11] UCS-2 LE + index(le32). Names longer than 11
    //   UCS-2 units spill into continuation dtslots chained by the `next` byte.
    var childIno = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(slotOff));
    if (childIno < 2) return;
    var namLen = _data[slotOff + 5];
    if (namLen == 0) return;

    var name = ReadDtreeName(pageBase, slotOff, namLen);
    if (name.Length == 0 || name == "." || name == "..") return;

    var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
    var childInodeOff = InodeOffset(childIno);
    var isDir = false;
    long childSize = 0;
    DateTime? mtime = null;

    if (childInodeOff >= 0 && childInodeOff + InodeSize <= _data.Length) {
      var cioff = (int)childInodeOff;
      var childMode = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(cioff + 52));
      isDir = (childMode & 0xF000) == 0x4000;
      childSize = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(cioff + 24));
      var ts = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(cioff + 80));  // di_mtime sec
      if (ts != 0) mtime = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
    }

    _entries.Add(new JfsEntry {
      Name = fullPath,
      Size = isDir ? 0 : childSize,
      IsDirectory = isDir,
      InodeNumber = childIno,
      LastModified = mtime,
    });

    if (isDir) ReadDirectory(childIno, fullPath);
  }

  // Reassembles a directory-entry name from its head ldtentry slot and any
  // chained continuation dtslots. <paramref name="dtBase"/> is the dtree-area
  // byte offset (so slot index → byte offset = dtBase + index*32).
  private string ReadDtreeName(int dtBase, int headSlotOff, int namLen) {
    const int DtHeadNameChars = 11;   // DTLHDRDATALEN
    const int DtSlotNameChars = 15;   // DTSLOTDATALEN
    // The slot array spans at most 128 slots: an inline dtroot uses 1..8, an
    // external dtpage uses 1..127. Continuation slots are chained by the head's
    // `next` byte; bound the walk by the maximum slot count.
    var maxSlot = (dtBase + 128 * 32 <= _data.Length) ? 127 : 8;
    var sb = new StringBuilder(namLen);

    var headChars = Math.Min(namLen, DtHeadNameChars);
    if (headSlotOff + 6 + headChars * 2 > _data.Length) return "";
    sb.Append(Encoding.Unicode.GetString(_data.AsSpan(headSlotOff + 6, headChars * 2)));

    var next = (sbyte)_data[headSlotOff + 4];
    var remaining = namLen - headChars;
    var guard = 0;
    while (remaining > 0 && next > 0 && next <= maxSlot && guard++ < 128) {
      var contOff = dtBase + next * 32;
      if (contOff + 32 > _data.Length) break;
      var contChars = Math.Min(remaining, DtSlotNameChars);
      sb.Append(Encoding.Unicode.GetString(_data.AsSpan(contOff + 2, contChars * 2)));
      remaining -= contChars;
      next = (sbyte)_data[contOff];
    }

    return sb.ToString();
  }

  public byte[] Extract(JfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var inodeOff = InodeOffset(entry.InodeNumber);
    if (inodeOff < 0 || inodeOff + InodeSize > _data.Length) return [];
    var ioff = (int)inodeOff;

    var size = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(ioff + 24));
    if (size <= 0) return [];

    // xtree root at di_data offset +256
    var xtOff = ioff + XtreeDataOffset;
    if (xtOff + 32 > _data.Length) return [];

    var xtheader = _data.AsSpan(xtOff);
    var nextIdx = BinaryPrimitives.ReadUInt16LittleEndian(xtheader[18..]);
    var maxEntry = BinaryPrimitives.ReadUInt16LittleEndian(xtheader[20..]);
    const int XtentryStart = 2;

    using var ms = new MemoryStream();
    for (var i = XtentryStart; i < nextIdx && i < maxEntry; i++) {
      var xadOff = xtOff + i * 16;
      if (xadOff + 16 > _data.Length) break;
      var extLen = (int)ReadPxdLength(_data.AsSpan(xadOff + 8));
      var extAddr = (long)ReadPxdAddress(_data.AsSpan(xadOff + 8));
      if (extLen == 0 || extAddr == 0) continue;

      var dataOff = extAddr * _blockSize;
      var remaining = size - ms.Length;
      if (remaining <= 0) break;
      var len = (int)Math.Min((long)extLen * _blockSize, remaining);
      if (dataOff + len <= _data.Length && len > 0)
        ms.Write(_data, (int)dataOff, len);
    }

    var result = ms.ToArray();
    if (result.Length > size)
      return result.AsSpan(0, (int)size).ToArray();
    return result;
  }

  public void Dispose() { }

  // ── pxd_t helpers ─────────────────────────────────────────────────────
  // len_addr (le32): bits 0..23 = length, bits 24..31 = high 8 bits of address
  // addr2    (le32): low 32 bits of address
  internal static uint ReadPxdLength(ReadOnlySpan<byte> pxd) {
    var lenAddr = BinaryPrimitives.ReadUInt32LittleEndian(pxd);
    return lenAddr & 0xFFFFFFu;
  }

  internal static ulong ReadPxdAddress(ReadOnlySpan<byte> pxd) {
    var lenAddr = BinaryPrimitives.ReadUInt32LittleEndian(pxd);
    var addr2 = BinaryPrimitives.ReadUInt32LittleEndian(pxd[4..]);
    var hi = (ulong)(lenAddr >> 24);
    return (hi << 32) | addr2;
  }

  private static long ReadFirstExtentByteOffset(ReadOnlySpan<byte> xtreeRoot, int blockSize) {
    if (xtreeRoot.Length < 48) return 0;
    var nextIdx = BinaryPrimitives.ReadUInt16LittleEndian(xtreeRoot[18..]);
    const int XtentryStart = 2;
    if (nextIdx <= XtentryStart) return 0;
    // First entry at xad slot [2] → byte offset 32
    var xad = xtreeRoot.Slice(XtentryStart * 16, 16);
    var addr = ReadPxdAddress(xad[8..]);
    return (long)addr * blockSize;
  }
}
