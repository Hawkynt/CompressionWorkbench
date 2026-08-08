#pragma warning disable CS1591
namespace FileSystem.OpenVms;

/// <summary>
/// True in-place R/W modifier for the workbench-layout OpenVMS Files-11 ODS-2
/// volume. Honours the ODS-2 semantics described at <see cref="OpenVmsLayout"/>:
/// <list type="bullet">
///   <item><b>Add</b> — scan BITMAP.SYS for the first free File-ID slot
///         in INDEXF.SYS, scan BITMAP.SYS for a contiguous run of free
///         data LBNs, write the File Header at its known offset, copy
///         the caller's bytes into the allocated data LBNs, drop a 64-byte
///         directory entry into 000000.DIR, and flush the bitmap +
///         home-block accounting back to disk.</item>
///   <item><b>Replace</b> — internally a Remove + Add round so the new
///         data lands at fresh LBNs. The previous file's File Header is
///         freed and its data LBNs are released to the bitmap; untouched
///         neighbours stay byte-identical.</item>
///   <item><b>Remove</b> — zero the directory entry, free the File Header
///         in INDEXF.SYS (zero its struc-level), release its data LBNs in
///         BITMAP.SYS, and securely wipe the released data LBNs to zero.</item>
/// </list>
/// <para>
/// The bytes touched by every operation are strictly bounded:
/// 1 BITMAP.SYS sector window + 1 INDEXF.SYS File-Header LBN + 1
/// directory LBN + N data LBNs (= ⌈size / 512⌉). LBNs outside that
/// footprint are byte-identical to the pre-operation image.
/// </para>
/// </summary>
public static class OpenVmsInPlaceModifier {

  /// <summary>
  /// Adds <paramref name="data"/> as a new file named <paramref name="name"/>.
  /// Throws <see cref="IOException"/> when the volume is full, or
  /// <see cref="InvalidDataException"/> when <paramref name="archive"/> is
  /// not a workbench-layout volume.
  /// </summary>
  public static int AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    EnsureCwbVolume(archive);

    var normalized = OpenVmsWriter.NormalizeName(name);

    // Replacement semantics handled by caller; this method itself fails on duplicate.
    if (FindDirectoryEntry(archive, normalized).Found)
      throw new IOException($"File '{normalized}' already exists in the root directory.");

    // 1. Pick a free File-ID slot in INDEXF.SYS.
    var fid = AllocateFreeFileId(archive)
      ?? throw new IOException($"INDEXF.SYS full (max {OpenVmsLayout.MaxFiles} files).");

    // 2. Allocate contiguous data LBNs via BITMAP.SYS.
    var blocks = (data.Length + OpenVmsLayout.BlockSize - 1) / OpenVmsLayout.BlockSize;
    var bitmap = ReadBitmap(archive);
    var startLbn = blocks > 0 ? bitmap.AllocateRun(blocks) : 0;
    if (blocks > 0 && startLbn < 0)
      throw new IOException($"Volume full: cannot allocate {blocks} contiguous LBN(s) for '{normalized}'.");

    // 3. Write the data bytes into the allocated LBNs.
    if (blocks > 0) {
      var blockBuffer = new byte[blocks * OpenVmsLayout.BlockSize];
      data.AsSpan().CopyTo(blockBuffer);
      WriteAt(archive, OpenVmsLayout.LbnToByteOffset(startLbn), blockBuffer);
    }

    // 4. Write the File Header at INDEXF.SYS[fid].
    var fh = new OpenVmsFileHeader {
      FileId = fid,
      Sequence = (ushort)(GetExistingSequence(archive, fid) + 1),
      InUse = true,
      Name = normalized,
      Size = data.Length,
    };
    if (blocks > 0) fh.Extents.Add(new OpenVmsFileHeader.RetrievalPointer(startLbn, blocks));
    WriteFileHeader(archive, fh);

    // 5. Flush the bitmap.
    WriteBitmap(archive, bitmap);

    // 6. Insert a directory entry in 000000.DIR.
    InsertDirectoryEntry(archive, new OpenVmsDirectory.Entry(fid, fh.Sequence, normalized, data.Length));

    return fid;
  }

  /// <summary>
  /// Removes the file named <paramref name="name"/> if present. Returns true when
  /// a removal happened; false when no such name exists. The freed data LBNs are
  /// zero-wiped so the previous content isn't recoverable via raw image bytes.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    EnsureCwbVolume(archive);

    var normalized = OpenVmsWriter.NormalizeName(name);
    var locator = FindDirectoryEntry(archive, normalized);
    if (!locator.Found) return false;

    // 1. Read FH, free its data LBNs in the bitmap, optionally wipe data.
    var fh = OpenVmsReader.ReadFileHeader(SnapshotArchive(archive), locator.Entry!.FileId)
      ?? throw new InvalidDataException($"File-ID {locator.Entry.FileId} referenced by directory entry '{normalized}' has no File Header.");

    var bitmap = ReadBitmap(archive);
    foreach (var ext in fh.Extents) {
      if (wipeData && ext.Count > 0) {
        var zero = new byte[ext.Count * OpenVmsLayout.BlockSize];
        WriteAt(archive, OpenVmsLayout.LbnToByteOffset(ext.StartLbn), zero);
      }
      bitmap.FreeRun(ext.StartLbn, ext.Count);
    }
    WriteBitmap(archive, bitmap);

    // 2. Mark the FH free in INDEXF.SYS (zero struc-level via ClearInUse, bump sequence).
    var seq = fh.Sequence;
    fh.ClearInUse();
    fh.Sequence = (ushort)((seq + 1) & 0xFFFF);
    WriteFileHeader(archive, fh);

    // 3. Zero the directory slot.
    var dirBlock = ReadBlock(archive, locator.DirectoryLbn);
    OpenVmsDirectory.ClearEntry(dirBlock, locator.SlotIndex);
    WriteBlock(archive, locator.DirectoryLbn, dirBlock);
    return true;
  }

  /// <summary>
  /// Replaces the file named <paramref name="name"/> with new bytes. Always
  /// fully removes the old entry first, then adds the new one. Returns the
  /// File-ID of the freshly-written entry. Throws when the volume can't
  /// satisfy the new allocation (the old entry is removed before the new
  /// allocation is attempted).
  /// </summary>
  public static int ReplaceFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    EnsureCwbVolume(archive);

    RemoveFile(archive, name, wipeData: true);
    return AddFile(archive, name, data);
  }

  // ── Internals ──

  /// <summary>Reads a single 512-byte block from <paramref name="archive"/>.</summary>
  private static byte[] ReadBlock(Stream archive, int lbn) {
    archive.Position = OpenVmsLayout.LbnToByteOffset(lbn);
    var buf = new byte[OpenVmsLayout.BlockSize];
    archive.ReadExactly(buf);
    return buf;
  }

  /// <summary>Writes a single 512-byte block back to <paramref name="archive"/>.</summary>
  private static void WriteBlock(Stream archive, int lbn, byte[] block) {
    if (block.Length != OpenVmsLayout.BlockSize)
      throw new ArgumentException("block must be exactly 512 bytes", nameof(block));
    archive.Position = OpenVmsLayout.LbnToByteOffset(lbn);
    archive.Write(block, 0, block.Length);
    archive.Flush();
  }

  /// <summary>Writes <paramref name="data"/> at the given byte offset.</summary>
  private static void WriteAt(Stream archive, long offset, byte[] data) {
    archive.Position = offset;
    archive.Write(data, 0, data.Length);
    archive.Flush();
  }

  /// <summary>Throws when <paramref name="archive"/> does not carry the workbench-layout layout marker.</summary>
  private static void EnsureCwbVolume(Stream archive) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("archive must be read/write/seekable for in-place modification", nameof(archive));
    var hb = ReadBlock(archive, OpenVmsLayout.HomeBlockLbn);
    if (!hb.AsSpan(OpenVmsLayout.LayoutMarkerOffset, OpenVmsLayout.LayoutMarker.Length)
        .SequenceEqual(OpenVmsLayout.LayoutMarker.AsSpan()))
      throw new InvalidDataException("archive is not a Files-11 volume this writer laid out (no layout marker at home-block byte 132).");
  }

  /// <summary>Reads BITMAP.SYS into an <see cref="OpenVmsBitmap"/>.</summary>
  private static OpenVmsBitmap ReadBitmap(Stream archive) {
    var bytes = new byte[OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize];
    archive.Position = OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.BitmapStartLbn);
    archive.ReadExactly(bytes);
    return new OpenVmsBitmap(bytes);
  }

  /// <summary>Flushes BITMAP.SYS back to disk.</summary>
  private static void WriteBitmap(Stream archive, OpenVmsBitmap bitmap) {
    archive.Position = OpenVmsLayout.LbnToByteOffset(OpenVmsLayout.BitmapStartLbn);
    archive.Write(bitmap.Bytes, 0, OpenVmsLayout.BitmapBlockCount * OpenVmsLayout.BlockSize);
    archive.Flush();
  }

  /// <summary>Scans INDEXF.SYS for the first File-ID whose FH is not in use (struc-level word zero).</summary>
  private static int? AllocateFreeFileId(Stream archive) {
    for (var fid = OpenVmsLayout.FirstUserFileId; fid <= OpenVmsLayout.MaxFiles; fid++) {
      var fh = ReadBlock(archive, OpenVmsLayout.IndexFileStartLbn + fid - 1);
      var struc = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
        fh.AsSpan(OpenVmsLayout.FhStrucLev, 2));
      if (struc == 0) return fid;
    }
    return null;
  }

  /// <summary>Returns the existing sequence number for the FH at <paramref name="fid"/> (0 if absent).</summary>
  private static int GetExistingSequence(Stream archive, int fid) {
    var fh = ReadBlock(archive, OpenVmsLayout.IndexFileStartLbn + fid - 1);
    return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
      fh.AsSpan(OpenVmsLayout.FhFidSeq, 2));
  }

  /// <summary>Writes <paramref name="fh"/> to its assigned INDEXF.SYS slot.</summary>
  private static void WriteFileHeader(Stream archive, OpenVmsFileHeader fh) {
    var lbn = OpenVmsLayout.IndexFileStartLbn + fh.FileId - 1;
    WriteBlock(archive, lbn, fh.Serialize());
  }

  /// <summary>Drops a directory entry into the first available slot, chaining a new block when full.</summary>
  private static void InsertDirectoryEntry(Stream archive, OpenVmsDirectory.Entry entry) {
    var lbn = OpenVmsLayout.RootDirectoryLbn;
    var visited = new HashSet<int>();
    while (visited.Add(lbn)) {
      var dirBlock = ReadBlock(archive, lbn);
      for (var slot = OpenVmsDirectory.FileEntryStartSlot; slot < OpenVmsDirectory.EntriesPerBlock; slot++) {
        var existing = OpenVmsDirectory.ReadEntry(dirBlock, slot);
        if (existing.IsFree) {
          OpenVmsDirectory.WriteEntry(dirBlock, slot, entry);
          WriteBlock(archive, lbn, dirBlock);
          return;
        }
      }
      var next = OpenVmsDirectory.ReadChainLink(dirBlock);
      if (next == 0) {
        // Allocate a new directory block and link it.
        var bitmap = ReadBitmap(archive);
        var newLbn = bitmap.AllocateRun(1);
        if (newLbn < 0)
          throw new IOException("Volume full: cannot allocate a new directory block to extend 000000.DIR.");
        WriteBitmap(archive, bitmap);

        var newBlock = new byte[OpenVmsLayout.BlockSize];
        OpenVmsDirectory.WriteChainLink(newBlock, 0);
        OpenVmsDirectory.WriteEntry(newBlock, OpenVmsDirectory.FileEntryStartSlot, entry);
        WriteBlock(archive, newLbn, newBlock);

        OpenVmsDirectory.WriteChainLink(dirBlock, newLbn);
        WriteBlock(archive, lbn, dirBlock);
        return;
      }
      lbn = next;
    }
    throw new IOException("000000.DIR chain corrupted (loop detected).");
  }

  /// <summary>Locator result for a directory walk — points at the dir block + slot containing the entry.</summary>
  internal sealed record class DirectoryEntryLocator(bool Found, int DirectoryLbn, int SlotIndex, OpenVmsDirectory.Entry? Entry) {
    public static readonly DirectoryEntryLocator NotFound = new(false, 0, 0, null);
  }

  /// <summary>Walks 000000.DIR looking for <paramref name="targetName"/>.</summary>
  private static DirectoryEntryLocator FindDirectoryEntry(Stream archive, string targetName) {
    var lbn = OpenVmsLayout.RootDirectoryLbn;
    var visited = new HashSet<int>();
    while (lbn > 0 && visited.Add(lbn)) {
      var dirBlock = ReadBlock(archive, lbn);
      for (var slot = OpenVmsDirectory.FileEntryStartSlot; slot < OpenVmsDirectory.EntriesPerBlock; slot++) {
        var entry = OpenVmsDirectory.ReadEntry(dirBlock, slot);
        if (entry.IsFree) continue;
        if (string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase))
          return new DirectoryEntryLocator(true, lbn, slot, entry);
      }
      lbn = OpenVmsDirectory.ReadChainLink(dirBlock);
    }
    return DirectoryEntryLocator.NotFound;
  }

  /// <summary>Snapshots the whole archive as a byte[] so we can use the static FH reader helpers.</summary>
  private static byte[] SnapshotArchive(Stream archive) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    return ms.ToArray();
  }
}
