#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// True in-place R/W modifier for OCFS2 (Oracle Cluster Filesystem 2) images
/// produced by <see cref="Ocfs2Writer"/>. Performs <b>O(touched bytes)</b>
/// random-access I/O against the image: only the global bitmap data block, the
/// root directory dinode (inline dirents), the affected file dinode block, and
/// the file's data blocks are read or written. No whole-image read or rewrite.
///
/// <para>Layout (matches <see cref="Ocfs2Writer"/>'s single-node geometry):
/// <list type="bullet">
///   <item>4 KB blocks = 4 KB clusters; one dinode per block.</item>
///   <item>Superblock dinode at block 2; global bitmap dinode at block 3;
///   bitmap data at block 4 (1 bit per cluster, LSB-first, bit=1 means used).</item>
///   <item>Root directory dinode at block 5 (INODE01) with inline dirents in
///   id2 after the 8-byte ocfs2_inline_data header (id2 + 8), each entry
///   <c>inode(8) | rec_len(2) | name_len(1) | file_type(1) | name[]</c>.</item>
///   <item>User files start at block 8: each gets one dinode block, plus
///   contiguous data clusters whose run is held in a single extent record.</item>
/// </list></para>
///
/// <para><b>Scope (MVP, single-node only):</b> root-directory mutations only.
/// Sub-directory mutation, DLM/heartbeat lockdown, multi-node cluster semantics,
/// and root-directory B-tree splits (extent-backed root) are out of scope and
/// throw <see cref="NotSupportedException"/> if encountered.</para>
/// </summary>
public static class Ocfs2InPlaceModifier {

  private const int BlockSize = Ocfs2Writer.BlockSize;
  private const int ClusterSize = Ocfs2Writer.ClusterSize;
  private const int Id2Offset = 0xC0;

  // ocfs2_dinode field offsets (spec-correct, per ocfs2_fs.h).
  private const int OffClusters = 0x14;       // i_clusters (u32)
  private const int OffSize = 0x20;           // i_size (u64)
  private const int OffMode = 0x28;           // i_mode (u16)
  private const int OffLinks = 0x2A;          // i_links_count (u16)
  private const int OffFlags = 0x2C;          // i_flags (u32)
  private const int OffBlkno = 0x50;          // i_blkno (u64)
  private const int OffFsGeneration = 0x60;   // i_fs_generation (u32)
  private const int OffDynFeatures = 0x76;    // i_dyn_features (u16)

  // ocfs2_inline_data header (id_count u16 + 6 reserved) and ocfs2_extent_list
  // header (16 bytes) — records / data start after these.
  private const int InlineHeaderLen = 8;
  private const int ListHeaderLen = 0x10;

  // Dinode file type for regular files in directory entries (ocfs2 FT_REG_FILE).
  // Subdir mutation (FT_DIR = 2) is out of MVP scope.
  private const byte FtRegFile = 1;

  // Dinode flags + features used when writing a new file dinode.
  private const uint InodeValid = 0x00000001;
  private const ushort DynInlineData = 0x0001;
  private const uint ModeFile = 0x8000 | 0x1B4; // -rw-r--r--

  // Regular-inode signature ("INODE01"); the superblock dinode uses "OCFSV2".
  private static readonly byte[] InodeSignature = "INODE01"u8.ToArray();

  // ── Public API ────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a regular file to the OCFS2 image's root directory. Performs random-access
  /// I/O only: bitmap data block, root dinode, new file dinode block, and the
  /// new data blocks are written. Throws <see cref="IOException"/> if an entry
  /// with the same name already exists (use <see cref="ReplaceFile"/> or call
  /// <see cref="RemoveFile"/> first).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("Ocfs2InPlaceModifier: subdirectory paths not supported (root-dir only).");

    ValidateImage(image);

    var rootDirBytes = ReadBlock(image, Ocfs2Writer.RootDirBlkno);
    EnsureInlineRootDir(rootDirBytes);

    var (inlineStart, inlineCapacity, _) = GetRootDirInlineWindow(rootDirBytes);
    if (FindDirEntry(rootDirBytes, inlineStart, inlineCapacity, name, out _, out _))
      throw new IOException($"ocfs2: entry '{name}' already exists in root directory.");

    var newEntrySize = ComputeDirEntrySize(name);

    // Allocate clusters for the file dinode + data clusters.
    var dataClusters = data.Length == 0 ? 0 : (data.Length + ClusterSize - 1) / ClusterSize;
    var bitmap = ReadBlock(image, Ocfs2Writer.BitmapDataBlkno);

    var newDinodeBlk = AllocateCluster(bitmap)
      ?? throw new IOException("ocfs2: no free clusters available for new file dinode.");

    long firstDataBlk = 0;
    if (dataClusters > 0) {
      // Allocate a contiguous run so a single extent record can describe it.
      var run = AllocateContiguousClusters(bitmap, dataClusters);
      if (run == null) {
        // Roll back the dinode allocation before throwing.
        ClearBit(bitmap, (int)newDinodeBlk);
        throw new IOException($"ocfs2: no contiguous run of {dataClusters} free clusters for '{name}'.");
      }
      firstDataBlk = run.Value;
    }

    // Grow image if needed so the new dinode + data blocks fit. Existing blocks
    // are unchanged (we use SetLength which zero-extends).
    var requiredLength = (Math.Max(newDinodeBlk, firstDataBlk + Math.Max(dataClusters - 1, 0)) + 1) * BlockSize;
    if (image.Length < requiredLength) image.SetLength(requiredLength);

    // Write the new file dinode block.
    var dinodeBytes = BuildFileDinodeBlock(newDinodeBlk, firstDataBlk, dataClusters, data.Length);
    WriteBlock(image, newDinodeBlk, dinodeBytes);

    // Write the file's data blocks (contiguous).
    if (dataClusters > 0) {
      var written = 0;
      for (var i = 0; i < dataClusters; i++) {
        var blk = new byte[ClusterSize];
        var toCopy = Math.Min(ClusterSize, data.Length - written);
        if (toCopy > 0) Array.Copy(data, written, blk, 0, toCopy);
        WriteBlock(image, firstDataBlk + i, blk);
        written += toCopy;
      }
    }

    // Insert a new dirent into the slack of the inline directory (OCFS2 inline
    // dirs keep the last entry's rec_len stretched to the inline end; we carve
    // the new entry out of that slack). i_size stays the full inline capacity.
    if (!InsertInlineDirEntry(rootDirBytes, inlineStart, inlineCapacity, newEntrySize, newDinodeBlk, name, FtRegFile)) {
      // No slack — roll back the cluster allocations.
      ClearBit(bitmap, (int)newDinodeBlk);
      if (dataClusters > 0)
        for (var i = 0; i < dataClusters; i++) ClearBit(bitmap, (int)(firstDataBlk + i));
      throw new IOException($"ocfs2: root directory inline area is full (cannot add '{name}').");
    }
    WriteBlock(image, Ocfs2Writer.RootDirBlkno, rootDirBytes);

    // Persist the bitmap.
    WriteBlock(image, Ocfs2Writer.BitmapDataBlkno, bitmap);
  }

  /// <summary>
  /// Removes a regular file from the OCFS2 image's root directory. Frees its
  /// dinode block + data clusters via the global bitmap, zeros their contents,
  /// and splices the dirent out of the root directory's inline area. Returns
  /// false if the named entry does not exist in the root directory.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    ValidateImage(image);

    var rootDirBytes = ReadBlock(image, Ocfs2Writer.RootDirBlkno);
    EnsureInlineRootDir(rootDirBytes);

    var (inlineStart, inlineCapacity, _) = GetRootDirInlineWindow(rootDirBytes);
    if (!FindDirEntry(rootDirBytes, inlineStart, inlineCapacity, name, out var entryOffset, out var entryLen))
      return false;

    // Read fields before splicing.
    var inodeBlk = (long)BinaryPrimitives.ReadUInt64LittleEndian(rootDirBytes.AsSpan(entryOffset, 8));
    var fileType = rootDirBytes[entryOffset + 11];
    if (fileType != FtRegFile)
      throw new NotSupportedException(
        $"ocfs2: refusing to remove non-regular-file entry '{name}' (file_type={fileType}).");

    // Walk the file's dinode and free its data extent (single extent record only,
    // matching the writer's emission). Then free the dinode block itself.
    var dinodeBytes = ReadBlock(image, inodeBlk);
    if (!HasInodeSignature(dinodeBytes))
      throw new InvalidDataException(
        $"ocfs2: dirent points at block {inodeBlk} which lacks the INODE01 signature.");

    var bitmap = ReadBlock(image, Ocfs2Writer.BitmapDataBlkno);

    var extOff = Id2Offset;
    var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(extOff + 4, 2));
    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + ListHeaderLen + i * 16;
      var clusters = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(recOff + 4, 2));
      var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinodeBytes.AsSpan(recOff + 8, 8));
      for (var c = 0; c < clusters; c++) {
        ClearBit(bitmap, (int)(blkno + c));
        if (wipeData) WriteBlock(image, blkno + c, new byte[ClusterSize]);
      }
    }

    // Free the dinode block and wipe it.
    ClearBit(bitmap, (int)inodeBlk);
    WriteBlock(image, inodeBlk, new byte[BlockSize]);

    // Remove the dirent: merge its space into the previous entry's rec_len
    // (OCFS2 dirent removal extends the predecessor over the deleted slot).
    // i_size stays the full inline capacity.
    RemoveInlineDirEntry(rootDirBytes, inlineStart, inlineCapacity, entryOffset, entryLen);
    WriteBlock(image, Ocfs2Writer.RootDirBlkno, rootDirBytes);

    WriteBlock(image, Ocfs2Writer.BitmapDataBlkno, bitmap);
    return true;
  }

  /// <summary>
  /// Replaces a file's contents in place when the new data fits inside the
  /// originally allocated cluster run. Returns false if the named file doesn't
  /// exist; throws if the new size requires more clusters than were originally
  /// allocated (callers should fall back to <see cref="RemoveFile"/> + <see cref="AddFile"/>).
  /// </summary>
  public static bool ReplaceFile(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);

    ValidateImage(image);

    var rootDirBytes = ReadBlock(image, Ocfs2Writer.RootDirBlkno);
    EnsureInlineRootDir(rootDirBytes);

    var (inlineStart, inlineCapacity, _) = GetRootDirInlineWindow(rootDirBytes);
    if (!FindDirEntry(rootDirBytes, inlineStart, inlineCapacity, name, out var entryOffset, out _))
      return false;

    var inodeBlk = (long)BinaryPrimitives.ReadUInt64LittleEndian(rootDirBytes.AsSpan(entryOffset, 8));
    var fileType = rootDirBytes[entryOffset + 11];
    if (fileType != FtRegFile)
      throw new NotSupportedException($"ocfs2: '{name}' is not a regular file.");

    var dinodeBytes = ReadBlock(image, inodeBlk);
    if (!HasInodeSignature(dinodeBytes))
      throw new InvalidDataException(
        $"ocfs2: dirent for '{name}' points at block {inodeBlk} which lacks the INODE01 signature.");

    var extOff = Id2Offset;
    var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(extOff + 4, 2));
    if (nextFreeRec == 0 && newData.Length > 0)
      throw new NotSupportedException(
        $"ocfs2: '{name}' has no data extent; in-place grow from zero is not supported.");

    // Sum existing allocation to verify the new payload fits.
    var totalAllocClusters = 0;
    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + ListHeaderLen + i * 16;
      totalAllocClusters += BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(recOff + 4, 2));
    }

    var neededClusters = newData.Length == 0 ? 0 : (newData.Length + ClusterSize - 1) / ClusterSize;
    if (neededClusters > totalAllocClusters)
      throw new IOException(
        $"ocfs2: in-place replace of '{name}' needs {neededClusters} clusters but only {totalAllocClusters} allocated.");

    // Rewrite data clusters across the existing extent run(s). Trailing
    // clusters that the new payload no longer fills are zeroed but stay
    // allocated (no bitmap change).
    var written = 0;
    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + ListHeaderLen + i * 16;
      var clusters = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(recOff + 4, 2));
      var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinodeBytes.AsSpan(recOff + 8, 8));
      for (var c = 0; c < clusters; c++) {
        var blk = new byte[ClusterSize];
        var toCopy = Math.Min(ClusterSize, newData.Length - written);
        if (toCopy > 0) Array.Copy(newData, written, blk, 0, toCopy);
        WriteBlock(image, blkno + c, blk);
        if (toCopy > 0) written += toCopy;
      }
    }

    // Update i_size in the file dinode header (+0x20).
    BinaryPrimitives.WriteUInt64LittleEndian(dinodeBytes.AsSpan(OffSize, 8), (ulong)newData.Length);
    WriteBlock(image, inodeBlk, dinodeBytes);
    return true;
  }

  // ── Image / dinode helpers ────────────────────────────────────────────

  private static void ValidateImage(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Ocfs2InPlaceModifier: stream must be readable, writable, and seekable.", nameof(image));
    if (image.Length < (long)(Ocfs2Writer.FirstFileBlkno) * BlockSize)
      throw new InvalidDataException("ocfs2: image too small to be a writer-emitted OCFS2 volume.");

    var sb = ReadBlock(image, Ocfs2Writer.SuperBlockBlkno);
    if (!HasSuperSignature(sb))
      throw new InvalidDataException("ocfs2: superblock dinode lacks the OCFSV2 signature.");
  }

  /// <summary>True iff the block begins with the OCFSV2 superblock signature.</summary>
  private static bool HasSuperSignature(byte[] block)
    => block.Length >= 6 && block.AsSpan(0, 6).SequenceEqual(Ocfs2Superblock.SignatureBytes);

  /// <summary>True iff the block begins with the INODE01 regular-inode signature.</summary>
  private static bool HasInodeSignature(byte[] block)
    => block.Length >= InodeSignature.Length
       && block.AsSpan(0, InodeSignature.Length).SequenceEqual(InodeSignature);

  private static void EnsureInlineRootDir(byte[] rootDirBytes) {
    if (!HasInodeSignature(rootDirBytes))
      throw new InvalidDataException("ocfs2: root directory dinode lacks the INODE01 signature.");
    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(rootDirBytes.AsSpan(OffDynFeatures, 2));
    if ((dynFeatures & DynInlineData) == 0)
      throw new NotSupportedException(
        "ocfs2: in-place modifier supports inline-data root directories only; "
        + "extent-backed root directories require subdir B-tree handling (deferred).");
  }

  /// <summary>
  /// Returns the inline dirent area inside the root directory dinode:
  /// (start, capacity). OCFS2 inline directories fill the whole inline area —
  /// the dirent chain runs from <c>start</c> for <c>capacity</c> bytes with the
  /// final entry's rec_len stretched to the end, and i_size == capacity. The
  /// third tuple element (currentSize) is retained as the capacity for callers
  /// that walk the full chain.
  /// </summary>
  private static (int Start, int Capacity, int CurrentSize) GetRootDirInlineWindow(byte[] rootDirBytes) {
    var start = Id2Offset + InlineHeaderLen; // skip the 8-byte ocfs2_inline_data header
    var capacity = BlockSize - start;
    return (start, capacity, capacity);
  }

  // ── Directory entry helpers ───────────────────────────────────────────

  private static int ComputeDirEntrySize(string name) {
    var nameBytes = Encoding.UTF8.GetByteCount(name);
    var raw = 8 + 2 + 1 + 1 + nameBytes; // inode + rec_len + name_len + file_type + name
    return (raw + 3) & ~3;
  }

  /// <summary>
  /// Linear-scans the inline dirent area [start, start+currentSize) for a
  /// matching name. Returns the entry's absolute byte offset and aligned length.
  /// Skips "." and ".." (caller asks for user-named entries only).
  /// </summary>
  private static bool FindDirEntry(byte[] block, int start, int capacity, string name,
                                   out int entryOffset, out int entryLen) {
    entryOffset = -1; entryLen = 0;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var cursor = start;
    var end = start + capacity;
    while (cursor + 12 <= end) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
      var nameLen = block[cursor + 10];
      if (recLen < 12 || cursor + recLen > end) return false;
      if (inode != 0 && nameLen == nameBytes.Length
          && block.AsSpan(cursor + 12, nameLen).SequenceEqual(nameBytes)) {
        entryOffset = cursor;
        entryLen = recLen;
        return true;
      }
      cursor += recLen;
    }
    return false;
  }

  /// <summary>
  /// Inserts a new dirent into the inline dir chain by carving it from the slack
  /// of an existing entry (an entry whose rec_len exceeds its natural size).
  /// OCFS2 always keeps the final entry stretched to the inline end, so there is
  /// slack there once the directory is not completely packed. Returns false when
  /// no entry has enough slack for <paramref name="newRecLen"/>.
  /// </summary>
  private static bool InsertInlineDirEntry(byte[] block, int start, int capacity,
                                           int newRecLen, long inodeBlk, string name, byte fileType) {
    var end = start + capacity;
    var cursor = start;
    while (cursor + 12 <= end) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
      var nameLen = block[cursor + 10];
      if (recLen < 12 || cursor + recLen > end) return false;

      var naturalLen = inode == 0 ? 0 : (12 + nameLen + 3) & ~3;
      var slack = recLen - naturalLen;
      if (slack >= newRecLen) {
        // Shrink the current entry to its natural size, place the new entry in
        // the freed slack and stretch it to consume the remaining slack so the
        // chain stays gap-free.
        var newEntryOff = cursor + naturalLen;
        var newEntryRecLen = slack; // absorb all remaining slack into the new (now last-in-run) entry
        if (inode != 0)
          BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(cursor + 8, 2), (ushort)naturalLen);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        Array.Clear(block, newEntryOff, newEntryRecLen);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(newEntryOff, 8), (ulong)inodeBlk);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(newEntryOff + 8, 2), (ushort)newEntryRecLen);
        block[newEntryOff + 10] = (byte)nameBytes.Length;
        block[newEntryOff + 11] = fileType;
        nameBytes.CopyTo(block.AsSpan(newEntryOff + 12, nameBytes.Length));
        return true;
      }
      cursor += recLen;
    }
    return false;
  }

  /// <summary>
  /// Removes a dirent by absorbing its rec_len into the previous entry (so the
  /// chain stays contiguous). If the entry is the first in the area, it is marked
  /// unused (inode = 0) instead. i_size is unchanged (inline dirs span the whole
  /// inline area).
  /// </summary>
  private static void RemoveInlineDirEntry(byte[] block, int start, int capacity,
                                           int entryOffset, int entryLen) {
    var prev = -1;
    var cursor = start;
    while (cursor < entryOffset) {
      prev = cursor;
      cursor += BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
    }
    if (prev >= 0) {
      var prevLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(prev + 8, 2));
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(prev + 8, 2), (ushort)(prevLen + entryLen));
    } else {
      // No predecessor — blank the inode so it reads as an empty slot.
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(entryOffset, 8), 0);
      block[entryOffset + 10] = 0;
    }
    Array.Clear(block, entryOffset + 8 + 2 + 1 + 1, Math.Max(0, entryLen - 12)); // wipe name bytes
  }

  /// <summary>
  /// Appends a new dirent at the end of the inline area. Caller has verified
  /// there is room (newEntrySize ≤ capacity − currentSize).
  /// </summary>
  // ── Bitmap helpers ────────────────────────────────────────────────────
  // The cluster allocation bitmap is the bg_bitmap field of the global_bitmap
  // group descriptor (block 3), which begins BitmapBase bytes into the block.
  // Bit N (= cluster N) therefore lives at byte BitmapBase + N/8.
  private const int BitmapBase = Ocfs2Writer.BitmapInGroupOffset;

  private static bool TestBit(byte[] bitmap, int bit)
    => (bitmap[BitmapBase + bit / 8] & (1 << (bit % 8))) != 0;

  private static void SetBit(byte[] bitmap, int bit)
    => bitmap[BitmapBase + bit / 8] |= (byte)(1 << (bit % 8));

  private static void ClearBit(byte[] bitmap, int bit)
    => bitmap[BitmapBase + bit / 8] &= (byte)~(1 << (bit % 8));

  /// <summary>Allocates the first free cluster (LSB-first scan). bit N = cluster N.</summary>
  private static long? AllocateCluster(byte[] bitmap) {
    var maxBit = (bitmap.Length - BitmapBase) * 8;
    for (var bit = 0; bit < maxBit; bit++) {
      if (TestBit(bitmap, bit)) continue;
      SetBit(bitmap, bit);
      return bit;
    }
    return null;
  }

  /// <summary>
  /// Allocates a contiguous run of <paramref name="count"/> free clusters.
  /// Returns the first cluster number, or null if no such run exists.
  /// </summary>
  private static long? AllocateContiguousClusters(byte[] bitmap, int count) {
    if (count <= 0) return 0;
    var maxBit = (bitmap.Length - BitmapBase) * 8;
    var runStart = -1;
    var runLen = 0;
    for (var bit = 0; bit < maxBit; bit++) {
      if (!TestBit(bitmap, bit)) {
        if (runLen == 0) runStart = bit;
        runLen++;
        if (runLen == count) {
          for (var b = runStart; b < runStart + count; b++) SetBit(bitmap, b);
          return runStart;
        }
      } else {
        runLen = 0;
        runStart = -1;
      }
    }
    return null;
  }

  // ── File dinode emission ──────────────────────────────────────────────

  /// <summary>
  /// Builds a 4 KB file dinode block matching the format <see cref="Ocfs2Writer"/>
  /// emits (spec-correct per ocfs2_fs.h): INODE01 signature at offset 0, then
  /// i_clusters (+0x14), i_size (+0x20), i_mode (+0x28), i_links_count (+0x2A),
  /// i_flags (+0x2C), i_blkno (+0x50), i_fs_generation (+0x60), and a single-extent
  /// <c>ocfs2_extent_list</c> at id2 (+0xC0, records at id2+0x10) pointing at the
  /// contiguous data run. <see cref="Ocfs2Reader"/> traverses these fields, so
  /// writing them in the canonical format is what makes the file surface in
  /// List/Extract.
  /// </summary>
  private static byte[] BuildFileDinodeBlock(long blkno, long dataBlkno, int dataClusters, int fileSize) {
    var block = new byte[BlockSize];

    // i_signature[8] at offset 0 — regular-inode magic.
    InodeSignature.CopyTo(block.AsSpan(0, InodeSignature.Length));
    // i_generation at +8
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x08, 4), (uint)(blkno + 100));
    // i_suballoc_slot at +0x0C (i16): -1
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(0x0C, 2), -1);
    // i_suballoc_bit at +0x0E (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0E, 2), (ushort)blkno);
    // i_clusters at +0x14
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffClusters, 4), (uint)dataClusters);
    // i_size at +0x20
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffSize, 8), (ulong)fileSize);
    // i_mode at +0x28
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffMode, 2), (ushort)ModeFile);
    // i_links_count at +0x2A
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(OffLinks, 2), 1);
    // i_flags at +0x2C
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffFlags, 4), InodeValid);
    // i_blkno at +0x50
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(OffBlkno, 8), (ulong)blkno);
    // i_fs_generation at +0x60
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(OffFsGeneration, 4), (uint)(blkno + 100));

    if (fileSize == 0) return block;

    // ocfs2_extent_list at id2 (records start at id2 + 0x10):
    var off = Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 0, 2), 0); // l_tree_depth
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 2, 2), 1); // l_count
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 4, 2), 1); // l_next_free_rec
    // extent record:
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(off + ListHeaderLen + 0, 4), 0);            // e_cpos
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + ListHeaderLen + 4, 2), (ushort)dataClusters); // e_leaf_clusters
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(off + ListHeaderLen + 8, 8), (ulong)dataBlkno);     // e_blkno
    return block;
  }

  // ── Block IO ──────────────────────────────────────────────────────────

  private static byte[] ReadBlock(Stream image, long blkno) {
    var buf = new byte[BlockSize];
    image.Position = blkno * BlockSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBlock(Stream image, long blkno, ReadOnlySpan<byte> data) {
    if (data.Length != BlockSize)
      throw new ArgumentException("block payload size mismatch", nameof(data));
    image.Position = blkno * BlockSize;
    image.Write(data);
  }
}
