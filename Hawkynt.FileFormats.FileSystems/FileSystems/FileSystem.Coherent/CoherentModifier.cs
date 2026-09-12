#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Coherent;

/// <summary>
/// In-place Coherent FS modifier — random-access Add/Remove against a Coherent
/// image emitted by <see cref="CoherentWriter"/> (or anything else with the
/// same V7-flavoured layout: 512-byte blocks, 64-byte inodes, 24-bit zone
/// pointers, magic 0xFD18 at file offset 1528).
///
/// <para><b>Free space discovery.</b> Coherent's V7-style on-disk free-list
/// uses a chained free-block list seeded from <c>s_free[]/s_nfree</c> in the
/// superblock and a free-inode cache <c>s_inode[]/s_ninode</c>. The
/// <see cref="CoherentWriter"/> intentionally leaves both caches at zero (a
/// fresh image has no free chain yet — the data area is exactly sized to the
/// committed files). The modifier therefore allocates by:
/// <list type="bullet">
///   <item>Inodes: scanning the inode table for slots whose mode is zero.</item>
///   <item>Zones: building the set of zones reachable from the inode table
///   (direct + single/double/triple indirect pointer blocks themselves and
///   their data zones) and treating any zone in [dataStart, fsize) that is
///   not reachable as free. If none free, the image is grown by extending
///   the underlying stream and bumping <c>s_fsize</c>.</item>
/// </list>
/// This matches what V7 <c>fsck</c> would reconstruct after a crash that
/// trashed the free-list caches: walk every inode, mark referenced zones,
/// rebuild the free list from the gaps.</para>
///
/// <para><b>Tier selection on Add.</b> The same tier rules the writer uses
/// are applied: ≤10 data blocks → direct; ≤10+170 → single-indirect; bigger
/// → double-indirect. Triple-indirect is not emitted (the writer never does
/// either; ~14.5 MB per file is the practical ceiling).</para>
///
/// <para><b>Wiping on Remove.</b> The freed data zones AND any freed
/// indirect/double-indirect pointer blocks are zeroed before the inode is
/// cleared. The dirent slot is cleared (inode set to zero, name bytes
/// zeroed) so no forensic recovery of the removed entry's name is possible
/// either. Trailing slack inside the file's last block is wiped via the
/// data-block zeroing.</para>
/// </summary>
public static class CoherentModifier {

  private const int BlockSize = 512;
  private const int InodeSize = 64;
  private const int InodesPerBlock = BlockSize / InodeSize; // 8
  // The coh_super_block lives at file offset 0 (and a duplicate at 512); the
  // inode table starts at block 2 (offset 1024). Coherent has no numeric magic
  // — it is recognised by the s_fname/s_fpack strings.
  private const int SuperblockOffset = 0;
  private const int SuperblockMirror = 512;
  private const int CohFnameOffset = 0x1E4;
  private const int CohFpackOffset = 0x1EA;
  private const int RootInode = 2;
  private const int DirEntrySize = 16;
  private const int MaxNameLen = 14;
  private const int DirectZones = 10;
  private const int SingleIndirectSlot = 10;
  private const int DoubleIndirectSlot = 11;
  private const int TripleIndirectSlot = 12;
  private const int PointersPerBlock = BlockSize / 3; // 170
  private const ushort ModeDirectory = 0x41ED;
  private const ushort ModeRegularFile = 0x81A4;

  private sealed class Geometry {
    public ushort Isize;
    public uint Fsize;
    public int DataStart;
    public int MaxInodeNumber;
    public long ImageSize;
  }

  // ── Public entry points ──────────────────────────────────────────────────

  /// <summary>
  /// Adds a file at root level. Replaces any existing entry with the same
  /// (truncated) leaf name. Allocates inode + zones in place; grows the
  /// underlying stream if free zones are exhausted.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) return;
    var truncated = leaf.Length > MaxNameLen ? leaf[..MaxNameLen] : leaf;

    // Replace-by-name semantics: if a previous entry with the same truncated
    // leaf already exists, remove it first so we wipe its zones cleanly.
    RemoveFile(image, truncated, wipeData: true);

    var geom = ReadGeometry(image);

    // 1) Allocate inode.
    var newInode = AllocateInode(image, geom);
    if (newInode == 0)
      throw new IOException("Coherent: no free inode slots in inode table.");

    // 2) Allocate enough zones for the payload + indirect overhead.
    var dataBlocksNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
    var (zonesForTier, useSingle, useDouble, doubleRows) = PlanTier(dataBlocksNeeded);
    var totalZonesNeeded = dataBlocksNeeded + (useSingle ? 1 : 0) + (useDouble ? 1 + doubleRows : 0);

    var allocated = AllocateZones(image, geom, totalZonesNeeded);

    // 3) Lay out the allocated zones into direct + indirect tiers.
    var direct = new uint[DirectZones];
    uint singleIndirect = 0;
    uint doubleHeader = 0;
    var doubleRowBlocks = new uint[doubleRows];
    var dataZones = new List<uint>(dataBlocksNeeded);

    var idx = 0;
    for (var i = 0; i < DirectZones && idx < dataBlocksNeeded; i++, idx++) {
      direct[i] = allocated[idx];
      dataZones.Add(allocated[idx]);
    }
    var allocIdx = idx;
    if (useSingle) {
      singleIndirect = allocated[allocIdx++];
      while (idx < Math.Min(DirectZones + PointersPerBlock, dataBlocksNeeded)) {
        dataZones.Add(allocated[allocIdx++]);
        idx++;
      }
    }
    if (useDouble) {
      doubleHeader = allocated[allocIdx++];
      for (var r = 0; r < doubleRows; r++) {
        doubleRowBlocks[r] = allocated[allocIdx++];
        var rowMax = Math.Min(PointersPerBlock, dataBlocksNeeded - idx);
        for (var p = 0; p < rowMax; p++) {
          dataZones.Add(allocated[allocIdx++]);
          idx++;
        }
      }
    }

    // 4) Write file data to data zones.
    var srcOff = 0;
    foreach (var zone in dataZones) {
      var block = new byte[BlockSize];
      var copy = Math.Min(BlockSize, data.Length - srcOff);
      if (copy > 0) Array.Copy(data, srcOff, block, 0, copy);
      WriteAt(image, (long)zone * BlockSize, block);
      srcOff += copy;
    }

    // 5) Write single-indirect block pointers.
    if (useSingle) {
      var block = new byte[BlockSize];
      var singleCovers = Math.Min(PointersPerBlock, dataBlocksNeeded - DirectZones);
      for (var p = 0; p < singleCovers; p++)
        Write24(block.AsSpan(p * 3), dataZones[DirectZones + p]);
      WriteAt(image, (long)singleIndirect * BlockSize, block);
    }

    // 6) Write double-indirect header + per-row indirect blocks.
    if (useDouble) {
      var header = new byte[BlockSize];
      for (var r = 0; r < doubleRows; r++)
        Write24(header.AsSpan(r * 3), doubleRowBlocks[r]);
      WriteAt(image, (long)doubleHeader * BlockSize, header);

      var consumed = (useSingle ? Math.Min(PointersPerBlock, dataBlocksNeeded - DirectZones) : 0);
      var spilled = dataBlocksNeeded - DirectZones - consumed;
      var dataZoneOff = DirectZones + consumed;
      for (var r = 0; r < doubleRows; r++) {
        var rowBlock = new byte[BlockSize];
        var rowCovers = Math.Min(PointersPerBlock, spilled);
        for (var p = 0; p < rowCovers; p++)
          Write24(rowBlock.AsSpan(p * 3), dataZones[dataZoneOff + p]);
        WriteAt(image, (long)doubleRowBlocks[r] * BlockSize, rowBlock);
        spilled -= rowCovers;
        dataZoneOff += rowCovers;
      }
    }

    // 7) Write the new inode.
    WriteFileInode(image, newInode, (uint)data.Length, direct, singleIndirect, doubleHeader);

    // 8) Append dirent to root directory.
    AddRootDirent(image, newInode, truncated);
  }

  /// <summary>
  /// Removes a file by name from the root directory. Frees all referenced
  /// zones (direct + indirect pointer blocks + their data blocks), zeros
  /// the inode slot, clears the dirent. Returns false if the entry is not
  /// found or refers to a directory.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);
    var truncated = name.Length > MaxNameLen ? name[..MaxNameLen] : name;

    // 1) Read root directory bytes.
    var rootInodeBytes = ReadInode(image, RootInode);
    var (_, rootSize, rootZones, _, _, _) = ParseInode(rootInodeBytes);
    var rootData = ReadFileData(image, rootZones, rootSize);

    // 2) Find the dirent.
    var dirZones = CollectFileZones(image, rootInodeBytes);
    var found = false;
    uint targetInode = 0;
    for (var off = 0; off + DirEntrySize <= rootData.Length; off += DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(rootData.AsSpan(off));
      if (ino == 0) continue;
      var entryName = ReadNullTermAscii(rootData, off + 2, MaxNameLen);
      if (!entryName.Equals(truncated, StringComparison.OrdinalIgnoreCase)) continue;
      targetInode = ino;
      // Wipe the entry slot in our local buffer.
      Array.Clear(rootData, off, DirEntrySize);
      found = true;
      break;
    }
    if (!found) return false;

    // 3) Read target inode to discover its on-disk footprint.
    var targetInodeBytes = ReadInode(image, (int)targetInode);
    var (mode, _, _, _, _, _) = ParseInode(targetInodeBytes);
    if ((mode & 0xF000) == 0x4000) return false; // refuse to remove directories

    // 4) Persist the cleared root directory bytes back to its zones.
    WriteToZones(image, dirZones, rootData);

    // 5) Free + wipe target inode zones.
    FreeInodeZones(image, targetInodeBytes, wipeData);

    // 6) Zero the inode slot.
    WriteInodeRaw(image, (int)targetInode, new byte[InodeSize]);

    return true;
  }

  // ── Geometry / superblock ────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    if (image.Length < 1024 + InodeSize)
      throw new InvalidDataException("Coherent: image too small for superblock.");
    var sb = new byte[BlockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    var fname = Encoding.ASCII.GetString(sb.AsSpan(CohFnameOffset, 6));
    var fpack = Encoding.ASCII.GetString(sb.AsSpan(CohFpackOffset, 6));
    if (fname is not ("noname" or "xxxxx ") || fpack is not ("nopack" or "xxxxx\n"))
      throw new InvalidDataException(
        $"Coherent: not a coh_super_block (s_fname='{fname}', s_fpack='{fpack}').");

    // s_isize (LE u16) is the first data zone; the inode list occupies blocks
    // 2..s_isize-1. s_fsize (PDP-32) is the total zone count.
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0, 2));
    var fsize = ReadPdp32(sb.AsSpan(2, 4));
    var dataStart = isize;
    var maxInode = Math.Max(RootInode, (isize - 2) * InodesPerBlock);
    return new Geometry {
      Isize = isize,
      Fsize = fsize,
      DataStart = dataStart,
      MaxInodeNumber = maxInode,
      ImageSize = image.Length,
    };
  }

  private static void WriteFsize(Stream image, uint newFsize) {
    var buf = new byte[4];
    WritePdp32(buf, newFsize);
    // Update both superblock copies (offset 0 and the mirror at 512).
    image.Position = SuperblockOffset + 2;
    image.Write(buf, 0, 4);
    image.Position = SuperblockMirror + 2;
    image.Write(buf, 0, 4);
  }

  // ── Inode I/O ─────────────────────────────────────────────────────────────

  private static byte[] ReadInode(Stream image, int inum) {
    var buf = new byte[InodeSize];
    image.Position = 2 * BlockSize + (long)(inum - 1) * InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInodeRaw(Stream image, int inum, byte[] data) {
    image.Position = 2 * BlockSize + (long)(inum - 1) * InodeSize;
    image.Write(data, 0, InodeSize);
  }

  private static void WriteFileInode(Stream image, int inum, uint size,
      uint[] direct, uint singleIndirect, uint doubleIndirectHeader) {
    var buf = new byte[InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), ModeRegularFile);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), 1);     // i_nlink
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), 0);     // i_uid
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), 0);     // i_gid
    WritePdp32(buf.AsSpan(8, 4), size);                                // i_size (PDP-32)
    for (var i = 0; i < DirectZones; i++)
      Write24(buf.AsSpan(12 + i * 3, 3), direct[i]);
    Write24(buf.AsSpan(12 + SingleIndirectSlot * 3, 3), singleIndirect);
    Write24(buf.AsSpan(12 + DoubleIndirectSlot * 3, 3), doubleIndirectHeader);
    WriteInodeRaw(image, inum, buf);
  }

  private static (ushort mode, uint size, uint[] direct, uint single, uint dbl, uint trip)
      ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0, 2));
    var size = ReadPdp32(inode.AsSpan(8, 4));
    var direct = new uint[DirectZones];
    for (var i = 0; i < DirectZones; i++)
      direct[i] = Read24(inode.AsSpan(12 + i * 3, 3));
    var single = Read24(inode.AsSpan(12 + SingleIndirectSlot * 3, 3));
    var dbl = Read24(inode.AsSpan(12 + DoubleIndirectSlot * 3, 3));
    var trip = Read24(inode.AsSpan(12 + TripleIndirectSlot * 3, 3));
    return (mode, size, direct, single, dbl, trip);
  }

  // ── Inode allocation ──────────────────────────────────────────────────────

  /// <summary>
  /// Scans the inode table for a slot with mode==0 (free) and returns its
  /// 1-based inode number, or 0 if the inode table is full.
  ///
  /// <para><b>Reserved-inode quirk.</b> The Coherent superblock aliases the
  /// same 512-byte block as the start of the inode list: SB fields live at
  /// byte offsets 0-7 (s_isize/s_fsize), 408-409 (s_ninode), 496-499 (time),
  /// 504-505 (magic 0xFD18). Any inode whose 64-byte slot intersects one of
  /// those fields is unsafe to write (it would clobber the SB). Inodes 1,
  /// 7, 8 are reserved in the first ilist block (isize=1). Inode 2 is
  /// always the root. Anything else is fair game.</para>
  /// </summary>
  private static int AllocateInode(Stream image, Geometry geom) {
    for (var ino = 3; ino <= geom.MaxInodeNumber; ino++) {
      if (InodeOverlapsSuperblockFields(ino)) continue;
      var buf = ReadInode(image, ino);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
      if (mode == 0) return ino;
    }
    return 0;
  }

  /// <summary>
  /// Returns true when inode <paramref name="inum"/>'s 64-byte slot
  /// intersects any of the V7-flavoured Coherent superblock fields the WORM
  /// writer relies on. Used to keep the modifier from clobbering the
  /// superblock through the aliased inode-list block (which contains the
  /// magic at offset 504, the size/freecount/time fields at 0..7, 408, 496).
  /// </summary>
  private static bool InodeOverlapsSuperblockFields(int inum) {
    // In the genuine Coherent layout the superblock occupies blocks 0 and 1
    // (file offsets 0 and 512) and the inode table starts at block 2 (offset
    // 1024), so no inode slot aliases superblock bytes. Inode 1 is reserved by
    // convention (callers allocate from inode 3 and enumerate from inode 2).
    _ = inum;
    return false;
  }

  // ── Zone allocation ───────────────────────────────────────────────────────

  /// <summary>
  /// Builds the set of zones currently referenced by any non-free inode
  /// (direct, indirect, double-indirect, triple-indirect — including the
  /// indirect pointer blocks themselves).
  /// </summary>
  private static HashSet<uint> CollectAllReferencedZones(Stream image, Geometry geom) {
    var referenced = new HashSet<uint>();
    for (var ino = RootInode; ino <= geom.MaxInodeNumber; ino++) {
      // SB-aliased inode slots in block 2 contain superblock bytes, not a
      // real inode — reading them would yield nonsensical mode/zone fields
      // and pollute the referenced-set with bogus block numbers.
      if (InodeOverlapsSuperblockFields(ino)) continue;
      var buf = ReadInode(image, ino);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
      if (mode == 0) continue;
      foreach (var z in CollectFileZones(image, buf))
        referenced.Add(z);
    }
    return referenced;
  }

  /// <summary>
  /// Returns every zone the file/directory occupies on disk: data zones plus
  /// any indirect pointer blocks. Walks 1/2/3-level indirection.
  /// </summary>
  private static List<uint> CollectFileZones(Stream image, byte[] inodeBytes) {
    var zones = new List<uint>();
    var (_, _, direct, single, dbl, trip) = ParseInode(inodeBytes);

    foreach (var z in direct) if (z != 0) zones.Add(z);

    if (single != 0) {
      zones.Add(single);
      foreach (var z in ReadIndirectPointers(image, single)) if (z != 0) zones.Add(z);
    }

    if (dbl != 0) {
      zones.Add(dbl);
      foreach (var rowBlock in ReadIndirectPointers(image, dbl)) {
        if (rowBlock == 0) continue;
        zones.Add(rowBlock);
        foreach (var z in ReadIndirectPointers(image, rowBlock)) if (z != 0) zones.Add(z);
      }
    }

    if (trip != 0) {
      zones.Add(trip);
      foreach (var l2 in ReadIndirectPointers(image, trip)) {
        if (l2 == 0) continue;
        zones.Add(l2);
        foreach (var l1 in ReadIndirectPointers(image, l2)) {
          if (l1 == 0) continue;
          zones.Add(l1);
          foreach (var z in ReadIndirectPointers(image, l1)) if (z != 0) zones.Add(z);
        }
      }
    }

    return zones;
  }

  private static uint[] ReadIndirectPointers(Stream image, uint block) {
    var buf = new byte[BlockSize];
    var offset = (long)block * BlockSize;
    if (offset + BlockSize > image.Length) return [];
    image.Position = offset;
    image.ReadExactly(buf);
    var result = new uint[PointersPerBlock];
    for (var i = 0; i < PointersPerBlock; i++)
      result[i] = Read24(buf.AsSpan(i * 3, 3));
    return result;
  }

  /// <summary>
  /// Allocates <paramref name="count"/> data zones, returning their block
  /// numbers. Reuses free (unreferenced) zones inside [dataStart, fsize)
  /// first; grows the image past <c>s_fsize</c> if more are needed.
  /// </summary>
  private static uint[] AllocateZones(Stream image, Geometry geom, int count) {
    if (count == 0) return [];
    var referenced = CollectAllReferencedZones(image, geom);
    var result = new List<uint>(count);

    // Pass 1: scavenge existing free zones inside the image.
    for (var z = (uint)geom.DataStart; z < geom.Fsize && result.Count < count; z++) {
      if (referenced.Contains(z)) continue;
      result.Add(z);
    }

    // Pass 2: extend the image.
    if (result.Count < count) {
      var newFsize = geom.Fsize;
      while (result.Count < count) {
        result.Add(newFsize);
        newFsize++;
      }
      var newSize = (long)newFsize * BlockSize;
      if (image.Length < newSize)
        image.SetLength(newSize);
      WriteFsize(image, newFsize);
    }

    return [.. result];
  }

  // ── File-data I/O via inode zones ────────────────────────────────────────

  private static byte[] ReadFileData(Stream image, uint[] direct, uint sizeBytes) {
    using var ms = new MemoryStream();
    long remaining = sizeBytes;
    for (var i = 0; i < DirectZones && remaining > 0; i++) {
      if (direct[i] == 0) break;
      AppendBlock(image, ms, direct[i], ref remaining);
    }
    return ms.ToArray();
  }

  private static void AppendBlock(Stream image, MemoryStream ms, uint block, ref long remaining) {
    var offset = (long)block * BlockSize;
    if (offset + BlockSize > image.Length) return;
    image.Position = offset;
    var buf = new byte[BlockSize];
    image.ReadExactly(buf);
    var toRead = (int)Math.Min(remaining, BlockSize);
    ms.Write(buf, 0, toRead);
    remaining -= toRead;
  }

  private static void WriteToZones(Stream image, List<uint> zones, byte[] data) {
    // Used only for the root directory (which currently fits in direct
    // zones in every practical case — root dirent count × 16 bytes).
    var written = 0;
    foreach (var z in zones) {
      // Only write to data-bearing zones — skip indirect pointer blocks,
      // identified by being present in CollectFileZones twice (once as
      // pointer block, once each as data block underneath). The simple
      // contract for the root dir is: it sits in direct zones, so this
      // path is exercised only with data blocks. If a future enhancement
      // pushes the root past 5KB this needs splitting between data zones
      // and indirect pointer blocks — we leave that as a documented
      // limitation rather than mis-write.
      var block = new byte[BlockSize];
      var copy = Math.Min(BlockSize, data.Length - written);
      if (copy > 0) Array.Copy(data, written, block, 0, copy);
      WriteAt(image, (long)z * BlockSize, block);
      written += copy;
      if (written >= data.Length) break;
    }
  }

  /// <summary>
  /// Zeros every zone the file references (data + indirect pointer blocks).
  /// We must wipe pointer blocks too — those can leak the offsets of the
  /// freed data zones, which an attacker can correlate with the wiped data
  /// area boundaries to reconstruct partial content. Belt-and-braces wipe
  /// is fast at 512 bytes per block.
  /// </summary>
  private static void FreeInodeZones(Stream image, byte[] inodeBytes, bool wipe) {
    if (!wipe) return;
    var zeros = new byte[BlockSize];
    foreach (var z in CollectFileZones(image, inodeBytes)) {
      if (z == 0) continue;
      if ((long)z * BlockSize + BlockSize > image.Length) continue;
      WriteAt(image, (long)z * BlockSize, zeros);
    }
  }

  // ── Root directory dirent management ────────────────────────────────────

  /// <summary>
  /// Appends a 16-byte dirent to the root directory, growing the root's
  /// size by 16 bytes. Allocates a new data zone if the existing zones are
  /// exhausted.
  /// </summary>
  private static void AddRootDirent(Stream image, int inum, string name) {
    var geom = ReadGeometry(image);
    var rootBytes = ReadInode(image, RootInode);
    var (mode, size, direct, single, dbl, _) = ParseInode(rootBytes);

    // Build the new dirent.
    var dirent = new byte[DirEntrySize];
    BinaryPrimitives.WriteUInt16LittleEndian(dirent.AsSpan(0, 2), (ushort)inum);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLen);
    Array.Copy(nameBytes, 0, dirent, 2, copyLen);

    // Reuse a zeroed slot in the existing root dir if there is one — Remove
    // leaves holes.
    var data = ReadFileData(image, direct, size);
    var reusedSlot = -1;
    for (var off = 0; off + DirEntrySize <= data.Length; off += DirEntrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off));
      if (ino == 0) { reusedSlot = off; break; }
    }
    if (reusedSlot >= 0) {
      Array.Copy(dirent, 0, data, reusedSlot, DirEntrySize);
      WriteRootDataDirectZones(image, direct, data, sizeUnchanged: true, currentSize: size);
      return;
    }

    // Otherwise append at end. Check if it fits in the existing tail block.
    var newSize = size + DirEntrySize;
    var currentBlocks = (size + BlockSize - 1) / BlockSize;
    var requiredBlocks = (newSize + BlockSize - 1) / BlockSize;

    if (requiredBlocks > DirectZones)
      throw new IOException("Coherent: root directory grew past direct-zone capacity "
        + $"({DirectZones} blocks = {DirectZones * BlockSize / DirEntrySize} entries). "
        + "Add an indirect-zone path for the root if you need more entries.");

    // Allocate any new data zones we now need.
    var needed = (int)(requiredBlocks - currentBlocks);
    if (needed > 0) {
      var newZones = AllocateZones(image, geom, needed);
      for (int i = 0, slot = (int)currentBlocks; i < newZones.Length && slot < DirectZones; i++, slot++)
        direct[slot] = newZones[i];
    }

    // Re-read geom in case AllocateZones extended fsize.
    geom = ReadGeometry(image);

    // Append the dirent bytes.
    var newData = new byte[newSize];
    Array.Copy(data, 0, newData, 0, data.Length);
    Array.Copy(dirent, 0, newData, (int)size, DirEntrySize);

    // Write the whole root payload back.
    WriteRootDataDirectZones(image, direct, newData, sizeUnchanged: false, currentSize: 0);

    // Update root inode's size + zone pointers.
    WritePdp32(rootBytes.AsSpan(8, 4), newSize);
    for (var i = 0; i < DirectZones; i++)
      Write24(rootBytes.AsSpan(12 + i * 3, 3), direct[i]);
    WriteInodeRaw(image, RootInode, rootBytes);

    _ = mode; _ = single; _ = dbl; // suppress unused-pattern warning
  }

  private static void WriteRootDataDirectZones(Stream image, uint[] direct, byte[] data,
      bool sizeUnchanged, uint currentSize) {
    var off = 0;
    for (var i = 0; i < DirectZones && off < data.Length; i++) {
      if (direct[i] == 0) break;
      var block = new byte[BlockSize];
      var copy = Math.Min(BlockSize, data.Length - off);
      Array.Copy(data, off, block, 0, copy);
      WriteAt(image, (long)direct[i] * BlockSize, block);
      off += copy;
    }
    _ = sizeUnchanged; _ = currentSize;
  }

  // ── Plan: which tier covers <data-blocks> data blocks ─────────────────────

  private static (int zonesForTier, bool useSingle, bool useDouble, int doubleRows)
      PlanTier(int dataBlocks) {
    if (dataBlocks <= DirectZones) return (dataBlocks, false, false, 0);
    if (dataBlocks <= DirectZones + PointersPerBlock) return (dataBlocks, true, false, 0);
    var spilled = dataBlocks - DirectZones - PointersPerBlock;
    var rows = (spilled + PointersPerBlock - 1) / PointersPerBlock;
    return (dataBlocks, true, true, rows);
  }

  // ── Tiny utilities ────────────────────────────────────────────────────────

  // PDP-11 3-byte zone address: disk [d0,d1,d2] → block d1 | d2<<8 | d0<<16.
  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[1] | ((uint)s[2] << 8) | ((uint)s[0] << 16);

  private static void Write24(Span<byte> dest, uint value) {
    dest[0] = (byte)((value >> 16) & 0xFF);
    dest[1] = (byte)(value & 0xFF);
    dest[2] = (byte)((value >> 8) & 0xFF);
  }

  // PDP-11 middle-endian 32-bit: high 16-bit half first, each half LE.
  private static uint ReadPdp32(ReadOnlySpan<byte> s) =>
    s[2] | ((uint)s[3] << 8) | ((uint)s[0] << 16) | ((uint)s[1] << 24);

  private static void WritePdp32(Span<byte> dest, uint value) {
    dest[0] = (byte)((value >> 16) & 0xFF);
    dest[1] = (byte)((value >> 24) & 0xFF);
    dest[2] = (byte)(value & 0xFF);
    dest[3] = (byte)((value >> 8) & 0xFF);
  }

  private static void WriteAt(Stream image, long offset, byte[] data) {
    if (offset + data.Length > image.Length)
      image.SetLength(offset + data.Length);
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  private static string ReadNullTermAscii(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }
}
