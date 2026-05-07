#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Random-access in-place modifier for Microware OS-9 RBF disk images. Reads
/// and writes only the identification sector, the allocation bitmap, the root
/// directory's FD + extents, the new file's FD sector, and the new file's data
/// segments — never the whole image. Lets the host operate on multi-megabyte
/// underlying streams without paging the entire disk into memory.
///
/// <para>Limitations: only the root directory is mutated (no nested dirs);
/// file data is allocated as a single contiguous segment (no fragmentation
/// recovery — fails with <see cref="InvalidOperationException"/> when the
/// largest free run is too small).</para>
/// </summary>
public static class Os9RbfModifier {

  /// <summary>
  /// Adds a file to an existing image. Caller is responsible for ensuring the
  /// name does not already exist (use <see cref="RemoveFile"/> first for
  /// replace-by-name semantics).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ValidateName(name);

    var id = ReadSector(image, 0);
    var totalSectors = ReadU24Be(id, Os9Layout.Pd_DD_TOT);
    var bitmapBytes = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(Os9Layout.Pd_DD_MAP));
    var clusterSize = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(Os9Layout.Pd_DD_BIT));
    var rootFdLsn = ReadU24Be(id, Os9Layout.Pd_DD_DIR);

    if (clusterSize != 1)
      throw new InvalidOperationException(
        "OS-9 RBF: modifier only supports cluster-size 1 (the writer's reference geometry).");

    var bitmapSectors = (bitmapBytes + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    var bitmap = ReadSectors(image, Os9Layout.BitmapLsn, bitmapSectors);

    // Allocate FD sector for the new file.
    var fdLsn = AllocateRun(bitmap, totalSectors, 1);
    if (fdLsn < 0)
      throw new InvalidOperationException("OS-9 RBF: out of free space for FD sector.");

    // Allocate contiguous data run.
    var dataSectors = (data.Length + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    var dataLsn = 0;
    if (dataSectors > 0) {
      dataLsn = AllocateRun(bitmap, totalSectors, dataSectors);
      if (dataLsn < 0) {
        // Roll back FD allocation.
        MarkFree(bitmap, fdLsn);
        throw new InvalidOperationException(
          $"OS-9 RBF: no contiguous {dataSectors}-sector run available for file data.");
      }
    }

    // Find a free directory slot in the root dir's segments — extending the
    // root dir's last segment when there's no slot.
    var rootFd = ReadSector(image, rootFdLsn);
    var rootSize = (long)BinaryPrimitives.ReadUInt32BigEndian(rootFd.AsSpan(Os9Layout.FD_SIZ));
    var slot = FindFreeRootDirSlot(image, rootFd, rootSize);
    if (!slot.Found) {
      // Try to extend the dir by one sector. We grow the last segment if its
      // trailing sector is free; otherwise add a new segment list entry.
      slot = ExtendRootDir(image, bitmap, totalSectors, rootFdLsn, rootFd, ref rootSize);
      if (!slot.Found) {
        // Roll back data + FD allocations.
        if (dataSectors > 0) MarkFreeRun(bitmap, dataLsn, dataSectors);
        MarkFree(bitmap, fdLsn);
        throw new InvalidOperationException("OS-9 RBF: cannot extend root directory; no free space.");
      }
    }

    // Write file data sectors (only the touched ones).
    for (var i = 0; i < dataSectors; i++) {
      var buf = new byte[Os9Layout.SectorSize];
      var srcOff = i * Os9Layout.SectorSize;
      var chunk = Math.Min(Os9Layout.SectorSize, data.Length - srcOff);
      Buffer.BlockCopy(data, srcOff, buf, 0, chunk);
      WriteSector(image, dataLsn + i, buf);
    }

    // Build and write the file's FD.
    var fd = new byte[Os9Layout.SectorSize];
    fd[Os9Layout.FD_ATT] = Os9Layout.DefaultFileAttr;
    BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(Os9Layout.FD_OWN), 0);
    WriteDate(fd, Os9Layout.FD_DAT, includeTime: true);
    fd[Os9Layout.FD_LNK] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(fd.AsSpan(Os9Layout.FD_SIZ), (uint)data.Length);
    WriteDate(fd, Os9Layout.FD_CRE, includeTime: false);
    if (dataSectors > 0) {
      WriteU24Be(fd, Os9Layout.FD_SEG + 0, dataLsn);
      BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(Os9Layout.FD_SEG + 3), (ushort)dataSectors);
    }
    WriteSector(image, fdLsn, fd);

    // Write the directory entry into the located slot.
    var dirSec = ReadSector(image, slot.Lsn);
    var entrySpan = dirSec.AsSpan(slot.OffsetInSector, Os9Layout.DirEntryBytes);
    entrySpan.Clear();
    Os9RbfWriter.WriteHighBitTerminatedAscii(entrySpan, name, Os9Layout.DirEntryNameMaxBytes);
    WriteU24Be(dirSec, slot.OffsetInSector + Os9Layout.DirEntryFdLsnOffset, fdLsn);
    WriteSector(image, slot.Lsn, dirSec);

    // Persist updated bitmap.
    WriteSectors(image, Os9Layout.BitmapLsn, bitmap);

    // Persist updated root FD if size grew.
    if (rootSize != (long)BinaryPrimitives.ReadUInt32BigEndian(
          ReadSector(image, rootFdLsn).AsSpan(Os9Layout.FD_SIZ))) {
      WriteSector(image, rootFdLsn, rootFd);
    }
  }

  /// <summary>
  /// Removes the named file. Returns true on success. Frees the file's FD and
  /// data segments via the bitmap, and zeroes the directory entry. When
  /// <paramref name="wipeData"/> is true, the file's data sectors and FD
  /// sector are zeroed as well.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var id = ReadSector(image, 0);
    var bitmapBytes = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(Os9Layout.Pd_DD_MAP));
    var clusterSize = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(Os9Layout.Pd_DD_BIT));
    var rootFdLsn = ReadU24Be(id, Os9Layout.Pd_DD_DIR);

    if (clusterSize != 1)
      throw new InvalidOperationException(
        "OS-9 RBF: modifier only supports cluster-size 1.");

    var bitmapSectors = (bitmapBytes + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    var bitmap = ReadSectors(image, Os9Layout.BitmapLsn, bitmapSectors);

    var rootFd = ReadSector(image, rootFdLsn);
    var rootSize = (long)BinaryPrimitives.ReadUInt32BigEndian(rootFd.AsSpan(Os9Layout.FD_SIZ));

    var found = LocateRootDirEntry(image, rootFd, rootSize, name, out var entryLsn, out var entryOff, out var fileFdLsn);
    if (!found) return false;

    // Read the file FD and free its segments.
    var fileFd = ReadSector(image, fileFdLsn);
    var segOff = Os9Layout.FD_SEG;
    while (segOff + Os9Layout.SegmentBytes <= fileFd.Length) {
      var startLsn = ReadU24Be(fileFd, segOff);
      var sectors = BinaryPrimitives.ReadUInt16BigEndian(fileFd.AsSpan(segOff + 3));
      if (startLsn == 0) break;
      MarkFreeRun(bitmap, startLsn, sectors);
      if (wipeData) {
        var zero = new byte[Os9Layout.SectorSize];
        for (var i = 0; i < sectors; i++) WriteSector(image, startLsn + i, zero);
      }
      segOff += Os9Layout.SegmentBytes;
    }
    MarkFree(bitmap, fileFdLsn);
    if (wipeData) WriteSector(image, fileFdLsn, new byte[Os9Layout.SectorSize]);

    // Zero the directory entry. (OS-9 marks slots empty by setting first byte to 0.)
    var dirSec = ReadSector(image, entryLsn);
    dirSec.AsSpan(entryOff, Os9Layout.DirEntryBytes).Clear();
    WriteSector(image, entryLsn, dirSec);

    // Persist updated bitmap.
    WriteSectors(image, Os9Layout.BitmapLsn, bitmap);
    return true;
  }

  // ── Sector I/O ─────────────────────────────────────────────────────────

  private static long Offset(int lsn) => (long)lsn * Os9Layout.SectorSize;

  private static byte[] ReadSector(Stream s, int lsn) {
    var buf = new byte[Os9Layout.SectorSize];
    s.Position = Offset(lsn);
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static byte[] ReadSectors(Stream s, int firstLsn, int count) {
    var buf = new byte[count * Os9Layout.SectorSize];
    s.Position = Offset(firstLsn);
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteSector(Stream s, int lsn, byte[] data) {
    s.Position = Offset(lsn);
    s.Write(data, 0, Os9Layout.SectorSize);
  }

  private static void WriteSectors(Stream s, int firstLsn, byte[] data) {
    s.Position = Offset(firstLsn);
    s.Write(data, 0, data.Length);
  }

  // ── Bitmap (1 = allocated, MSB-first) ──────────────────────────────────

  private static bool IsAllocated(byte[] bitmap, int lsn) {
    var byteIdx = lsn / 8;
    if (byteIdx >= bitmap.Length) return true;
    return (bitmap[byteIdx] & (0x80 >> (lsn % 8))) != 0;
  }

  private static void MarkAllocated(byte[] bitmap, int lsn) {
    var byteIdx = lsn / 8;
    if (byteIdx >= bitmap.Length) return;
    bitmap[byteIdx] |= (byte)(0x80 >> (lsn % 8));
  }

  private static void MarkFree(byte[] bitmap, int lsn) {
    var byteIdx = lsn / 8;
    if (byteIdx >= bitmap.Length) return;
    bitmap[byteIdx] &= (byte)~(0x80 >> (lsn % 8));
  }

  private static void MarkFreeRun(byte[] bitmap, int firstLsn, int count) {
    for (var i = 0; i < count; i++) MarkFree(bitmap, firstLsn + i);
  }

  /// <summary>Allocates a contiguous run of <paramref name="count"/> sectors. Returns -1 if none fits.</summary>
  private static int AllocateRun(byte[] bitmap, int totalSectors, int count) {
    if (count <= 0) return -1;
    var run = 0;
    var runStart = -1;
    for (var lsn = 0; lsn < totalSectors; lsn++) {
      if (!IsAllocated(bitmap, lsn)) {
        if (run == 0) runStart = lsn;
        run++;
        if (run >= count) {
          for (var i = 0; i < count; i++) MarkAllocated(bitmap, runStart + i);
          return runStart;
        }
      } else {
        run = 0;
        runStart = -1;
      }
    }
    return -1;
  }

  // ── Root directory walking ────────────────────────────────────────────

  private readonly record struct DirSlot(bool Found, int Lsn, int OffsetInSector);

  /// <summary>Iterates root dir bytes, calling <paramref name="onEntry"/> per 32-byte slot.</summary>
  private static void WalkRootDir(
      Stream image, byte[] rootFd, long rootSize,
      Func<int, int, byte[], bool> onEntry) {
    var consumed = 0L;
    var segOff = Os9Layout.FD_SEG;
    while (segOff + Os9Layout.SegmentBytes <= rootFd.Length && consumed < rootSize) {
      var startLsn = ReadU24Be(rootFd, segOff);
      var sectors = BinaryPrimitives.ReadUInt16BigEndian(rootFd.AsSpan(segOff + 3));
      if (startLsn == 0) break;

      for (var i = 0; i < sectors && consumed < rootSize; i++) {
        var sec = ReadSector(image, startLsn + i);
        for (var off = 0; off + Os9Layout.DirEntryBytes <= sec.Length && consumed < rootSize;
             off += Os9Layout.DirEntryBytes, consumed += Os9Layout.DirEntryBytes) {
          if (!onEntry(startLsn + i, off, sec)) return;
        }
      }
      segOff += Os9Layout.SegmentBytes;
    }
  }

  private static DirSlot FindFreeRootDirSlot(Stream image, byte[] rootFd, long rootSize) {
    var slot = default(DirSlot);
    WalkRootDir(image, rootFd, rootSize, (lsn, off, sec) => {
      if (sec[off] == 0) {
        slot = new DirSlot(true, lsn, off);
        return false;
      }
      return true;
    });
    return slot;
  }

  /// <summary>
  /// Locates a root-dir entry by name and returns its sector LSN, byte offset
  /// in that sector, and the FD LSN of the file it points to.
  /// </summary>
  private static bool LocateRootDirEntry(
      Stream image, byte[] rootFd, long rootSize, string targetName,
      out int entryLsn, out int entryOff, out int fileFdLsn) {
    int foundLsn = 0, foundOff = 0, foundFd = 0;
    var found = false;
    WalkRootDir(image, rootFd, rootSize, (lsn, off, sec) => {
      if (sec[off] == 0) return true;
      var name = ReadHighBitTerminatedAscii(sec.AsSpan(off, Os9Layout.DirEntryNameMaxBytes));
      if (!string.Equals(name, targetName, StringComparison.Ordinal)) return true;
      foundLsn = lsn;
      foundOff = off;
      foundFd = ReadU24Be(sec, off + Os9Layout.DirEntryFdLsnOffset);
      found = true;
      return false;
    });
    entryLsn = foundLsn;
    entryOff = foundOff;
    fileFdLsn = foundFd;
    return found && fileFdLsn != 0;
  }

  /// <summary>
  /// Extends the root directory by one sector. Grows the last segment in
  /// place when the sector immediately after it is free; otherwise adds a
  /// new segment list entry pointing at a freshly allocated sector.
  /// </summary>
  private static DirSlot ExtendRootDir(
      Stream image, byte[] bitmap, int totalSectors,
      int rootFdLsn, byte[] rootFd, ref long rootSize) {

    // Find the last non-empty segment slot.
    var segOff = Os9Layout.FD_SEG;
    var lastNonEmpty = -1;
    var nextEmpty = -1;
    while (segOff + Os9Layout.SegmentBytes <= rootFd.Length) {
      var startLsn = ReadU24Be(rootFd, segOff);
      if (startLsn == 0) {
        if (nextEmpty < 0) nextEmpty = segOff;
        break;
      }
      lastNonEmpty = segOff;
      segOff += Os9Layout.SegmentBytes;
    }
    if (lastNonEmpty < 0) return default;

    var lastStart = ReadU24Be(rootFd, lastNonEmpty);
    var lastCount = BinaryPrimitives.ReadUInt16BigEndian(rootFd.AsSpan(lastNonEmpty + 3));
    var trailingLsn = lastStart + lastCount;

    int newSectorLsn;
    if (trailingLsn < totalSectors && !IsAllocated(bitmap, trailingLsn)) {
      // Grow last segment in place.
      MarkAllocated(bitmap, trailingLsn);
      newSectorLsn = trailingLsn;
      BinaryPrimitives.WriteUInt16BigEndian(rootFd.AsSpan(lastNonEmpty + 3), (ushort)(lastCount + 1));
    } else {
      if (nextEmpty < 0) return default; // segment list full
      newSectorLsn = AllocateRun(bitmap, totalSectors, 1);
      if (newSectorLsn < 0) return default;
      WriteU24Be(rootFd, nextEmpty, newSectorLsn);
      BinaryPrimitives.WriteUInt16BigEndian(rootFd.AsSpan(nextEmpty + 3), 1);
    }

    // Zero the new sector and grow FD.SIZ to cover all 8 new entries.
    WriteSector(image, newSectorLsn, new byte[Os9Layout.SectorSize]);
    rootSize += Os9Layout.SectorSize;
    BinaryPrimitives.WriteUInt32BigEndian(rootFd.AsSpan(Os9Layout.FD_SIZ), (uint)rootSize);
    WriteSector(image, rootFdLsn, rootFd);

    return new DirSlot(true, newSectorLsn, 0);
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static void ValidateName(string name) {
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("OS-9 RBF: filename must not be empty.", nameof(name));
    if (name.Length > Os9Layout.DirEntryNameMaxBytes - 1)
      throw new ArgumentException(
        $"OS-9 RBF: filename \"{name}\" exceeds {Os9Layout.DirEntryNameMaxBytes - 1} characters.", nameof(name));
    foreach (var c in name) {
      if (c is < (char)0x20 or > (char)0x7E)
        throw new ArgumentException(
          $"OS-9 RBF: filename \"{name}\" contains non-printable ASCII characters.", nameof(name));
    }
  }

  private static int ReadU24Be(byte[] span, int offset)
    => (span[offset] << 16) | (span[offset + 1] << 8) | span[offset + 2];

  private static void WriteU24Be(byte[] span, int offset, int value) {
    span[offset + 0] = (byte)((value >> 16) & 0xFF);
    span[offset + 1] = (byte)((value >> 8) & 0xFF);
    span[offset + 2] = (byte)(value & 0xFF);
  }

  private static void WriteDate(byte[] fd, int offset, bool includeTime) {
    var now = DateTime.Now;
    fd[offset + 0] = (byte)(now.Year % 100);
    fd[offset + 1] = (byte)now.Month;
    fd[offset + 2] = (byte)now.Day;
    if (includeTime) {
      fd[offset + 3] = (byte)now.Hour;
      fd[offset + 4] = (byte)now.Minute;
    }
  }

  private static string ReadHighBitTerminatedAscii(ReadOnlySpan<byte> span) {
    var sb = new StringBuilder();
    for (var i = 0; i < span.Length; i++) {
      var b = span[i];
      if (b == 0) break;
      sb.Append((char)(b & 0x7F));
      if ((b & 0x80) != 0) break;
    }
    return sb.ToString();
  }
}
