#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs;

/// <summary>
/// Real in-place mutation of a JFS1 aggregate image emitted by <see cref="JfsWriter"/>.
/// <para>
/// Scope (extended past leaf-only):
/// <list type="bullet">
///   <item><b>dtree leaf insert/delete</b> — at <b>arbitrary path depth</b>.
///   Descends from the root by name into each intermediate directory's dtree
///   (inline or external/router-promoted), then mutates the target directory's
///   dtree slot table. Supports <b>long names via continuation slots</b> chained
///   through the head ldtentry's <c>next</c> byte. Inline dtroot leaf splits and
///   external dtree leaf splits still fall back; insert into an external dtree
///   leaf that has room is handled.</item>
///   <item><b>xtree extent allocate</b> — inline xad written into the new file
///   dinode's <c>di_data</c> area (up to 16 xad slots). xtree root promotion to
///   a non-leaf falls back honestly.</item>
///   <item><b>dmap binary-buddy</b> — walks the per-AG dmap chain (up to 2 dmaps
///   on the writer's layout) to find a free contiguous run, sets/clears bits in
///   <c>wmap</c>+<c>pmap</c>, then reruns the canonical <c>ujfs_adjtree</c>
///   two-phase walk against every modified dmap and refreshes the L0
///   <c>dmapctl.stree</c> and <c>dbmap.dn_nfree</c>/<c>dn_agfree[]</c>
///   counters.</item>
///   <item><b>dtree leaf delete</b> — locates the entry's stbl index (in the
///   inline dtroot or an external leaf page), shifts the table down to close
///   the hole, frees any continuation slots in the name chain, pushes the
///   freed entry slots back onto the page's freelist, frees the entry's xtree
///   extents via dmap and zeros the dinode.</item>
///   <item><b>recursive subdirectory removal</b> — when the entry is a
///   directory, walks its dtree (inline or external) in DFS, frees each child
///   file's xtree extents + inode + dmap bits, recurses into nested
///   subdirectories, then frees the directory's own external dtree pages
///   (when present) and its inode. The parent directory's stbl entry is then
///   closed out the same way as a file removal.</item>
/// </list>
/// </para>
/// <para>
/// Operations that genuinely require multi-week scope still throw
/// <see cref="NotSupportedException"/> with a SPECIFIC message identifying the
/// path: inline dtroot splits, external dtree leaf splits, xtree root
/// promotion, IAG allocation, FSIT extent growth.
/// </para>
/// </summary>
internal static class JfsMutator {
  // Constants mirrored from JfsWriter so the mutator does not require new
  // visibility surface. Kept private to this class.
  private const int SuperblockOffset = JfsWriter.SuperblockOffset;
  private const int BlockSize = JfsWriter.BlockSize;
  private const int InodeSize = JfsWriter.InodeSize;
  private const int InodesPerExtent = JfsWriter.InodesPerExtent;
  private const int InodeExtentBlocks = JfsWriter.InodeExtentBlocks;
  private const int RootIno = JfsWriter.RootIno;
  private const int FirstFileIno = JfsWriter.FirstFileIno;
  private const int FilesetIno = JfsWriter.FilesetIno;
  private const int XtreeDataOffset = JfsWriter.XtreeDataOffset;
  private const int DiDataSize = JfsWriter.DiDataSize;
  private const int InlineDirEntries = JfsWriter.InlineDirEntries;
  private const int InostampFixed = JfsWriter.InostampFixed;
  private const int MaxNodesPerIag = JfsWriter.MaxNodesPerIag;

  // di_mode bits
  private const uint IfReg = 0x8000;
  private const uint IfDir = 0x4000;
  private const uint IfJournal = 0x00010000;

  // dtree flag bits
  private const byte BtRoot = 0x01;
  private const byte BtLeaf = 0x02;
  private const byte BtInternal = 0x04;

  // dmap geometry — must match JfsWriter
  private const int Dmap_Lperdmap = 256;
  private const int Dmap_L2lperdmap = 8;
  private const int Dmap_Bperdmap = 8192;
  private const int Dmap_L2bperdmap = 13;
  private const int Dmap_Budmin = 5;
  private const int Dmap_Leafind = 85;
  private const int Dmapctl_Lperctl = 1024;
  private const int Dmapctl_L2lperctl = 10;
  private const int Dmapctl_Leafind = 341;
  private const sbyte Dmap_Nofree = -1;

  // Fixed block addresses (must match writer)
  private const int AimBlock = 9;
  private const int AitBlock = 11;
  private const int BmapBlock = 16;
  private const int L0DmapctlBlock = 19;
  private const int FirstDmapBlock = 20;
  private const int SecondaryAimBlock = 22;
  private const int SecondaryAitBlock = 24;
  private const int FilesetAimBlock = 28;
  private const int FsitBlock = 30;

  // Inline dtree name capacities
  private const int DtSlotSize = 32;
  private const int DtHeadNameChars = 11;
  private const int DtSlotNameChars = 15;
  private const int DtRootStblOffset = 24;
  private const int DtPageMaxSlot = 128;          // external dtpage has 128 slots
  private const int DtStblSlotIndex = 1;          // external page stbl base slot
  private const int ExternalStblSlots = 4;        // 4 stbl slots = 128 entries
  private const int ExternalFirstEntrySlot = DtStblSlotIndex + ExternalStblSlots; // 5

  // ── public entry points ────────────────────────────────────────────────

  /// <summary>
  /// Inserts <paramref name="data"/> as a file at the given <paramref name="path"/>
  /// inside the JFS image. Supports arbitrary path depth, long names via
  /// continuation slots, and external dtree leaf insertion when the target
  /// directory's dtree has been promoted to a router. Splits and IAG-full
  /// scenarios still throw <see cref="NotSupportedException"/> with a specific
  /// message identifying the situation.
  /// </summary>
  public static void AddRootFile(byte[] image, string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);

    var ctx = new ImageContext(image);

    // Split path into components — accept '/' and '\\' as separators.
    var parts = path.Split('/', '\\').Where(p => p.Length > 0).ToArray();
    if (parts.Length == 0)
      throw new ArgumentException("Path must contain at least one component.", nameof(path));

    // Descend by name through each intermediate directory.
    var parentIno = RootIno;
    for (var i = 0; i < parts.Length - 1; i++) {
      var part = parts[i];
      var childIno = LookupChildInDirectory(ctx, parentIno, part);
      if (childIno < 0)
        throw new DirectoryNotFoundException($"Jfs: intermediate directory '{part}' not found at depth {i}.");
      var childMode = ReadInodeMode(ctx, childIno);
      if ((childMode & 0xF000) != IfDir)
        throw new InvalidOperationException($"Jfs: path component '{part}' is not a directory.");
      parentIno = childIno;
    }

    var leafName = parts[^1];
    if (LookupChildInDirectory(ctx, parentIno, leafName) >= 0)
      throw new InvalidOperationException($"Jfs: entry '{leafName}' already exists in target directory.");

    // 1. Allocate a free fileset inode.
    var newIno = AllocateFilesetInode(ctx);
    if (newIno < 0)
      throw new NotSupportedException("Jfs: fileset IAG #0 full — multi-week scope (would need new IAG extent).");

    // 2. Allocate data blocks via dmap.
    var blocksNeeded = Math.Max(1, (data.Length + BlockSize - 1) / BlockSize);
    var firstBlock = AllocateContiguousBlocks(ctx, blocksNeeded);
    if (firstBlock < 0)
      throw new NotSupportedException("Jfs: contiguous block allocation failed across both dmaps — image too full.");

    // 3. Write file data.
    if (data.Length > 0)
      data.CopyTo(image.AsSpan((int)((long)firstBlock * BlockSize)));
    var tailStart = (long)firstBlock * BlockSize + data.Length;
    var tailEnd = (long)(firstBlock + blocksNeeded) * BlockSize;
    image.AsSpan((int)tailStart, (int)(tailEnd - tailStart)).Clear();

    // 4. Write the file dinode (xtree root with one inline xad).
    var dinodeOff = ctx.FsitOffset + (long)newIno * InodeSize;
    WriteFileDinode(image, (int)dinodeOff, newIno, data.Length, firstBlock, blocksNeeded, ctx.WriteTimestamp);

    // 5. Insert entry into the parent directory's dtree.
    InsertEntryIntoDirectory(ctx, parentIno, leafName, newIno);

    // 6. Update parent directory's mtime/ctime.
    var parentDinodeOff = (int)ctx.FsitOffset + parentIno * InodeSize;
    UpdateInodeTimes(image, parentDinodeOff, ctx.WriteTimestamp);
  }

  /// <summary>
  /// Removes the entry at the given <paramref name="path"/>, freeing its xtree
  /// extents and inode. Supports arbitrary path depth, external dtree leaf
  /// removal, and recursive subdirectory removal. External dtree leaf splits
  /// are not needed for delete (only insert), so the only fallback here is
  /// xtree non-leaf-root removal for very large files.
  /// </summary>
  public static void RemoveRootEntry(byte[] image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);

    var ctx = new ImageContext(image);

    var parts = path.Split('/', '\\').Where(p => p.Length > 0).ToArray();
    if (parts.Length == 0)
      throw new ArgumentException("Path must contain at least one component.", nameof(path));

    // Descend to parent directory.
    var parentIno = RootIno;
    for (var i = 0; i < parts.Length - 1; i++) {
      var part = parts[i];
      var childIno = LookupChildInDirectory(ctx, parentIno, part);
      if (childIno < 0)
        throw new DirectoryNotFoundException($"Jfs: intermediate directory '{part}' not found at depth {i}.");
      parentIno = childIno;
    }

    var leafName = parts[^1];
    var entryIno = LookupChildInDirectory(ctx, parentIno, leafName);
    if (entryIno < 0)
      throw new FileNotFoundException($"Jfs: entry '{leafName}' not found in target directory.");

    var entryDinodeOff = (int)ctx.FsitOffset + entryIno * InodeSize;
    var entryMode = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entryDinodeOff + 52));

    if ((entryMode & 0xF000) == IfDir) {
      // Recursive subdirectory removal: walk the dtree (DFS), freeing every
      // child's resources, then free the directory's own pages + inode.
      RecursivelyRemoveDirectory(ctx, entryIno);
    } else {
      // File: free xtree extents.
      FreeFileXtreeExtents(image, entryDinodeOff, ctx);
    }

    // Clear the IAG bit for the inode.
    FreeFilesetInode(ctx, entryIno);

    // Zero the dinode.
    image.AsSpan(entryDinodeOff, InodeSize).Clear();

    // Remove the entry from the parent directory's dtree.
    RemoveEntryFromDirectory(ctx, parentIno, leafName);

    // Update parent mtime/ctime.
    var parentDinodeOff = (int)ctx.FsitOffset + parentIno * InodeSize;
    UpdateInodeTimes(image, parentDinodeOff, ctx.WriteTimestamp);
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private sealed class ImageContext {
    public byte[] Image { get; }
    public long FsitOffset { get; }
    public uint WriteTimestamp { get; } = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public ImageContext(byte[] image) {
      this.Image = image;
      var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SuperblockOffset + 16));
      if (blockSize <= 0) blockSize = BlockSize;
      var aitOff = (long)AitBlock * blockSize;
      var fsiOff = aitOff + (long)FilesetIno * InodeSize;
      var xtRootOff = (int)fsiOff + XtreeDataOffset;
      var xad = image.AsSpan(xtRootOff + 32, 16);
      var filesetAimAddr = ReadPxdAddress(xad[8..]);
      var iagOff = (long)filesetAimAddr * blockSize + blockSize;
      var inoextPxd = image.AsSpan((int)iagOff + 3072, 8);
      var inoextAddr = ReadPxdAddress(inoextPxd);
      this.FsitOffset = (long)inoextAddr * blockSize;
    }

    public int RootDtRootOffset => (int)this.FsitOffset + RootIno * InodeSize + XtreeDataOffset;

    public int DinodeOffset(int ino) => (int)this.FsitOffset + ino * InodeSize;
    public int DtRootOffset(int ino) => this.DinodeOffset(ino) + XtreeDataOffset;
  }

  private static ulong ReadPxdAddress(ReadOnlySpan<byte> pxd) {
    var lenAddr = BinaryPrimitives.ReadUInt32LittleEndian(pxd);
    var addr2 = BinaryPrimitives.ReadUInt32LittleEndian(pxd[4..]);
    var hi = (ulong)(lenAddr >> 24);
    return (hi << 32) | addr2;
  }

  private static uint ReadPxdLength(ReadOnlySpan<byte> pxd) {
    var lenAddr = BinaryPrimitives.ReadUInt32LittleEndian(pxd);
    return lenAddr & 0xFFFFFFu;
  }

  private static void WritePxd(Span<byte> dst, uint length, ulong address) {
    var lenMasked = length & 0xFFFFFFu;
    var addrHi = (uint)((address >> 32) & 0xFF) << 24;
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0..], lenMasked | addrHi);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(address & 0xFFFFFFFF));
  }

  // Reads a directory-entry name from a head slot in an inline dtroot OR an
  // external dtpage. `pageBase` is the slot-array base byte offset (di_data
  // origin for an inline dtroot, the page byte offset for an external page).
  // Continuation slots are chained through the head's `next` byte (at +4 for
  // ldtentry leaf head, +8 for idtentry router head).
  private static string ReadEntryName(byte[] image, int pageBase, int headSlotOff, bool isRouter, int maxSlot) {
    var namLenOff = isRouter ? headSlotOff + 9 : headSlotOff + 5;
    var nextOff = isRouter ? headSlotOff + 8 : headSlotOff + 4;
    var nameOff = isRouter ? headSlotOff + 10 : headSlotOff + 6;
    var namLen = image[namLenOff];
    if (namLen == 0) return "";

    var sb = new StringBuilder(namLen);
    var headChars = Math.Min((int)namLen, DtHeadNameChars);
    sb.Append(Encoding.Unicode.GetString(image.AsSpan(nameOff, headChars * 2)));
    var remaining = namLen - headChars;
    var next = (int)(sbyte)image[nextOff];
    var guard = 0;
    while (remaining > 0 && next > 0 && next <= maxSlot && guard++ < 128) {
      var contOff = pageBase + next * DtSlotSize;
      var contChars = Math.Min(remaining, DtSlotNameChars);
      sb.Append(Encoding.Unicode.GetString(image.AsSpan(contOff + 2, contChars * 2)));
      remaining -= contChars;
      next = (int)(sbyte)image[contOff];
    }
    return sb.ToString();
  }

  private static uint ReadInodeMode(ImageContext ctx, int ino) {
    var dinodeOff = ctx.DinodeOffset(ino);
    return BinaryPrimitives.ReadUInt32LittleEndian(ctx.Image.AsSpan(dinodeOff + 52));
  }

  // Looks up a child by name in the given directory's dtree (inline or
  // external/router). Returns -1 if not found.
  private static int LookupChildInDirectory(ImageContext ctx, int dirIno, string name) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var flag = image[dtRootOff + 16];
    var nextIndex = image[dtRootOff + 17];

    if ((flag & BtInternal) != 0 && (flag & BtLeaf) == 0) {
      var stblOff = dtRootOff + DtRootStblOffset;
      for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
        var slotIdx = (int)(sbyte)image[stblOff + i];
        if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
        var slotOff = dtRootOff + slotIdx * DtSlotSize;
        var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
        var found = LookupInExternalDtree(ctx, childBlock, name);
        if (found >= 0) return found;
      }
      return -1;
    }

    var inlineStblOff = dtRootOff + DtRootStblOffset;
    for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
      var slotIdx = (int)(sbyte)image[inlineStblOff + i];
      if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
      var slotOff = dtRootOff + slotIdx * DtSlotSize;
      var existing = ReadEntryName(image, dtRootOff, slotOff, isRouter: false, maxSlot: InlineDirEntries);
      if (existing == name)
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotOff));
    }
    return -1;
  }

  // Walks an external dtree subtree looking for `name`.
  private static int LookupInExternalDtree(ImageContext ctx, long pageBlock, string name) {
    var image = ctx.Image;
    var pageOff = pageBlock * BlockSize;
    if (pageOff <= 0 || pageOff + BlockSize > image.Length) return -1;
    var p = (int)pageOff;

    var flag = image[p + 16];
    var nextIndex = image[p + 17];
    var stblIndex = image[p + 21];
    var stblOff = p + stblIndex * DtSlotSize;
    var isLeaf = (flag & BtLeaf) != 0;

    if (isLeaf) {
      for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
        var slotIdx = (int)(byte)image[stblOff + i];
        if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
        var slotOff = p + slotIdx * DtSlotSize;
        var existing = ReadEntryName(image, p, slotOff, isRouter: false, maxSlot: DtPageMaxSlot - 1);
        if (existing == name)
          return (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotOff));
      }
      return -1;
    }

    for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
      var slotIdx = (int)(byte)image[stblOff + i];
      if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
      var slotOff = p + slotIdx * DtSlotSize;
      var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
      if (childBlock > 0) {
        var found = LookupInExternalDtree(ctx, childBlock, name);
        if (found >= 0) return found;
      }
    }
    return -1;
  }

  // ── dtree insert + delete dispatch ─────────────────────────────────────

  private static void InsertEntryIntoDirectory(ImageContext ctx, int dirIno, string name, int newIno) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var flag = image[dtRootOff + 16];

    if ((flag & BtInternal) != 0 && (flag & BtLeaf) == 0) {
      InsertIntoExternalDtree(ctx, dirIno, name, newIno);
      return;
    }
    InsertIntoInlineDtroot(image, dtRootOff, name, newIno, InlineDirEntries);
  }

  private static void RemoveEntryFromDirectory(ImageContext ctx, int dirIno, string name) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var flag = image[dtRootOff + 16];

    if ((flag & BtInternal) != 0 && (flag & BtLeaf) == 0) {
      RemoveFromExternalDtree(ctx, dirIno, name);
      return;
    }
    RemoveFromInlineDtroot(image, dtRootOff, name);
  }

  private static int SlotsRequiredForName(string name) {
    if (name.Length <= DtHeadNameChars) return 1;
    return 1 + (name.Length - DtHeadNameChars + DtSlotNameChars - 1) / DtSlotNameChars;
  }

  // ── inline dtroot insert ──────────────────────────────────────────────

  private static void InsertIntoInlineDtroot(byte[] image, int dtRootOff, string name, int newIno, int maxSlot) {
    var nextIndex = image[dtRootOff + 17];
    var stblOff = dtRootOff + DtRootStblOffset;
    var freeCount = (int)(sbyte)image[dtRootOff + 18];

    var slotsNeeded = SlotsRequiredForName(name);
    if (freeCount < slotsNeeded || nextIndex >= InlineDirEntries)
      throw new NotSupportedException(
        $"Jfs: inline dtroot leaf split needed (need {slotsNeeded} slots, have {freeCount} free, stbl at {nextIndex}/{InlineDirEntries}).");

    for (var i = 0; i < nextIndex; i++) {
      var slotIdx = (int)(sbyte)image[stblOff + i];
      if (slotIdx <= 0 || slotIdx > maxSlot) continue;
      var slotOff = dtRootOff + slotIdx * DtSlotSize;
      var existingName = ReadEntryName(image, dtRootOff, slotOff, isRouter: false, maxSlot: maxSlot);
      if (existingName == name)
        throw new InvalidOperationException($"Jfs: entry '{name}' already exists in directory.");
    }

    Span<int> claimedSlots = stackalloc int[slotsNeeded];
    for (var s = 0; s < slotsNeeded; s++) {
      var freelistHead = (int)(sbyte)image[dtRootOff + 19];
      if (freelistHead <= 0 || freelistHead > maxSlot)
        throw new NotSupportedException("Jfs: inline dtroot freelist exhausted mid-allocation.");
      claimedSlots[s] = freelistHead;
      var nextFreeOff = dtRootOff + freelistHead * DtSlotSize;
      var nextHead = (int)(sbyte)image[nextFreeOff];
      image[dtRootOff + 19] = unchecked((byte)nextHead);
      image.AsSpan(nextFreeOff, DtSlotSize).Clear();
    }

    var headSlot = claimedSlots[0];
    var headSlotOff = dtRootOff + headSlot * DtSlotSize;

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headSlotOff), (uint)newIno);
    image[headSlotOff + 5] = (byte)name.Length;
    var headChars = Math.Min(name.Length, DtHeadNameChars);
    for (var c = 0; c < headChars; c++)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headSlotOff + 6 + c * 2), name[c]);

    var prevNextOff = headSlotOff + 4;
    var written = headChars;
    for (var contIdx = 1; contIdx < slotsNeeded; contIdx++) {
      var contSlot = claimedSlots[contIdx];
      image[prevNextOff] = (byte)contSlot;
      var contOff = dtRootOff + contSlot * DtSlotSize;
      var contChars = Math.Min(name.Length - written, DtSlotNameChars);
      image[contOff + 1] = (byte)contChars;
      for (var c = 0; c < contChars; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(contOff + 2 + c * 2), name[written + c]);
      written += contChars;
      prevNextOff = contOff;
    }
    image[prevNextOff] = unchecked((byte)-1);

    var insertAt = (int)nextIndex;
    for (var i = 0; i < nextIndex; i++) {
      var existingSlot = (int)(sbyte)image[stblOff + i];
      if (existingSlot <= 0 || existingSlot > maxSlot) continue;
      var existingName = ReadEntryName(image, dtRootOff, dtRootOff + existingSlot * DtSlotSize,
        isRouter: false, maxSlot: maxSlot);
      if (string.CompareOrdinal(name, existingName) < 0) {
        insertAt = i;
        break;
      }
    }

    for (var i = nextIndex; i > insertAt; i--)
      image[stblOff + i] = image[stblOff + i - 1];
    image[stblOff + insertAt] = (byte)headSlot;

    for (var i = 0; i < nextIndex + 1; i++) {
      var slot = (int)(sbyte)image[stblOff + i];
      if (slot <= 0 || slot > maxSlot) continue;
      var slotOff = dtRootOff + slot * DtSlotSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(slotOff + 28), (uint)i);
    }

    image[dtRootOff + 18] = (byte)(freeCount - slotsNeeded);
    image[dtRootOff + 17] = (byte)(nextIndex + 1);
  }

  // ── inline dtroot remove ──────────────────────────────────────────────

  private static void RemoveFromInlineDtroot(byte[] image, int dtRootOff, string name) {
    var nextIndex = image[dtRootOff + 17];
    var stblOff = dtRootOff + DtRootStblOffset;

    var stblIndex = -1;
    var slotIdx = -1;
    for (var i = 0; i < nextIndex; i++) {
      var idx = (int)(sbyte)image[stblOff + i];
      if (idx <= 0 || idx > InlineDirEntries) continue;
      var slotOff = dtRootOff + idx * DtSlotSize;
      var existing = ReadEntryName(image, dtRootOff, slotOff, isRouter: false, maxSlot: InlineDirEntries);
      if (existing == name) {
        stblIndex = i;
        slotIdx = idx;
        break;
      }
    }

    if (stblIndex < 0)
      throw new FileNotFoundException($"Jfs: entry '{name}' not found in inline dtroot.");

    var headSlotOff = dtRootOff + slotIdx * DtSlotSize;
    var namLen = image[headSlotOff + 5];
    var slotsToFree = new List<int> { slotIdx };
    var next = (int)(sbyte)image[headSlotOff + 4];
    var remaining = namLen - Math.Min((int)namLen, DtHeadNameChars);
    var guard = 0;
    while (remaining > 0 && next > 0 && next <= InlineDirEntries && guard++ < InlineDirEntries) {
      slotsToFree.Add(next);
      var contOff = dtRootOff + next * DtSlotSize;
      var contChars = Math.Min(remaining, DtSlotNameChars);
      remaining -= contChars;
      next = (int)(sbyte)image[contOff];
    }

    for (var i = stblIndex; i < nextIndex - 1; i++)
      image[stblOff + i] = image[stblOff + i + 1];
    image[stblOff + nextIndex - 1] = 0;

    foreach (var s in slotsToFree) {
      var freedSlotOff = dtRootOff + s * DtSlotSize;
      image.AsSpan(freedSlotOff, DtSlotSize).Clear();
      var prevHead = (int)(sbyte)image[dtRootOff + 19];
      image[freedSlotOff] = unchecked((byte)prevHead);
      image[freedSlotOff + 1] = 1;
      image[dtRootOff + 19] = (byte)s;
    }

    var newNextIndex = nextIndex - 1;
    for (var i = 0; i < newNextIndex; i++) {
      var slot = (int)(sbyte)image[stblOff + i];
      if (slot <= 0 || slot > InlineDirEntries) continue;
      var slotOff = dtRootOff + slot * DtSlotSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(slotOff + 28), (uint)i);
    }

    var oldFree = (int)(sbyte)image[dtRootOff + 18];
    image[dtRootOff + 18] = (byte)(oldFree + slotsToFree.Count);
    image[dtRootOff + 17] = (byte)newNextIndex;
  }

  // ── external dtree leaf insert ────────────────────────────────────────

  private static void InsertIntoExternalDtree(ImageContext ctx, int dirIno, string name, int newIno) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var nextIndex = image[dtRootOff + 17];
    var stblOff = dtRootOff + DtRootStblOffset;

    var targetChildBlock = -1L;
    for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
      var slotIdx = (int)(sbyte)image[stblOff + i];
      if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
      var slotOff = dtRootOff + slotIdx * DtSlotSize;
      var key = ReadEntryName(image, dtRootOff, slotOff, isRouter: true, maxSlot: InlineDirEntries);
      if (string.CompareOrdinal(key, name) <= 0)
        targetChildBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
      else
        break;
    }
    if (targetChildBlock < 0) {
      var firstSlot = (int)(sbyte)image[stblOff + 0];
      if (firstSlot <= 0 || firstSlot > InlineDirEntries)
        throw new NotSupportedException("Jfs: external dtree router has no valid first slot.");
      var firstSlotOff = dtRootOff + firstSlot * DtSlotSize;
      targetChildBlock = (long)ReadPxdAddress(image.AsSpan(firstSlotOff));
    }

    InsertIntoExternalLeafPage(ctx, targetChildBlock, name, newIno);
  }

  private static void InsertIntoExternalLeafPage(ImageContext ctx, long pageBlock, string name, int newIno) {
    var image = ctx.Image;
    var pageOff = pageBlock * BlockSize;
    if (pageOff <= 0 || pageOff + BlockSize > image.Length)
      throw new InvalidDataException("Jfs: external dtpage out of range.");
    var p = (int)pageOff;
    var flag = image[p + 16];
    if ((flag & BtLeaf) == 0)
      throw new NotSupportedException("Jfs: external dtree internal-page descent past one level not implemented — multi-week scope.");

    var nextIndex = image[p + 17];
    var freeCount = image[p + 18];
    var stblIndex = image[p + 21];
    var stblOff = p + stblIndex * DtSlotSize;

    if (nextIndex >= DtPageMaxSlot - ExternalFirstEntrySlot)
      throw new NotSupportedException("Jfs: external dtree leaf page stbl full — split needed (multi-week scope).");

    var slotsNeeded = SlotsRequiredForName(name);
    if (freeCount < slotsNeeded)
      throw new NotSupportedException(
        $"Jfs: external dtree leaf page split needed (need {slotsNeeded} slots, have {freeCount} free).");

    Span<int> claimedSlots = stackalloc int[slotsNeeded];
    for (var s = 0; s < slotsNeeded; s++) {
      var freelistHead = (int)(sbyte)image[p + 19];
      if (freelistHead <= 0 || freelistHead >= DtPageMaxSlot)
        throw new NotSupportedException("Jfs: external dtpage freelist exhausted mid-allocation.");
      claimedSlots[s] = freelistHead;
      var nextFreeOff = p + freelistHead * DtSlotSize;
      var nextHead = (int)(sbyte)image[nextFreeOff];
      image[p + 19] = unchecked((byte)nextHead);
      image.AsSpan(nextFreeOff, DtSlotSize).Clear();
    }

    var headSlot = claimedSlots[0];
    var headSlotOff = p + headSlot * DtSlotSize;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headSlotOff), (uint)newIno);
    image[headSlotOff + 5] = (byte)name.Length;
    var headChars = Math.Min(name.Length, DtHeadNameChars);
    for (var c = 0; c < headChars; c++)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headSlotOff + 6 + c * 2), name[c]);

    var prevNextOff = headSlotOff + 4;
    var written = headChars;
    for (var contIdx = 1; contIdx < slotsNeeded; contIdx++) {
      var contSlot = claimedSlots[contIdx];
      image[prevNextOff] = (byte)contSlot;
      var contOff = p + contSlot * DtSlotSize;
      var contChars = Math.Min(name.Length - written, DtSlotNameChars);
      image[contOff + 1] = (byte)contChars;
      for (var c = 0; c < contChars; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(contOff + 2 + c * 2), name[written + c]);
      written += contChars;
      prevNextOff = contOff;
    }
    image[prevNextOff] = unchecked((byte)-1);

    var insertAt = (int)nextIndex;
    for (var i = 0; i < nextIndex; i++) {
      var existingSlot = (int)(byte)image[stblOff + i];
      if (existingSlot == 0 || existingSlot >= DtPageMaxSlot) continue;
      var existingName = ReadEntryName(image, p, p + existingSlot * DtSlotSize,
        isRouter: false, maxSlot: DtPageMaxSlot - 1);
      if (string.CompareOrdinal(name, existingName) < 0) {
        insertAt = i;
        break;
      }
    }

    for (var i = nextIndex; i > insertAt; i--)
      image[stblOff + i] = image[stblOff + i - 1];
    image[stblOff + insertAt] = (byte)headSlot;

    for (var i = 0; i < nextIndex + 1; i++) {
      var slot = (int)(byte)image[stblOff + i];
      if (slot == 0 || slot >= DtPageMaxSlot) continue;
      var slotOff = p + slot * DtSlotSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(slotOff + 28), (uint)i);
    }

    image[p + 17] = (byte)(nextIndex + 1);
    image[p + 18] = (byte)(freeCount - slotsNeeded);
  }

  // ── external dtree leaf delete ────────────────────────────────────────

  private static void RemoveFromExternalDtree(ImageContext ctx, int dirIno, string name) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var nextIndex = image[dtRootOff + 17];
    var stblOff = dtRootOff + DtRootStblOffset;

    for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
      var slotIdx = (int)(sbyte)image[stblOff + i];
      if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
      var slotOff = dtRootOff + slotIdx * DtSlotSize;
      var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
      if (TryRemoveFromExternalLeafPage(ctx, childBlock, name)) return;
    }
    throw new FileNotFoundException($"Jfs: entry '{name}' not found in external dtree.");
  }

  private static bool TryRemoveFromExternalLeafPage(ImageContext ctx, long pageBlock, string name) {
    var image = ctx.Image;
    var pageOff = pageBlock * BlockSize;
    if (pageOff <= 0 || pageOff + BlockSize > image.Length) return false;
    var p = (int)pageOff;
    var flag = image[p + 16];
    var nextIndex = image[p + 17];
    var stblIndex = image[p + 21];
    var stblOff = p + stblIndex * DtSlotSize;

    if ((flag & BtLeaf) == 0) {
      for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
        var slotIdx = (int)(byte)image[stblOff + i];
        if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
        var slotOff = p + slotIdx * DtSlotSize;
        var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
        if (childBlock > 0 && TryRemoveFromExternalLeafPage(ctx, childBlock, name)) return true;
      }
      return false;
    }

    var stblIndexFound = -1;
    var slotIndexFound = -1;
    for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
      var slotIdx = (int)(byte)image[stblOff + i];
      if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
      var slotOff = p + slotIdx * DtSlotSize;
      var existing = ReadEntryName(image, p, slotOff, isRouter: false, maxSlot: DtPageMaxSlot - 1);
      if (existing == name) {
        stblIndexFound = i;
        slotIndexFound = slotIdx;
        break;
      }
    }
    if (stblIndexFound < 0) return false;

    var headSlotOff = p + slotIndexFound * DtSlotSize;
    var namLen = image[headSlotOff + 5];
    var slotsToFree = new List<int> { slotIndexFound };
    var next = (int)(sbyte)image[headSlotOff + 4];
    var remaining = namLen - Math.Min((int)namLen, DtHeadNameChars);
    var guard = 0;
    while (remaining > 0 && next > 0 && next < DtPageMaxSlot && guard++ < DtPageMaxSlot) {
      slotsToFree.Add(next);
      var contOff = p + next * DtSlotSize;
      var contChars = Math.Min(remaining, DtSlotNameChars);
      remaining -= contChars;
      next = (int)(sbyte)image[contOff];
    }

    for (var i = stblIndexFound; i < nextIndex - 1; i++)
      image[stblOff + i] = image[stblOff + i + 1];
    image[stblOff + nextIndex - 1] = 0;

    foreach (var s in slotsToFree) {
      var freedSlotOff = p + s * DtSlotSize;
      image.AsSpan(freedSlotOff, DtSlotSize).Clear();
      var prevHead = (int)(sbyte)image[p + 19];
      image[freedSlotOff] = unchecked((byte)prevHead);
      image[freedSlotOff + 1] = 1;
      image[p + 19] = (byte)s;
    }

    var newNextIndex = nextIndex - 1;
    for (var i = 0; i < newNextIndex; i++) {
      var slot = (int)(byte)image[stblOff + i];
      if (slot == 0 || slot >= DtPageMaxSlot) continue;
      var slotOff = p + slot * DtSlotSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(slotOff + 28), (uint)i);
    }

    var freeCount = image[p + 18];
    image[p + 18] = (byte)(freeCount + slotsToFree.Count);
    image[p + 17] = (byte)newNextIndex;
    return true;
  }

  // ── recursive directory removal ───────────────────────────────────────

  private static void RecursivelyRemoveDirectory(ImageContext ctx, int dirIno) {
    var image = ctx.Image;
    var dtRootOff = ctx.DtRootOffset(dirIno);
    var flag = image[dtRootOff + 16];
    var nextIndex = image[dtRootOff + 17];

    if ((flag & BtInternal) != 0 && (flag & BtLeaf) == 0) {
      var stblOff = dtRootOff + DtRootStblOffset;
      var routerSlots = new List<int>();
      for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
        var slotIdx = (int)(sbyte)image[stblOff + i];
        if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
        routerSlots.Add(slotIdx);
      }
      foreach (var slot in routerSlots) {
        var slotOff = dtRootOff + slot * DtSlotSize;
        var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
        if (childBlock > 0)
          RecursivelyFreeExternalSubtree(ctx, childBlock);
      }
    } else {
      var stblOff = dtRootOff + DtRootStblOffset;
      var childInos = new List<int>();
      for (var i = 0; i < nextIndex && i < InlineDirEntries; i++) {
        var slotIdx = (int)(sbyte)image[stblOff + i];
        if (slotIdx <= 0 || slotIdx > InlineDirEntries) continue;
        var slotOff = dtRootOff + slotIdx * DtSlotSize;
        var childIno = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotOff));
        if (childIno >= FirstFileIno) childInos.Add(childIno);
      }
      foreach (var childIno in childInos)
        FreeOneChild(ctx, childIno);
    }
  }

  private static void RecursivelyFreeExternalSubtree(ImageContext ctx, long pageBlock) {
    var image = ctx.Image;
    var pageOff = pageBlock * BlockSize;
    if (pageOff <= 0 || pageOff + BlockSize > image.Length) return;
    var p = (int)pageOff;
    var flag = image[p + 16];
    var nextIndex = image[p + 17];
    var stblIndex = image[p + 21];
    var stblOff = p + stblIndex * DtSlotSize;

    if ((flag & BtLeaf) != 0) {
      var childInos = new List<int>();
      for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
        var slotIdx = (int)(byte)image[stblOff + i];
        if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
        var slotOff = p + slotIdx * DtSlotSize;
        var childIno = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotOff));
        if (childIno >= FirstFileIno) childInos.Add(childIno);
      }
      foreach (var childIno in childInos)
        FreeOneChild(ctx, childIno);
    } else {
      for (var i = 0; i < nextIndex && i < DtPageMaxSlot; i++) {
        var slotIdx = (int)(byte)image[stblOff + i];
        if (slotIdx == 0 || slotIdx >= DtPageMaxSlot) continue;
        var slotOff = p + slotIdx * DtSlotSize;
        var childBlock = (long)ReadPxdAddress(image.AsSpan(slotOff));
        if (childBlock > 0)
          RecursivelyFreeExternalSubtree(ctx, childBlock);
      }
    }

    FreeBlocks(ctx, (int)pageBlock, 1);
    image.AsSpan(p, BlockSize).Clear();
  }

  private static void FreeOneChild(ImageContext ctx, int childIno) {
    var image = ctx.Image;
    var childDinodeOff = ctx.DinodeOffset(childIno);
    var childMode = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(childDinodeOff + 52));
    if ((childMode & 0xF000) == IfDir)
      RecursivelyRemoveDirectory(ctx, childIno);
    else
      FreeFileXtreeExtents(image, childDinodeOff, ctx);
    FreeFilesetInode(ctx, childIno);
    image.AsSpan(childDinodeOff, InodeSize).Clear();
  }

  // ── inode allocation in fileset AIM ────────────────────────────────────

  private static int AllocateFilesetInode(ImageContext ctx) {
    var aimPageOff = (long)FilesetAimBlock * BlockSize;
    var iagOff = aimPageOff + BlockSize;
    var img = ctx.Image;
    var newIno = -1;
    for (var ino = FirstFileIno; ino < MaxNodesPerIag; ino++) {
      var word = ino >> 5;
      var bit = 0x80000000u >> (ino & 31);
      var wmapVal = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)iagOff + 2048 + word * 4));
      if ((wmapVal & bit) == 0) {
        newIno = ino;
        break;
      }
    }
    if (newIno < 0) return -1;

    var dinomapOff = aimPageOff;
    var nbackedInodes = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)dinomapOff + 8));
    if (newIno >= nbackedInodes)
      throw new NotSupportedException("Jfs: fileset inode table extent growth needed — multi-week scope.");

    {
      var word = newIno >> 5;
      var bit = 0x80000000u >> (newIno & 31);
      var wmapAddr = (int)iagOff + 2048 + word * 4;
      var pmapAddr = (int)iagOff + 2560 + word * 4;
      var w = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(wmapAddr));
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(wmapAddr), w | bit);
      var pp = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(pmapAddr));
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pmapAddr), pp | bit);
    }

    var freeInos = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)iagOff + 64));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)iagOff + 64), freeInos - 1);

    var extentIdx = newIno / InodesPerExtent;
    var extentWmap = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)iagOff + 2048 + (extentIdx * InodesPerExtent / 32) * 4));
    if (extentWmap == 0xFFFFFFFFu) {
      var smapWord = extentIdx >> 5;
      var smapBit = 0x80000000u >> (extentIdx & 31);
      var inosmapAddr = (int)iagOff + 32 + smapWord * 4;
      var inosmapVal = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(inosmapAddr));
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(inosmapAddr), inosmapVal | smapBit);
    }

    var agNumFree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)dinomapOff + 2060));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)dinomapOff + 2060), agNumFree - 1);
    var nFree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)dinomapOff + 12));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)dinomapOff + 12), nFree - 1);

    return newIno;
  }

  private static void FreeFilesetInode(ImageContext ctx, int ino) {
    var aimPageOff = (long)FilesetAimBlock * BlockSize;
    var iagOff = aimPageOff + BlockSize;
    var dinomapOff = aimPageOff;
    var img = ctx.Image;

    var word = ino >> 5;
    var bit = 0x80000000u >> (ino & 31);
    var wmapAddr = (int)iagOff + 2048 + word * 4;
    var pmapAddr = (int)iagOff + 2560 + word * 4;
    var w = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(wmapAddr));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(wmapAddr), w & ~bit);
    var p = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(pmapAddr));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pmapAddr), p & ~bit);

    var freeInos = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)iagOff + 64));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)iagOff + 64), freeInos + 1);

    var extentIdx = ino / InodesPerExtent;
    var smapWord = extentIdx >> 5;
    var smapBit = 0x80000000u >> (extentIdx & 31);
    var inosmapAddr = (int)iagOff + 32 + smapWord * 4;
    var inosmapVal = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(inosmapAddr));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(inosmapAddr), inosmapVal & ~smapBit);

    var agNumFree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)dinomapOff + 2060));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)dinomapOff + 2060), agNumFree + 1);
    var nFree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan((int)dinomapOff + 12));
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan((int)dinomapOff + 12), nFree + 1);
  }

  // ── dmap block allocation (multi-dmap) ─────────────────────────────────

  private static int AllocateContiguousBlocks(ImageContext ctx, int count) {
    var img = ctx.Image;
    for (var dmapIdx = 0; dmapIdx < 2; dmapIdx++) {
      var dmapPageBlock = FirstDmapBlock + dmapIdx;
      var dmapPageOff = (int)((long)dmapPageBlock * BlockSize);
      if (dmapPageOff + BlockSize > img.Length) break;

      var nblocks = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan(dmapPageOff));
      if (nblocks <= 0) continue;
      var startBlk = BinaryPrimitives.ReadInt64LittleEndian(img.AsSpan(dmapPageOff + 8));
      var wmapOff = dmapPageOff + 2048;

      var runStart = -1;
      var runLen = 0;
      for (var leaf = 0; leaf < Dmap_Lperdmap && runLen < count; leaf++) {
        var word = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(wmapOff + leaf * 4));
        for (var bit = 0; bit < 32; bit++) {
          var blk = (int)startBlk + leaf * 32 + bit;
          if (blk >= startBlk + nblocks) break;
          var bitMask = 0x80000000u >> bit;
          if ((word & bitMask) == 0) {
            if (runStart < 0) runStart = blk;
            ++runLen;
            if (runLen >= count) goto Found;
          } else {
            runStart = -1;
            runLen = 0;
          }
        }
      }
      continue;
      Found:
      for (var b = runStart; b < runStart + count; b++) {
        var bitInDmap = b - (int)startBlk;
        var leaf = bitInDmap / 32;
        var bit = bitInDmap % 32;
        var bitMask = 0x80000000u >> bit;
        var addr = wmapOff + leaf * 4;
        var w = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(addr));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(addr), w | bitMask);
        var paddr = dmapPageOff + 3072 + leaf * 4;
        var pw = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(paddr));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(paddr), pw | bitMask);
      }
      var nfree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan(dmapPageOff + 4));
      BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(dmapPageOff + 4), nfree - count);

      RecomputeDmapTrees(img);
      return runStart;
    }
    return -1;
  }

  private static void FreeBlocks(ImageContext ctx, int firstBlock, int count) {
    var img = ctx.Image;
    var totalFreed = 0;
    for (var dmapIdx = 0; dmapIdx < 2; dmapIdx++) {
      var dmapPageBlock = FirstDmapBlock + dmapIdx;
      var dmapPageOff = (int)((long)dmapPageBlock * BlockSize);
      if (dmapPageOff + BlockSize > img.Length) break;
      var nblocks = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan(dmapPageOff));
      if (nblocks <= 0) continue;
      var startBlk = (int)BinaryPrimitives.ReadInt64LittleEndian(img.AsSpan(dmapPageOff + 8));
      var wmapOff = dmapPageOff + 2048;
      var pmapOff = dmapPageOff + 3072;
      var dmapFreed = 0;
      for (var b = firstBlock; b < firstBlock + count; b++) {
        var bitInDmap = b - startBlk;
        if (bitInDmap < 0 || bitInDmap >= nblocks) continue;
        var leaf = bitInDmap / 32;
        var bit = bitInDmap % 32;
        var bitMask = ~(0x80000000u >> bit);
        var waddr = wmapOff + leaf * 4;
        var w = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(waddr));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(waddr), w & bitMask);
        var paddr = pmapOff + leaf * 4;
        var pw = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(paddr));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(paddr), pw & bitMask);
        ++dmapFreed;
      }
      if (dmapFreed > 0) {
        var nfree = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan(dmapPageOff + 4));
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(dmapPageOff + 4), nfree + dmapFreed);
        totalFreed += dmapFreed;
      }
    }
    if (totalFreed > 0)
      RecomputeDmapTrees(img);
  }

  private static void RecomputeDmapTrees(byte[] image) {
    var dmapMaxes = new sbyte[2];
    var dmapsActive = 0;
    var totalFree = 0L;

    for (var dmapIdx = 0; dmapIdx < 2; dmapIdx++) {
      var dmapPageBlock = FirstDmapBlock + dmapIdx;
      var dmapPageOff = (int)((long)dmapPageBlock * BlockSize);
      if (dmapPageOff + BlockSize > image.Length) {
        dmapMaxes[dmapIdx] = Dmap_Nofree;
        continue;
      }
      var page = image.AsSpan(dmapPageOff, BlockSize);
      var nblocks = BinaryPrimitives.ReadInt32LittleEndian(page);
      if (nblocks <= 0) {
        dmapMaxes[dmapIdx] = Dmap_Nofree;
        continue;
      }
      ++dmapsActive;
      var wmapOff = 2048;
      var streeBase = 33;
      var leaves = new sbyte[Dmap_Lperdmap];
      for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++) {
        var word = BinaryPrimitives.ReadUInt32LittleEndian(page[(wmapOff + leaf * 4)..]);
        leaves[leaf] = MaxFreeStringExponent(word);
      }
      for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++)
        page[streeBase + Dmap_Leafind + leaf] = unchecked((byte)leaves[leaf]);
      for (var i = 0; i < Dmap_Leafind; i++)
        page[streeBase + i] = unchecked((byte)Dmap_Nofree);
      dmapMaxes[dmapIdx] = AdjTree(page, streeBase, Dmap_L2lperdmap, Dmap_Budmin);
      totalFree += BinaryPrimitives.ReadInt32LittleEndian(page[4..]);
    }

    var ctlOff = (int)((long)L0DmapctlBlock * BlockSize);
    var ctlPage = image.AsSpan(ctlOff, BlockSize);
    var ctlStreeBase = 17;
    for (var i = 0; i < Dmapctl_Lperctl; i++)
      ctlPage[ctlStreeBase + Dmapctl_Leafind + i] = unchecked((byte)Dmap_Nofree);
    for (var i = 0; i < dmapsActive; i++)
      ctlPage[ctlStreeBase + Dmapctl_Leafind + i] = unchecked((byte)dmapMaxes[i]);
    for (var i = 0; i < Dmapctl_Leafind; i++)
      ctlPage[ctlStreeBase + i] = unchecked((byte)Dmap_Nofree);
    var ctlMax = AdjTree(ctlPage, ctlStreeBase, Dmapctl_L2lperctl, Dmap_L2bperdmap);

    var dbOff = (int)((long)BmapBlock * BlockSize);
    var dbPage = image.AsSpan(dbOff, BlockSize);
    BinaryPrimitives.WriteInt64LittleEndian(dbPage[8..], totalFree);
    BinaryPrimitives.WriteInt64LittleEndian(dbPage[56..], totalFree);
    dbPage[1088] = unchecked((byte)ctlMax);
  }

  // ── dinode write ───────────────────────────────────────────────────────

  private static void WriteFileDinode(byte[] image, int ioff, int ino, long size, int firstBlock, int blockCount, uint timestamp) {
    var di = image.AsSpan(ioff, InodeSize);
    di.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(di[0..], InostampFixed);
    BinaryPrimitives.WriteInt32LittleEndian(di[4..], FilesetIno);
    BinaryPrimitives.WriteUInt32LittleEndian(di[8..], (uint)ino);
    BinaryPrimitives.WriteUInt32LittleEndian(di[12..], 1);
    var extentBlock = FsitBlock + (ino / InodesPerExtent) * InodeExtentBlocks;
    WritePxd(di[16..], (uint)InodeExtentBlocks, (ulong)extentBlock);
    BinaryPrimitives.WriteInt64LittleEndian(di[24..], size);
    BinaryPrimitives.WriteInt64LittleEndian(di[32..], blockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(di[40..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(di[44..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(di[48..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(di[52..], IfJournal | IfReg | 0x1A4);
    for (var t = 0; t < 4; t++) {
      BinaryPrimitives.WriteUInt32LittleEndian(di[(56 + t * 8)..], timestamp);
      BinaryPrimitives.WriteUInt32LittleEndian(di[(60 + t * 8)..], 0);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(di[120..], 2);
    BinaryPrimitives.WriteUInt32LittleEndian(di[124..], 0);

    const int XtentryStart = 2;
    var data = di[XtreeDataOffset..];
    data.Clear();
    var maxEntry = DiDataSize / 16;
    data[16] = 0x83;
    BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)(XtentryStart + 1));
    BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)maxEntry);
    WritePxd(data[24..], 0, 0);

    var xad = data.Slice(XtentryStart * 16, 16);
    xad.Clear();
    WritePxd(xad[8..], (uint)blockCount, (ulong)firstBlock);
  }

  private static void UpdateInodeTimes(byte[] image, int ioff, uint timestamp) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ioff + 72), timestamp);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ioff + 80), timestamp);
  }

  // ── xtree extent free ──────────────────────────────────────────────────

  private static void FreeFileXtreeExtents(byte[] image, int dinodeOff, ImageContext ctx) {
    var data = image.AsSpan(dinodeOff + XtreeDataOffset, DiDataSize);
    var flag = data[16];
    if ((flag & BtInternal) != 0)
      throw new NotSupportedException("Jfs: xtree non-leaf root removal — multi-week scope (file too large for inline xad slots).");

    var nextIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
    const int XtentryStart = 2;
    for (var i = XtentryStart; i < nextIdx; i++) {
      var xadOff = i * 16;
      if (xadOff + 16 > data.Length) break;
      var extLen = (int)ReadPxdLength(data.Slice(xadOff + 8, 8));
      var extAddr = (int)ReadPxdAddress(data.Slice(xadOff + 8, 8));
      if (extLen == 0 || extAddr == 0) continue;
      FreeBlocks(ctx, extAddr, extLen);
    }
  }

  // ── ujfs_adjtree (copy of JfsWriter.AdjTree) ───────────────────────────

  private static sbyte AdjTree(Span<byte> page, int streeBase, int l2leaves, int l2min) {
    var nleaves = 1 << l2leaves;
    var leafIndex = (nleaves - 1) / 3;
    var l2max = l2min + l2leaves;

    var bsize = 1;
    for (var l2free = l2min; l2free < l2max; l2free++, bsize <<= 1) {
      var nextb = bsize << 1;
      for (var idx = 0; idx < nleaves; idx += nextb) {
        var leftIdx = streeBase + leafIndex + idx;
        var rightIdx = streeBase + leafIndex + idx + bsize;
        if ((sbyte)page[leftIdx] == l2free && (sbyte)page[rightIdx] == l2free) {
          page[leftIdx] = (byte)(l2free + 1);
          page[rightIdx] = unchecked((byte)Dmap_Nofree);
        }
      }
    }

    var leaf = leafIndex;
    var numAtLevel = nleaves >> 2;
    while (numAtLevel > 0) {
      var parent = (leaf - 1) >> 2;
      for (var i = 0; i < numAtLevel; i++) {
        var c0 = (sbyte)page[streeBase + leaf + i * 4 + 0];
        var c1 = (sbyte)page[streeBase + leaf + i * 4 + 1];
        var c2 = (sbyte)page[streeBase + leaf + i * 4 + 2];
        var c3 = (sbyte)page[streeBase + leaf + i * 4 + 3];
        var max = (sbyte)Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
        page[streeBase + parent + i] = unchecked((byte)max);
      }
      numAtLevel >>= 2;
      leaf = parent;
    }

    return (sbyte)page[streeBase + 0];
  }

  private static sbyte MaxFreeStringExponent(uint word) {
    if (word == 0u) return Dmap_Budmin;
    if (word == 0xFFFFFFFFu) return Dmap_Nofree;
    var hi = (ushort)(word >> 16);
    var lo = (ushort)(word & 0xFFFF);
    if (hi == 0 || lo == 0) return Dmap_Budmin - 1;
    var b0 = BudTab[(byte)(word >> 24)];
    var b1 = BudTab[(byte)(word >> 16)];
    var b2 = BudTab[(byte)(word >> 8)];
    var b3 = BudTab[(byte)word];
    return (sbyte)Math.Max(Math.Max(b0, b1), Math.Max(b2, b3));
  }

  private static readonly sbyte[] BudTab = [
    3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, -1,
  ];
}
