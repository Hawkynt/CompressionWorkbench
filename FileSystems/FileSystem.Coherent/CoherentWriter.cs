#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Coherent;

/// <summary>
/// Builds minimal Mark Williams Coherent OS filesystem images compatible with
/// <see cref="CoherentReader"/>. WORM emission: produces a fresh image; existing
/// content is overwritten.
///
/// Layout (BlockSize = 512, matches the reader's hard-coded assumptions):
/// <code>
///   block 0        boot block (zeros)
///   block 1        padding (zeros)
///   block 2..      inode list — 8 inodes per block, root = inode 2.
///                  The Coherent superblock structure overlaps the start of
///                  the inode list area: the magic 0xFD18 lives at file
///                  offset 1528 (= 1024 + 504), which falls into the same
///                  512-byte block as inode 1. Inode 1 is reserved on V7-
///                  derived UNIX layouts so the overlap is benign — we never
///                  emit a real inode at index 1.
///   block 2+isize  data zones (directories then files)
/// </code>
///
/// The writer fills in V7-flavoured superblock fields (s_isize, s_fsize,
/// s_nfree/s_free free-block cache, s_ninode/s_inode free-inode cache,
/// s_time, magic 0xFD18) so an external Coherent-aware reader can mount the
/// image (the in-tree reader only checks the magic).
///
/// Files use direct zone pointers (up to 10 per inode, 5120 bytes with
/// 512-byte blocks). Larger files use one single-indirect zone (extra
/// 512/3 ≈ 170 zones = 87,040 bytes). Larger still falls back to the
/// double-indirect zone slot for up to ~14.5 MB per file. The directory
/// hierarchy is flat: every input is added under the root inode using its
/// leaf filename (Coherent dir entries are 16 bytes total, 14 bytes for the
/// name, so longer names are truncated).
/// </summary>
public sealed class CoherentWriter : IDisposable {

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];

  internal const int BlockSize = 512;
  internal const int InodeSize = 64;
  internal const int InodesPerBlock = BlockSize / InodeSize; // 8
  internal const int SuperblockOffset = 1024;
  // coh_super_block volume-name string offsets (the Linux sysv detect_coherent
  // match fields); the structure carries no numeric magic.
  internal const int CohFnameOffset = 0x1E4;
  internal const int CohFpackOffset = 0x1EA;
  internal const int RootInode = 2;
  internal const int DirEntrySize = 16;
  internal const int MaxNameLen = 14;

  // V7-flavoured mode bits used by Coherent.
  private const ushort ModeDirectory   = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644

  // Per-inode zone slot layout: 10 direct + single/double/triple indirect.
  private const int DirectZones        = 10;
  private const int SingleIndirectSlot = 10;
  private const int DoubleIndirectSlot = 11;
  private const int PointersPerBlock   = BlockSize / 3; // 170 (24-bit pointers)

  public CoherentWriter(Stream output, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) return;
    this._files.Add((leaf, data));
  }

  /// <summary>
  /// Builds and writes the Coherent filesystem image. Layout is sized
  /// dynamically to the registered files; a 16 KB image holds a handful of
  /// small files and is enough for self-round-trip tests.
  /// </summary>
  public void Finish() {
    // ── 1. Plan the inode list ────────────────────────────────────────────
    // Inode 1 is reserved (overlaps the superblock magic area). Inode 2 is
    // the root directory. Each registered file claims the next sequential
    // inode index.
    var fileInodes = new int[this._files.Count];
    for (var i = 0; i < this._files.Count; i++) fileInodes[i] = 3 + i;

    var maxInodeUsed = this._files.Count == 0 ? RootInode : fileInodes[^1];
    // s_isize is the inode-list size in blocks; we round up so every used
    // inode is on disk. Always reserve at least one full block.
    var isize = Math.Max(1, (maxInodeUsed + InodesPerBlock - 1) / InodesPerBlock);

    // ── 2. Compose the on-disk root directory contents ────────────────────
    // Coherent dirents are 16 bytes: u16 inode + 14-byte name (NUL-padded).
    // Reserved entries "." and ".." for the root.
    var rootEntries = new List<(ushort Inode, string Name)> {
      ((ushort)RootInode, "."),
      ((ushort)RootInode, ".."),
    };
    for (var i = 0; i < this._files.Count; i++)
      rootEntries.Add(((ushort)fileInodes[i], Truncate(this._files[i].Name, MaxNameLen)));

    var rootDirBytes = EncodeDirectory(rootEntries);

    // ── 3. Allocate data zones ─────────────────────────────────────────────
    var dataStart = 2 + isize; // boot (1) + padding (1) + ilist (isize)
    var nextBlock = dataStart;

    // Allocate zones for the root directory.
    var (rootZones, rootSingleIndirectBlock, rootDoubleIndirectBlocks) =
      AllocateFileZones(rootDirBytes.Length, ref nextBlock);

    // Allocate zones for each file.
    var fileZoneRecords = new List<(uint[] Zones, uint SingleIndirect, uint[] DoubleIndirect)>();
    for (var i = 0; i < this._files.Count; i++) {
      var rec = AllocateFileZones(this._files[i].Data.Length, ref nextBlock);
      fileZoneRecords.Add(rec);
    }

    var fsize = (uint)nextBlock;
    if (fsize < dataStart + 1) fsize = (uint)(dataStart + 1);

    // ── 4. Build the in-memory image ──────────────────────────────────────
    // The volume is assembled as one array, so a payload past what an array
    // holds has to be refused here — computing the size and allocating anyway
    // surfaced as an arithmetic overflow that told the caller nothing.
    var imageSize = (long)fsize * BlockSize;
    if (imageSize > System.Array.MaxLength)
      throw new InvalidOperationException(
        $"Coherent: the payload needs {fsize:N0} blocks ({imageSize:N0} bytes), more than the " +
        $"{System.Array.MaxLength:N0} bytes this writer lays out in memory.");
    var image = new byte[imageSize];

    // 4a. Superblock (genuine coh_super_block, 500 bytes). The Linux sysv
    // driver detects Coherent by the s_fname/s_fpack strings ("noname"/"nopack")
    // — it has no numeric magic — and reads it PDP-11 middle-endian. The driver
    // checks the strings in the copy at file offset 512 (block 0 + BLOCK_SIZE/2
    // during 1024-byte detection) but reads s_isize/s_fsize from the copy at
    // file offset 0 (s_bh1 after the 512-byte re-read), so we write an identical
    // superblock at both offsets.
    //   s_isize  (le16  @0x000)  first data zone = first block past the inodes
    //   s_fsize  (pdp32 @0x002)  total number of zones
    //   s_fname  (char  @0x1E4)  "noname"
    //   s_fpack  (char  @0x1EA)  "nopack"
    // Free caches (s_nfree/s_ninode) stay zero — a read-only mount never
    // consults them.
    var firstDataZone = (uint)dataStart;
    WriteCoherentSuperblock(image, 0, (ushort)firstDataZone, fsize);
    WriteCoherentSuperblock(image, BlockSize, (ushort)firstDataZone, fsize);

    // 4b. Inode 2 (root directory).
    WriteInode(image, RootInode, ModeDirectory, (uint)rootDirBytes.Length,
      rootZones, rootSingleIndirectBlock, rootDoubleIndirectBlocks);

    // 4c. File inodes.
    for (var i = 0; i < this._files.Count; i++) {
      var (zones, single, dbl) = fileZoneRecords[i];
      WriteInode(image, fileInodes[i], ModeRegularFile, (uint)this._files[i].Data.Length,
        zones, single, dbl);
    }

    // 4d. Write the root directory zone bytes.
    WriteFileBytes(image, rootDirBytes, rootZones, rootSingleIndirectBlock, rootDoubleIndirectBlocks);

    // 4e. Write file zone bytes.
    for (var i = 0; i < this._files.Count; i++) {
      var (zones, single, dbl) = fileZoneRecords[i];
      WriteFileBytes(image, this._files[i].Data, zones, single, dbl);
    }

    // ── 5. Flush ──────────────────────────────────────────────────────────
    this._output.Write(image, 0, image.Length);
    this._output.Flush();
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static string Truncate(string s, int maxLen)
    => s.Length <= maxLen ? s : s[..maxLen];

  private static byte[] EncodeDirectory(IReadOnlyList<(ushort Inode, string Name)> entries) {
    var bytes = new byte[entries.Count * DirEntrySize];
    for (var i = 0; i < entries.Count; i++) {
      var off = i * DirEntrySize;
      BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off, 2), entries[i].Inode);
      var nameBytes = Encoding.ASCII.GetBytes(entries[i].Name);
      var copyLen = Math.Min(nameBytes.Length, MaxNameLen);
      Array.Copy(nameBytes, 0, bytes, off + 2, copyLen);
    }
    return bytes;
  }

  /// <summary>
  /// Reserves enough data zones to hold <paramref name="byteLength"/> bytes
  /// of payload starting at block <paramref name="nextBlock"/>. The first
  /// up-to-10 zones are stored in the returned direct array; bigger payloads
  /// allocate one single-indirect block (170 extra zones) or, beyond that, a
  /// double-indirect block + per-row single-indirect blocks.
  /// </summary>
  private static (uint[] DirectZones, uint SingleIndirect, uint[] DoubleIndirect)
    AllocateFileZones(int byteLength, ref int nextBlock) {
    var direct = new uint[DirectZones];
    var dataBlocksNeeded = (byteLength + BlockSize - 1) / BlockSize;
    if (dataBlocksNeeded == 0) return (direct, 0, []);

    var blockIdx = 0;
    // 1) Direct zones.
    for (; blockIdx < dataBlocksNeeded && blockIdx < DirectZones; blockIdx++) {
      direct[blockIdx] = (uint)nextBlock++;
    }
    if (blockIdx >= dataBlocksNeeded) return (direct, 0, []);

    // 2) Single-indirect.
    var singleIndirect = (uint)nextBlock++;
    var singleCovers = Math.Min(PointersPerBlock, dataBlocksNeeded - blockIdx);
    for (var j = 0; j < singleCovers; j++) blockIdx++;
    // We don't track the per-pointer block IDs here — WriteFileBytes will
    // re-compute them deterministically by walking the same allocation
    // sequence used in Finish().
    nextBlock += singleCovers; // reserve the actual data blocks behind the indirect
    if (blockIdx >= dataBlocksNeeded) return (direct, singleIndirect, []);

    // 3) Double-indirect. Each pointer in the double-indirect block addresses
    //    a single-indirect block which in turn addresses PointersPerBlock
    //    data blocks. We allocate one double-indirect header block then
    //    enough single-indirect rows to cover the remaining data.
    var remainingAfterSingle = dataBlocksNeeded - blockIdx;
    var rowsNeeded = (remainingAfterSingle + PointersPerBlock - 1) / PointersPerBlock;
    var doubleIndirectRows = new uint[rowsNeeded + 1];
    doubleIndirectRows[0] = (uint)nextBlock++; // double-indirect header
    for (var r = 0; r < rowsNeeded; r++) {
      doubleIndirectRows[r + 1] = (uint)nextBlock++; // each row's single-indirect block
      var rowCovers = Math.Min(PointersPerBlock, remainingAfterSingle);
      remainingAfterSingle -= rowCovers;
      nextBlock += rowCovers; // data blocks behind that row
    }
    return (direct, singleIndirect, doubleIndirectRows);
  }

  /// <summary>Writes <paramref name="payload"/> into <paramref name="image"/>
  /// at the zones recorded for the file, also populating any indirect
  /// pointer blocks.</summary>
  private static void WriteFileBytes(byte[] image, byte[] payload,
      uint[] directZones, uint singleIndirect, uint[] doubleIndirectRows) {
    if (payload.Length == 0) return;
    var remaining = payload.Length;
    var srcOffset = 0;

    // 1) Direct zones.
    for (var i = 0; i < DirectZones && remaining > 0; i++) {
      if (directZones[i] == 0) break;
      var dstOff = (int)(directZones[i] * BlockSize);
      var copy = Math.Min(remaining, BlockSize);
      Array.Copy(payload, srcOffset, image, dstOff, copy);
      srcOffset += copy;
      remaining -= copy;
    }
    if (remaining <= 0) return;

    // 2) Single-indirect: pointers immediately follow the indirect block.
    if (singleIndirect != 0) {
      var indirectOff = (int)(singleIndirect * BlockSize);
      var firstDataBlock = singleIndirect + 1;
      var dataBlocksHere = (remaining + BlockSize - 1) / BlockSize;
      var ptrCount = Math.Min(PointersPerBlock, dataBlocksHere);
      for (var j = 0; j < ptrCount; j++) {
        var dataBlock = firstDataBlock + (uint)j;
        Write24(image.AsSpan(indirectOff + j * 3), dataBlock);
        var dstOff = (int)(dataBlock * BlockSize);
        var copy = Math.Min(remaining, BlockSize);
        Array.Copy(payload, srcOffset, image, dstOff, copy);
        srcOffset += copy;
        remaining -= copy;
        if (remaining <= 0) break;
      }
    }
    if (remaining <= 0) return;

    // 3) Double-indirect rows. doubleIndirectRows[0] is the header block;
    //    [1..] are the per-row single-indirect blocks. Data blocks for row R
    //    follow that row's single-indirect block in sequence.
    if (doubleIndirectRows.Length > 0) {
      var headerOff = (int)(doubleIndirectRows[0] * BlockSize);
      for (var r = 1; r < doubleIndirectRows.Length && remaining > 0; r++) {
        var rowBlock = doubleIndirectRows[r];
        Write24(image.AsSpan(headerOff + (r - 1) * 3), rowBlock);
        var rowOff = (int)(rowBlock * BlockSize);
        var firstDataBlock = rowBlock + 1;
        var rowDataBlocks = (remaining + BlockSize - 1) / BlockSize;
        var ptrCount = Math.Min(PointersPerBlock, rowDataBlocks);
        for (var j = 0; j < ptrCount; j++) {
          var dataBlock = firstDataBlock + (uint)j;
          Write24(image.AsSpan(rowOff + j * 3), dataBlock);
          var dstOff = (int)(dataBlock * BlockSize);
          var copy = Math.Min(remaining, BlockSize);
          Array.Copy(payload, srcOffset, image, dstOff, copy);
          srcOffset += copy;
          remaining -= copy;
          if (remaining <= 0) break;
        }
      }
    }
  }

  /// <summary>Writes an inode record into the inode table.</summary>
  private static void WriteInode(byte[] image, int inum, ushort mode, uint size,
      uint[] directZones, uint singleIndirect, uint[] doubleIndirectRows) {
    // Inode table starts at file offset 2 * BlockSize = 1024.
    var inodeOffset = 2 * BlockSize + (inum - 1) * InodeSize;
    var ino = image.AsSpan(inodeOffset, InodeSize);

    BinaryPrimitives.WriteUInt16LittleEndian(ino[..2], mode);
    BinaryPrimitives.WriteUInt16LittleEndian(ino.Slice(2, 2), 1);     // i_nlink
    BinaryPrimitives.WriteUInt16LittleEndian(ino.Slice(4, 2), 0);     // i_uid
    BinaryPrimitives.WriteUInt16LittleEndian(ino.Slice(6, 2), 0);     // i_gid
    WritePdp32(ino.Slice(8, 4), size);                                // i_size (PDP-32)

    // 13 × 3-byte zone pointers starting at offset 12.
    for (var i = 0; i < DirectZones; i++)
      Write24(ino.Slice(12 + i * 3, 3), directZones[i]);
    Write24(ino.Slice(12 + SingleIndirectSlot * 3, 3), singleIndirect);
    var doublePtr = doubleIndirectRows.Length > 0 ? doubleIndirectRows[0] : 0u;
    Write24(ino.Slice(12 + DoubleIndirectSlot * 3, 3), doublePtr);

    // i_atime / i_mtime / i_ctime stamps are 4 bytes each, optional —
    // leave them as zero (Coherent kernels accept 0 timestamps).
  }

  // 3-byte zone address in PDP-11 byte order, the form the Linux sysv driver's
  // read3byte() reconstructs for a Coherent (BYTESEX_PDP) volume: from disk
  // bytes [d0,d1,d2] it forms the block number d1 | d2<<8 | d0<<16. Inverting:
  // d0 = (B>>16), d1 = B&0xFF, d2 = (B>>8)&0xFF.
  private static void Write24(Span<byte> dest, uint value) {
    dest[0] = (byte)((value >> 16) & 0xFF);
    dest[1] = (byte)(value & 0xFF);
    dest[2] = (byte)((value >> 8) & 0xFF);
  }

  // 32-bit value in PDP-11 middle-endian: the high 16-bit half first, each half
  // little-endian. fs32_to_cpu() for BYTESEX_PDP swaps the two halves back.
  private static void WritePdp32(Span<byte> dest, uint value) {
    dest[0] = (byte)((value >> 16) & 0xFF);
    dest[1] = (byte)((value >> 24) & 0xFF);
    dest[2] = (byte)(value & 0xFF);
    dest[3] = (byte)((value >> 8) & 0xFF);
  }

  // Writes a 500-byte coh_super_block at file offset baseOffset. s_isize is a
  // plain little-endian u16; s_fsize is PDP-32; the s_fname/s_fpack volume
  // strings carry the canonical "noname"/"nopack" the driver matches on.
  private static void WriteCoherentSuperblock(byte[] image, int baseOffset, ushort isize, uint fsize) {
    var sb = image.AsSpan(baseOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[..2], isize);   // s_isize @0x000
    WritePdp32(sb.Slice(0x002, 4), fsize);                      // s_fsize @0x002 (PDP)
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x006, 2), 0); // s_nfree
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x108, 2), 0); // s_ninode
    "noname"u8.CopyTo(sb.Slice(CohFnameOffset, 6));             // s_fname @0x1E4
    "nopack"u8.CopyTo(sb.Slice(CohFpackOffset, 6));             // s_fpack @0x1EA
  }

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}
