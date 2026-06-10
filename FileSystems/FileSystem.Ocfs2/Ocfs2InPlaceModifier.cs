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
///   <item>Root directory dinode at block 5 with inline dirents in id2 (+0xC0),
///   each entry <c>inode(8) | rec_len(2) | name_len(1) | file_type(1) | name[]</c>.</item>
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

  // Dinode file type for regular files in directory entries (ocfs2 FT_REG_FILE).
  // Subdir mutation (FT_DIR = 2) is out of MVP scope.
  private const byte FtRegFile = 1;

  // Dinode flags + features used when writing a new file dinode.
  private const uint InodeValid = 0x00000001;
  private const ushort DynInlineData = 0x0001;
  private const uint ModeFile = 0x8000 | 0x1B4; // -rw-r--r--

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

    var (inlineStart, inlineCapacity, currentSize) = GetRootDirInlineWindow(rootDirBytes);
    if (FindDirEntry(rootDirBytes, inlineStart, currentSize, name, out _, out _))
      throw new IOException($"ocfs2: entry '{name}' already exists in root directory.");

    var newEntrySize = ComputeDirEntrySize(name);
    if (currentSize + newEntrySize > inlineCapacity)
      throw new IOException($"ocfs2: root directory inline area is full (cannot add '{name}'); "
                            + "extent-backed root directories are not supported by the in-place modifier.");

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

    // Append a new dirent into the root dir inline area.
    AppendInlineDirEntry(rootDirBytes, inlineStart, currentSize, newDinodeBlk, name, FtRegFile);
    var newDirSize = currentSize + newEntrySize;
    UpdateDinodeSize(rootDirBytes, newDirSize);
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

    var (inlineStart, _, currentSize) = GetRootDirInlineWindow(rootDirBytes);
    if (!FindDirEntry(rootDirBytes, inlineStart, currentSize, name, out var entryOffset, out var entryLen))
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
    if (!HasSignature(dinodeBytes))
      throw new InvalidDataException(
        $"ocfs2: dirent points at block {inodeBlk} which lacks the OCFSV2 signature.");

    var bitmap = ReadBlock(image, Ocfs2Writer.BitmapDataBlkno);

    var extOff = Id2Offset;
    var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(extOff + 4, 2));
    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + 8 + i * 16;
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

    // Splice the dirent out of the inline area: shift trailing entries forward.
    SpliceOutInlineDirEntry(rootDirBytes, inlineStart, currentSize, entryOffset, entryLen);
    UpdateDinodeSize(rootDirBytes, currentSize - entryLen);
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

    var (inlineStart, _, currentSize) = GetRootDirInlineWindow(rootDirBytes);
    if (!FindDirEntry(rootDirBytes, inlineStart, currentSize, name, out var entryOffset, out _))
      return false;

    var inodeBlk = (long)BinaryPrimitives.ReadUInt64LittleEndian(rootDirBytes.AsSpan(entryOffset, 8));
    var fileType = rootDirBytes[entryOffset + 11];
    if (fileType != FtRegFile)
      throw new NotSupportedException($"ocfs2: '{name}' is not a regular file.");

    var dinodeBytes = ReadBlock(image, inodeBlk);
    if (!HasSignature(dinodeBytes))
      throw new InvalidDataException(
        $"ocfs2: dirent for '{name}' points at block {inodeBlk} which lacks the OCFSV2 signature.");

    var extOff = Id2Offset;
    var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(dinodeBytes.AsSpan(extOff + 4, 2));
    if (nextFreeRec == 0 && newData.Length > 0)
      throw new NotSupportedException(
        $"ocfs2: '{name}' has no data extent; in-place grow from zero is not supported.");

    // Sum existing allocation to verify the new payload fits.
    var totalAllocClusters = 0;
    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + 8 + i * 16;
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
      var recOff = extOff + 8 + i * 16;
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

    // Update i_size in the file dinode header (+0x1C).
    BinaryPrimitives.WriteUInt64LittleEndian(dinodeBytes.AsSpan(0x1C, 8), (ulong)newData.Length);
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
    if (!HasSignature(sb))
      throw new InvalidDataException("ocfs2: superblock dinode lacks the OCFSV2 signature.");
  }

  private static bool HasSignature(byte[] block)
    => block.Length >= 6 && block.AsSpan(0, 6).SequenceEqual(Ocfs2Superblock.SignatureBytes);

  private static void EnsureInlineRootDir(byte[] rootDirBytes) {
    if (!HasSignature(rootDirBytes))
      throw new InvalidDataException("ocfs2: root directory dinode lacks the OCFSV2 signature.");
    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(rootDirBytes.AsSpan(0x4C, 2));
    if ((dynFeatures & DynInlineData) == 0)
      throw new NotSupportedException(
        "ocfs2: in-place modifier supports inline-data root directories only; "
        + "extent-backed root directories require subdir B-tree handling (deferred).");
  }

  /// <summary>
  /// Returns the byte range of the inline dirent area inside the root
  /// directory dinode: (start, capacity, currentSize). The id2 area opens with
  /// a u16 id_count, followed by the inline dirent stream whose total byte
  /// length is recorded in the dinode's i_size (+0x1C).
  /// </summary>
  private static (int Start, int Capacity, int CurrentSize) GetRootDirInlineWindow(byte[] rootDirBytes) {
    var start = Id2Offset + 2; // skip the id_count u16
    var capacity = BlockSize - start;
    var currentSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(rootDirBytes.AsSpan(0x1C, 8));
    if (currentSize < 0 || currentSize > capacity)
      throw new InvalidDataException(
        $"ocfs2: root dir i_size {currentSize} is outside inline capacity [0..{capacity}].");
    return (start, capacity, currentSize);
  }

  private static void UpdateDinodeSize(byte[] dinodeBytes, long newSize)
    => BinaryPrimitives.WriteUInt64LittleEndian(dinodeBytes.AsSpan(0x1C, 8), (ulong)newSize);

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
  private static bool FindDirEntry(byte[] block, int start, int currentSize, string name,
                                   out int entryOffset, out int entryLen) {
    entryOffset = -1; entryLen = 0;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var cursor = start;
    var end = start + currentSize;
    while (cursor + 12 <= end) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 8, 2));
      var nameLen = block[cursor + 10];
      if (recLen < 12 || cursor + recLen > end) return false;
      if (nameLen == nameBytes.Length && block.AsSpan(cursor + 12, nameLen).SequenceEqual(nameBytes)) {
        entryOffset = cursor;
        entryLen = recLen;
        return true;
      }
      cursor += recLen;
    }
    return false;
  }

  /// <summary>
  /// Appends a new dirent at the end of the inline area. Caller has verified
  /// there is room (newEntrySize ≤ capacity − currentSize).
  /// </summary>
  private static void AppendInlineDirEntry(byte[] block, int start, int currentSize,
                                           long inodeBlk, string name, byte fileType) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var recLen = (8 + 2 + 1 + 1 + nameBytes.Length + 3) & ~3;
    var pos = start + currentSize;

    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(pos, 8), (ulong)inodeBlk);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos + 8, 2), (ushort)recLen);
    block[pos + 10] = (byte)nameBytes.Length;
    block[pos + 11] = fileType;
    nameBytes.CopyTo(block.AsSpan(pos + 12, nameBytes.Length));
    // Zero the alignment padding between name end and recLen.
    for (var i = pos + 12 + nameBytes.Length; i < pos + recLen; i++)
      block[i] = 0;
  }

  /// <summary>
  /// Splices a dirent out of the inline area: shifts the trailing entries
  /// forward by entryLen bytes and zero-fills the freed tail. Preserves the
  /// reader's contract that the inline area is a packed stream of dirents up
  /// to currentSize.
  /// </summary>
  private static void SpliceOutInlineDirEntry(byte[] block, int start, int currentSize,
                                              int entryOffset, int entryLen) {
    var endOfArea = start + currentSize;
    var tailStart = entryOffset + entryLen;
    var tailLen = endOfArea - tailStart;
    if (tailLen > 0)
      Buffer.BlockCopy(block, tailStart, block, entryOffset, tailLen);
    // Zero the now-unused tail so old name bytes don't linger forensically.
    Array.Clear(block, entryOffset + tailLen, entryLen);
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────

  private static bool TestBit(byte[] bitmap, int bit)
    => (bitmap[bit / 8] & (1 << (bit % 8))) != 0;

  private static void SetBit(byte[] bitmap, int bit)
    => bitmap[bit / 8] |= (byte)(1 << (bit % 8));

  private static void ClearBit(byte[] bitmap, int bit)
    => bitmap[bit / 8] &= (byte)~(1 << (bit % 8));

  /// <summary>Allocates the first free cluster (LSB-first scan). bit N = cluster N.</summary>
  private static long? AllocateCluster(byte[] bitmap) {
    var maxBit = bitmap.Length * 8;
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
    var maxBit = bitmap.Length * 8;
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
  /// emits: OCFSV2 signature at offset 0, i_size + i_mode + i_flags + i_blkno
  /// + i_clusters + i_fs_generation in the header, and a single-extent
  /// <c>ocfs2_extent_list</c> at id2 (+0xC0) pointing at the contiguous data
  /// run. Reader (Ocfs2FormatDescriptor.ExtractFileData) traverses these
  /// fields, so writing them in the writer's format is what makes the file
  /// surface in List/Extract.
  /// </summary>
  private static byte[] BuildFileDinodeBlock(long blkno, long dataBlkno, int dataClusters, int fileSize) {
    var block = new byte[BlockSize];

    // i_signature[8] at offset 0
    Ocfs2Superblock.SignatureBytes.CopyTo(block.AsSpan(0, 6));
    // i_generation at +8
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x08, 4), (uint)(blkno + 100));
    // i_suballoc_slot at +12 (i16): -1
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(0x0C, 2), -1);
    // i_suballoc_bit at +14 (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0E, 2), (ushort)blkno);
    // i_links_count at +0x10
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x10, 2), 1);
    // i_size at +0x1C
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x1C, 8), (ulong)fileSize);
    // i_mode at +0x24
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x24, 2), (ushort)ModeFile);
    // i_flags at +0x28
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x28, 4), InodeValid);
    // i_blkno at +0x30
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x30, 8), (ulong)blkno);
    // i_clusters at +0x38
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x38, 4), (uint)dataClusters);
    // i_fs_generation at +0x40
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x40, 4), 1);

    if (fileSize == 0) return block;

    // ocfs2_extent_list at id2:
    var off = Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 0, 2), 0); // l_tree_depth
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 2, 2), 1); // l_count
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 4, 2), 1); // l_next_free_rec
    // extent record:
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(off + 8, 4), 0);            // e_cpos
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 12, 2), (ushort)dataClusters); // e_int_clusters
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(off + 16, 8), (ulong)dataBlkno);     // e_blkno
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
