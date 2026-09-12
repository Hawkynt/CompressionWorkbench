#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Htfs;

/// <summary>
/// Genuine in-place R/W mutation for HTFS images at the root directory. New
/// files claim a free inode slot inside the existing inode-block region and a
/// fresh contiguous data run appended at image end; the dirent is inserted into
/// the single-block root directory and <c>s_fsize</c> is bumped. Every untouched
/// inode + data block stays byte-identical at its original offset.
/// </summary>
/// <remarks>
/// <para>On-disk anatomy (mirrors <see cref="HtfsWriter"/> / <see cref="HtfsReader"/>):</para>
/// <list type="bullet">
///   <item>Superblock at byte 512: magic@0, <c>s_isize</c>@4 (=inode blocks),
///     <c>s_fsize</c>@8 (=total blocks).</item>
///   <item>Inode array from block <c>inodeStart = sbBlock+1</c>; inode N (≥2)
///     lives at block <c>inodeStart + (N-2)/inodesPerBlock</c>, slot
///     <c>(N-2)%inodesPerBlock</c>. Each inode 64 bytes: di_mode@0, di_nlink@2,
///     di_size@8 (u32), di_first_blk@24 (u32), di_block_count@28 (u32).</item>
///   <item>Root dir = inode 2; its body is a single data block of 16-byte
///     entries (u16 inode + 14-byte name). Files use one contiguous extent.</item>
/// </list>
/// <para><b>Scope.</b> Root-directory files only. A new inode must fit in the
/// existing inode-block region (no new inode block — that would shift data), and
/// the new dirent must fit the single root block. Nested-directory adds and any
/// case that breaks these invariants fall back to the caller's <c>rebuild</c>.</para>
/// </remarks>
internal static class HtfsInPlaceModifier {

  private const int SuperblockOffset = HtfsWriter.SuperblockOffset; // 512
  private const int InodeSize = HtfsWriter.InodeSize;               // 64
  private const int MaxNameLen = HtfsWriter.MaxNameLen;             // 14
  private const ushort ModeFile = 0x8000 | 0x1A4;                   // 0o644 file
  private const int RootInode = 2;

  // ── Public entry points ─────────────────────────────────────────────

  public static void Add(
    Stream archive,
    IReadOnlyList<ArchiveInputInfo> inputs,
    Action<Stream, IReadOnlyList<ArchiveInputInfo>> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(rebuild);

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      payloads.Add((name, data));
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

  // ── Geometry ─────────────────────────────────────────────────────────

  private readonly struct Geometry {
    public required int BlockSize { get; init; }
    public required int InodeStart { get; init; }
    public required int InodeBlocks { get; init; }
    public required int InodesPerBlock { get; init; }
    public required int TotalBlocks { get; init; }
  }

  private static bool TryGeometry(byte[] image, out Geometry geo) {
    geo = default;
    var sb = HtfsSuperblock.TryParse(image);
    if (!sb.Valid) return false;

    // Block-size auto-detect: pick the size whose fsize×bs ≈ image length
    // (same rule HtfsReader uses).
    var blockSize = 0;
    foreach (var bs in new[] { 512, 1024, 2048 }) {
      var implied = (long)sb.Fsize * bs;
      if (implied >= image.LongLength - bs && implied <= image.LongLength + bs) { blockSize = bs; break; }
    }
    if (blockSize == 0) blockSize = HtfsWriter.DefaultBlockSize;
    if (image.LongLength < (long)sb.Fsize * blockSize) return false;

    var inodesPerBlock = blockSize / InodeSize;
    if (inodesPerBlock <= 0) return false;
    var inodeStart = (SuperblockOffset / blockSize) + 1;

    geo = new Geometry {
      BlockSize = blockSize,
      InodeStart = inodeStart,
      InodeBlocks = (int)sb.Isize,
      InodesPerBlock = inodesPerBlock,
      TotalBlocks = (int)sb.Fsize,
    };
    return true;
  }

  // ── Inode accessors ──────────────────────────────────────────────────

  private static int InodeOffset(Geometry geo, int inode) {
    var blockOff = (inode - 2) / geo.InodesPerBlock;
    var slotOff = (inode - 2) % geo.InodesPerBlock;
    return (geo.InodeStart + blockOff) * geo.BlockSize + slotOff * InodeSize;
  }

  private static bool InodeInUse(byte[] image, Geometry geo, int inode) {
    var off = InodeOffset(geo, inode);
    if (off + InodeSize > image.Length) return false;
    return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off)) != 0; // di_mode != 0
  }

  private static (bool IsDir, int Size, int First, int BlockCount) ReadInode(byte[] image, Geometry geo, int inode) {
    var off = InodeOffset(geo, inode);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off));
    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 8));
    var first = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 24));
    var bc = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 28));
    return ((mode & 0xF000) == 0x4000, size, first, bc);
  }

  private static void WriteFileInode(byte[] image, Geometry geo, int inode, int size, int first, int blockCount) {
    var off = InodeOffset(geo, inode);
    image.AsSpan(off, InodeSize).Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0), ModeFile);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2), 1);            // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8), (uint)size);   // di_size
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 12), now);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 16), now);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 20), now);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 24), (uint)first); // di_first_blk
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28), (uint)blockCount);
  }

  /// <summary>Finds the next free inode number that lives inside the existing inode-block region.</summary>
  private static bool TryAllocInode(byte[] image, Geometry geo, out int inode) {
    inode = 0;
    var capacity = geo.InodeBlocks * geo.InodesPerBlock; // total inode slots (inode numbers 2..capacity+1)
    for (var n = 3; n <= capacity + 1; n++) {            // 2 is root, never reuse
      if (!InodeInUse(image, geo, n)) { inode = n; return true; }
    }
    return false;
  }

  // ── Root directory ───────────────────────────────────────────────────

  private static bool TryRootDirBlock(byte[] image, Geometry geo, out int dirOffset) {
    dirOffset = 0;
    var (isDir, _, first, _) = ReadInode(image, geo, RootInode);
    if (!isDir || first == 0) return false;
    var off = first * geo.BlockSize;
    if (off + geo.BlockSize > image.Length) return false;
    dirOffset = off;
    return true;
  }

  private static IEnumerable<(int Slot, int Inode, string Name)> EnumerateDir(byte[] image, Geometry geo, int dirOffset) {
    for (var cur = 0; cur + 16 <= geo.BlockSize; cur += 16) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOffset + cur));
      if (ino == 0) continue;
      var nameSpan = image.AsSpan(dirOffset + cur + 2, MaxNameLen);
      var nul = nameSpan.IndexOf((byte)0);
      var len = nul < 0 ? MaxNameLen : nul;
      var name = Encoding.ASCII.GetString(nameSpan[..len]);
      yield return (cur, ino, name);
    }
  }

  private static bool TryFindFreeDirSlot(byte[] image, Geometry geo, int dirOffset, out int slot) {
    slot = -1;
    for (var cur = 0; cur + 16 <= geo.BlockSize; cur += 16) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOffset + cur)) == 0) { slot = cur; return true; }
    }
    return false;
  }

  private static void WriteDirEntry(byte[] image, int dirOffset, int slot, int inode, string name) {
    var off = dirOffset + slot;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off), (ushort)inode);
    for (var i = 0; i < MaxNameLen; i++) image[off + 2 + i] = 0;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    if (nameBytes.Length > MaxNameLen) Array.Resize(ref nameBytes, MaxNameLen);
    nameBytes.CopyTo(image.AsSpan(off + 2));
  }

  private static void BumpDirSize(byte[] image, Geometry geo, int delta16ByteEntries) {
    var off = InodeOffset(geo, RootInode);
    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8), (uint)Math.Max(0, size + delta16ByteEntries * 16));
  }

  // ── Superblock counter ───────────────────────────────────────────────

  private static void SetFsize(byte[] image, int totalBlocks) =>
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SuperblockOffset + 0x08), (uint)totalBlocks);

  // ── Mutators ─────────────────────────────────────────────────────────

  private static bool TryAddInPlace(ref byte[] image, List<(string Name, byte[] Data)> payloads) {
    if (!TryGeometry(image, out var geo)) return false;

    // Root-only scope: any nested path falls back to rebuild.
    foreach (var (name, _) in payloads) {
      var norm = name.Replace('\\', '/').TrimStart('/');
      if (norm.Contains('/')) return false;
      if (Encoding.ASCII.GetByteCount(norm) > MaxNameLen) return false;
    }

    if (!TryRootDirBlock(image, geo, out var dirOffset)) return false;

    foreach (var (rawName, data) in payloads) {
      var name = rawName.Replace('\\', '/').TrimStart('/');

      // Replace by name when present.
      var existing = EnumerateDir(image, geo, dirOffset)
        .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
      if (existing.Inode != 0) {
        if (!TryReplaceBytes(ref image, ref geo, existing.Inode, data)) return false;
        continue;
      }

      // New file: alloc inode + dir slot first (both must fit), then append data.
      if (!TryAllocInode(image, geo, out var inode)) return false;
      if (!TryFindFreeDirSlot(image, geo, dirOffset, out var slot)) return false;

      var blockCount = data.Length == 0 ? 0 : (data.Length + geo.BlockSize - 1) / geo.BlockSize;
      var first = 0;
      if (blockCount > 0) {
        first = AppendBlocks(ref image, ref geo, data, blockCount);
      }

      WriteFileInode(image, geo, inode, data.Length, first, blockCount);
      WriteDirEntry(image, dirOffset, slot, inode, name);
      BumpDirSize(image, geo, +1);
    }

    return true;
  }

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames) {
    if (!TryGeometry(image, out var geo)) return false;
    if (!TryRootDirBlock(image, geo, out var dirOffset)) return false;

    var toRemove = new HashSet<string>(
      entryNames.Select(n => Leaf(n)), StringComparer.OrdinalIgnoreCase);

    var removedAny = false;
    foreach (var (slot, inode, name) in EnumerateDir(image, geo, dirOffset).ToList()) {
      if (inode <= RootInode) continue;
      if (!toRemove.Contains(name)) continue;

      // Wipe the data run + inode, clear the dir slot.
      var (_, _, first, bc) = ReadInode(image, geo, inode);
      if (bc > 0 && first > 0) {
        var dataOff = (long)first * geo.BlockSize;
        var dataLen = (long)bc * geo.BlockSize;
        if (dataOff >= 0 && dataOff + dataLen <= image.LongLength)
          image.AsSpan((int)dataOff, (int)dataLen).Clear();
      }
      image.AsSpan(InodeOffset(geo, inode), InodeSize).Clear();
      image.AsSpan(dirOffset + slot, 16).Clear();
      BumpDirSize(image, geo, -1);
      removedAny = true;
    }

    return removedAny;
  }

  /// <summary>
  /// Replaces a file's bytes. Overwrites the existing extent in place when the
  /// new payload fits the allocated block run (zeroing slack); otherwise appends
  /// a fresh contiguous run at image end and re-points the inode (old run dead).
  /// </summary>
  private static bool TryReplaceBytes(ref byte[] image, ref Geometry geo, int inode, byte[] data) {
    var (_, _, first, bc) = ReadInode(image, geo, inode);
    var newBlockCount = data.Length == 0 ? 0 : (data.Length + geo.BlockSize - 1) / geo.BlockSize;

    if (newBlockCount <= bc) {
      if (bc > 0 && first > 0) {
        var runOff = (long)first * geo.BlockSize;
        var runLen = (long)bc * geo.BlockSize;
        if (runOff + runLen > image.LongLength) return false;
        image.AsSpan((int)runOff, (int)runLen).Clear();
        if (data.Length > 0) data.CopyTo(image.AsSpan((int)runOff));
      } else if (data.Length > 0) {
        first = AppendBlocks(ref image, ref geo, data, newBlockCount);
        WriteFileInode(image, geo, inode, data.Length, first, newBlockCount);
        return true;
      }
      // Keep di_first_blk + the (possibly larger) block_count; only di_size shrinks.
      var off = InodeOffset(geo, inode);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8), (uint)data.Length);
      return true;
    }

    // Larger — append fresh run, re-point inode. Old run becomes dead space.
    first = AppendBlocks(ref image, ref geo, data, newBlockCount);
    WriteFileInode(image, geo, inode, data.Length, first, newBlockCount);
    return true;
  }

  /// <summary>
  /// Appends <paramref name="blockCount"/> blocks at image end, copies
  /// <paramref name="data"/> into them, bumps <c>s_fsize</c>, and returns the
  /// first block number. Updates <paramref name="geo"/> TotalBlocks.
  /// </summary>
  private static int AppendBlocks(ref byte[] image, ref Geometry geo, byte[] data, int blockCount) {
    var firstBlock = geo.TotalBlocks;
    var oldLen = image.Length;
    var addBytes = blockCount * geo.BlockSize;
    Array.Resize(ref image, oldLen + addBytes);
    if (data.Length > 0) data.CopyTo(image, firstBlock * geo.BlockSize);

    var newTotal = geo.TotalBlocks + blockCount;
    geo = geo with { TotalBlocks = newTotal };
    SetFsize(image, newTotal);
    return firstBlock;
  }

  private static string Leaf(string name) {
    var n = name.Replace('\\', '/').TrimStart('/');
    var idx = n.LastIndexOf('/');
    return idx >= 0 ? n[(idx + 1)..] : n;
  }
}
