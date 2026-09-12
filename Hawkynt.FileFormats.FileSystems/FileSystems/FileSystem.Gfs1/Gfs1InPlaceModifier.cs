#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Gfs1;

/// <summary>
/// Genuine in-place R/W mutation for Sistina GFS1 images produced by
/// <see cref="Gfs1Writer"/>. All untouched blocks (existing inodes, existing
/// file data, the superblock fields we don't change) stay byte-identical at
/// their original offsets; only the changed region plus the relevant counters
/// are written.
/// </summary>
/// <remarks>
/// <para>Layout recap (big-endian, 4096-byte blocks, 256-byte inodes):</para>
/// <list type="bullet">
///   <item>block 16 = superblock at byte 65536; <c>sb_isize_blocks</c>@0x48,
///     <c>sb_size</c>@0x50.</item>
///   <item>block 17.. = inode table; 16 inodes per block. Inode N (>=2) lives at
///     block <c>17 + (N-2)/16</c>, slot <c>(N-2)%16</c>.</item>
///   <item>data blocks follow the inode table.</item>
///   <item>inode fields: <c>no_addr</c>@24 (u64 = first data block), <c>mode</c>@40
///     (dir if <c>(mode&amp;0xF000)==0x4000</c>), <c>nlink</c>@52, <c>di_size</c>@56
///     (u64), <c>di_blocks</c>@64 (u64).</item>
///   <item>dir body block: <c>u16 magic 0xDEAD</c>@0, <c>u16 slotcount</c>@2, then
///     entries from off 4: <c>u32 inode</c> + <c>u8 nameLen</c> + name.</item>
/// </list>
/// <para><b>Add</b> allocates a fresh inode number from a free slot inside the
/// existing inode-block region (falls back to rebuild if a new inode block
/// would be needed — that would shift data), appends contiguous data blocks at
/// the image end, writes the inode, adds a dir entry to the parent dir block,
/// and bumps <c>sb_size</c>. <b>Replace</b> overwrites the extent in place when
/// the new block count fits the old run, else appends a fresh run at image end.
/// <b>Remove</b> zeroes the inode slot + data run and rewrites the parent dir
/// block with the surviving entries. Both root files and one level of nested
/// directories are handled in place; anything outside scope falls back to
/// <see cref="Gfs1FormatDescriptor"/>'s rebuild delegate.</para>
/// </remarks>
internal static class Gfs1InPlaceModifier {

  private const int BlockSize = Gfs1Writer.BlockSize;       // 4096
  private const int InodeSize = Gfs1Writer.InodeSize;       // 256
  private const int InodesPerBlock = Gfs1Writer.InodesPerBlock; // 16
  private const int SbOffset = Gfs1Writer.SuperblockOffset; // 65536
  private const int SbBlock = SbOffset / BlockSize;         // 16
  private const int InodeStart = SbBlock + 1;               // 17
  private const int SbIsizeBlocks = SbOffset + 0x48;
  private const int SbSize = SbOffset + 0x50;
  private const uint MhMagic = Gfs1Superblock.MhMagicConst;
  private const ushort DirMagic = 0xDEAD;
  private const ushort ModeDir = 0x4000 | 0x1ED;
  private const ushort ModeFile = 0x8000 | 0x1A4;
  private const int RootInode = 2;

  // ── Public entry points ────────────────────────────────────────────

  public static void Add(
    Stream archive,
    IReadOnlyList<ArchiveInputInfo> inputs,
    Action<Stream, IReadOnlyList<ArchiveInputInfo>> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(rebuild);

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      payloads.Add((name.Replace('\\', '/').Trim('/'), data));
    if (payloads.Count == 0) return;

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryAddInPlace(ref image, payloads)) {
      rebuild(archive, inputs);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  public static void Remove(
    Stream archive,
    string[] entryNames,
    Action<Stream, string[]> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ArgumentNullException.ThrowIfNull(rebuild);
    if (entryNames.Length == 0) return;

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryRemoveInPlace(image, entryNames)) {
      rebuild(archive, entryNames);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  // ── Sanity / accessors ─────────────────────────────────────────────

  private static bool IsValid(byte[] image) {
    if (image.Length < SbOffset + BlockSize) return false;
    if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(SbOffset)) != MhMagic) return false;
    var bsize = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(SbOffset + 0x44));
    return bsize == BlockSize;
  }

  private static int InodeBlocks(byte[] image)
    => (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(SbIsizeBlocks));

  private static int InodeOffset(int inode) {
    var blockOff = (inode - 2) / InodesPerBlock;
    var slotOff = (inode - 2) % InodesPerBlock;
    return (InodeStart + blockOff) * BlockSize + slotOff * InodeSize;
  }

  private static bool InodeUsed(byte[] image, int inode) {
    var off = InodeOffset(inode);
    if (off + InodeSize > image.Length) return false;
    return BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(off)) == MhMagic;
  }

  private static bool IsDir(byte[] image, int inode) {
    var off = InodeOffset(inode);
    var mode = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(off + 40));
    return (mode & 0xF000) == 0x4000;
  }

  private static long InodeNoAddr(byte[] image, int inode)
    => (long)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(InodeOffset(inode) + 24));

  private static long InodeBlocksCount(byte[] image, int inode)
    => (long)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(InodeOffset(inode) + 64));

  /// <summary>Finds the first free inode number that lives inside the existing
  /// inode-block region. Returns false (forces rebuild) if all slots are used.</summary>
  private static bool TryAllocInode(byte[] image, out int inode) {
    inode = 0;
    var capacity = InodeBlocks(image) * InodesPerBlock; // total inode slots
    for (var n = 2; n < capacity + 2; n++) {
      if (!InodeUsed(image, n)) { inode = n; return true; }
    }
    return false;
  }

  // ── Dir-block parsing / writing ─────────────────────────────────────

  private readonly record struct DirEntry(int Inode, string Name);

  private static List<DirEntry> ReadDir(byte[] image, int dirBlock, out bool ok) {
    ok = false;
    var entries = new List<DirEntry>();
    var off = dirBlock * BlockSize;
    if (off < 0 || off + 4 > image.Length) return entries;
    if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(off)) != DirMagic) return entries;
    int slots = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(off + 2));
    var cur = off + 4;
    for (var i = 0; i < slots && cur + 5 <= off + BlockSize; i++) {
      var ino = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(cur));
      int nlen = image[cur + 4];
      if (cur + 5 + nlen > off + BlockSize) return entries;
      var name = Encoding.UTF8.GetString(image, cur + 5, nlen);
      cur += 5 + nlen;
      entries.Add(new DirEntry(ino, name));
    }
    ok = true;
    return entries;
  }

  /// <summary>Computes the byte length a dir block needs to hold the given entries.</summary>
  private static int DirByteLength(IEnumerable<DirEntry> entries) {
    var len = 4;
    foreach (var e in entries)
      len += 5 + Encoding.UTF8.GetByteCount(e.Name);
    return len;
  }

  /// <summary>Rewrites a directory body block in place from the supplied entry
  /// list. Returns false if it would overflow a single block.</summary>
  private static bool WriteDir(byte[] image, int dirBlock, List<DirEntry> entries) {
    if (DirByteLength(entries) > BlockSize) return false;
    var off = dirBlock * BlockSize;
    image.AsSpan(off, BlockSize).Clear();
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off), DirMagic);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 2), (ushort)entries.Count);
    var cur = off + 4;
    foreach (var e in entries) {
      var nb = Encoding.UTF8.GetBytes(e.Name);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(cur), (uint)e.Inode);
      image[cur + 4] = (byte)nb.Length;
      nb.CopyTo(image.AsSpan(cur + 5));
      cur += 5 + nb.Length;
    }
    return true;
  }

  /// <summary>Sets a directory inode's di_size to match its dir-body entry list
  /// (mirrors Gfs1Writer.ComputeDirSize).</summary>
  private static void UpdateDirSize(byte[] image, int inode, List<DirEntry> entries) {
    ulong size = 0;
    foreach (var e in entries) {
      if (e.Name == ".") { size += 4 + 1 + 1; continue; }
      if (e.Name == "..") { size += 4 + 1 + 2; continue; }
      size += (ulong)(4 + 1 + Encoding.UTF8.GetByteCount(e.Name));
    }
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(InodeOffset(inode) + 56), size);
  }

  // ── Inode writer ────────────────────────────────────────────────────

  private static void WriteFileInode(byte[] image, int inode, int firstBlock, long blockCount, long fileSize) {
    var off = InodeOffset(inode);
    image.AsSpan(off, InodeSize).Clear();
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off), MhMagic);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 4), 4); // GFS_METATYPE_DI
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 24), (ulong)firstBlock); // no_addr
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 32), (ulong)inode);      // no_formal_ino
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 40), ModeFile);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 52), 1); // nlink
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 56), (ulong)fileSize);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 64), (ulong)blockCount);
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 72), now);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 80), now);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 88), now);
  }

  private static void WriteDirInode(byte[] image, int inode, int parentInode, int dirBlock) {
    var off = InodeOffset(inode);
    image.AsSpan(off, InodeSize).Clear();
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off), MhMagic);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 4), 4);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 24), (ulong)dirBlock); // no_addr
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 32), (ulong)inode);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 40), ModeDir);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 52), 2); // nlink
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 64), 1); // di_blocks
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 72), now);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 80), now);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 88), now);
  }

  // ── Image growth ────────────────────────────────────────────────────

  /// <summary>Grows the image by <paramref name="blocks"/> blocks at the end,
  /// bumps sb_size, and returns the first new block number.</summary>
  private static int AppendBlocks(ref byte[] image, int blocks) {
    var firstBlock = image.Length / BlockSize;
    var grown = new byte[image.Length + (long)blocks * BlockSize];
    Array.Copy(image, grown, image.Length);
    image = grown;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(SbSize), (uint)(image.Length / BlockSize));
    return firstBlock;
  }

  // ── Add ─────────────────────────────────────────────────────────────

  private static bool TryAddInPlace(ref byte[] image, List<(string Name, byte[] Data)> payloads) {
    if (!IsValid(image)) return false;

    foreach (var (name, data) in payloads) {
      var segs = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length == 0) return false;
      if (segs.Length > 2) return false; // only root or one nesting level in place

      // Resolve (or create) the parent directory inode + its dir block.
      int parentInode, parentDirBlock;
      if (segs.Length == 1) {
        parentInode = RootInode;
        parentDirBlock = (int)InodeNoAddr(image, RootInode);
      } else {
        if (!TryResolveOrCreateSubdir(ref image, segs[0], out parentInode, out parentDirBlock))
          return false;
      }

      var leaf = segs[^1];
      var parentEntries = ReadDir(image, parentDirBlock, out var okDir);
      if (!okDir) return false;

      var existingIdx = parentEntries.FindIndex(e =>
        string.Equals(e.Name, leaf, StringComparison.OrdinalIgnoreCase) && e.Name is not ("." or ".."));

      if (existingIdx >= 0) {
        var ino = parentEntries[existingIdx].Inode;
        if (IsDir(image, ino)) return false; // replacing a dir with a file: out of scope
        if (!TryReplaceFileBytes(ref image, ino, data)) return false;
        continue;
      }

      // New file inode.
      if (!TryAllocInode(image, out var newInode)) return false;
      // Reserve the slot immediately so a second payload doesn't reuse it.
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(InodeOffset(newInode)), MhMagic);

      var blockCount = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
      var firstBlock = 0;
      if (blockCount > 0) {
        firstBlock = AppendBlocks(ref image, blockCount);
        data.CopyTo(image.AsSpan(firstBlock * BlockSize));
      }
      WriteFileInode(image, newInode, firstBlock, blockCount, data.Length);

      parentEntries.Add(new DirEntry(newInode, leaf));
      if (!WriteDir(image, parentDirBlock, parentEntries)) return false;
      UpdateDirSize(image, parentInode, parentEntries);
    }

    return true;
  }

  /// <summary>Finds an existing subdir entry under root, or creates a brand-new
  /// subdir inode + dir block. Returns its inode + dir-body block.</summary>
  private static bool TryResolveOrCreateSubdir(ref byte[] image, string dirName, out int dirInode, out int dirBlock) {
    dirInode = 0; dirBlock = 0;
    var rootBlock = (int)InodeNoAddr(image, RootInode);
    var rootEntries = ReadDir(image, rootBlock, out var ok);
    if (!ok) return false;

    var idx = rootEntries.FindIndex(e =>
      string.Equals(e.Name, dirName, StringComparison.OrdinalIgnoreCase) && e.Name is not ("." or ".."));
    if (idx >= 0) {
      var ino = rootEntries[idx].Inode;
      if (!IsDir(image, ino)) return false; // name taken by a file
      dirInode = ino;
      dirBlock = (int)InodeNoAddr(image, ino);
      return true;
    }

    // Create the subdir: alloc inode, append a dir-body block, seed "." / "..".
    if (!TryAllocInode(image, out var newDirInode)) return false;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(InodeOffset(newDirInode)), MhMagic);
    var newDirBlock = AppendBlocks(ref image, 1);
    var dirEntries = new List<DirEntry> {
      new(newDirInode, "."),
      new(RootInode, ".."),
    };
    if (!WriteDir(image, newDirBlock, dirEntries)) return false;
    WriteDirInode(image, newDirInode, RootInode, newDirBlock);
    UpdateDirSize(image, newDirInode, dirEntries);

    // Link into root.
    rootEntries.Add(new DirEntry(newDirInode, dirName));
    if (!WriteDir(image, rootBlock, rootEntries)) return false;
    UpdateDirSize(image, RootInode, rootEntries);

    dirInode = newDirInode;
    dirBlock = newDirBlock;
    return true;
  }

  /// <summary>Replaces a file inode's data: overwrite in place when the new
  /// block count fits the old run, else append a fresh run at image end.</summary>
  private static bool TryReplaceFileBytes(ref byte[] image, int inode, byte[] data) {
    var off = InodeOffset(inode);
    var oldFirst = (int)InodeNoAddr(image, inode);
    var oldBlocks = (int)InodeBlocksCount(image, inode);
    var newBlocks = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;

    if (newBlocks <= oldBlocks && oldFirst > 0) {
      // Fits — overwrite extent in place, zero the slack.
      var runOff = oldFirst * BlockSize;
      if (runOff + (long)oldBlocks * BlockSize > image.Length) return false;
      image.AsSpan(runOff, oldBlocks * BlockSize).Clear();
      if (data.Length > 0) data.CopyTo(image.AsSpan(runOff));
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 56), (ulong)data.Length);
      // di_blocks stays oldBlocks (slack retained, fine) — but keep no_addr.
      return true;
    }

    if (newBlocks == 0) {
      // Shrink to empty.
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 24), 0);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 56), 0);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 64), 0);
      return true;
    }

    // Doesn't fit — append a fresh run; old run becomes dead space.
    var firstBlock = AppendBlocks(ref image, newBlocks);
    data.CopyTo(image.AsSpan(firstBlock * BlockSize));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 24), (ulong)firstBlock);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 56), (ulong)data.Length);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(off + 64), (ulong)newBlocks);
    return true;
  }

  // ── Remove ──────────────────────────────────────────────────────────

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames) {
    if (!IsValid(image)) return false;

    var names = entryNames
      .Select(n => n.Replace('\\', '/').Trim('/'))
      .Where(n => n.Length > 0)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
    if (names.Count == 0) return true;

    foreach (var name in names) {
      var segs = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length is 0 or > 2) return false;

      int parentInode, parentDirBlock;
      if (segs.Length == 1) {
        parentInode = RootInode;
        parentDirBlock = (int)InodeNoAddr(image, RootInode);
      } else {
        var rootBlock = (int)InodeNoAddr(image, RootInode);
        var rootEntries = ReadDir(image, rootBlock, out var okR);
        if (!okR) return false;
        var di = rootEntries.FindIndex(e =>
          string.Equals(e.Name, segs[0], StringComparison.OrdinalIgnoreCase) && e.Name is not ("." or ".."));
        if (di < 0) return false; // parent dir absent — nothing to remove cleanly
        var dino = rootEntries[di].Inode;
        if (!IsDir(image, dino)) return false;
        parentInode = dino;
        parentDirBlock = (int)InodeNoAddr(image, dino);
      }

      var entries = ReadDir(image, parentDirBlock, out var ok);
      if (!ok) return false;
      var idx = entries.FindIndex(e =>
        string.Equals(e.Name, segs[^1], StringComparison.OrdinalIgnoreCase) && e.Name is not ("." or ".."));
      if (idx < 0) continue; // already gone — clean no-op for this name

      var victim = entries[idx].Inode;
      // Free the victim's data run + inode slot.
      FreeInode(image, victim);
      entries.RemoveAt(idx);
      if (!WriteDir(image, parentDirBlock, entries)) return false;
      UpdateDirSize(image, parentInode, entries);
    }

    return true;
  }

  private static void FreeInode(byte[] image, int inode) {
    if (!InodeUsed(image, inode)) return;
    if (!IsDir(image, inode)) {
      var first = (int)InodeNoAddr(image, inode);
      var blocks = (int)InodeBlocksCount(image, inode);
      if (first > 0 && blocks > 0) {
        var runOff = first * BlockSize;
        if (runOff + (long)blocks * BlockSize <= image.Length)
          image.AsSpan(runOff, blocks * BlockSize).Clear();
      }
    } else {
      var dirBlock = (int)InodeNoAddr(image, inode);
      if (dirBlock > 0 && (long)dirBlock * BlockSize + BlockSize <= image.Length)
        image.AsSpan(dirBlock * BlockSize, BlockSize).Clear();
    }
    image.AsSpan(InodeOffset(inode), InodeSize).Clear();
  }
}
