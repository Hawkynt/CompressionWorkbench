#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Os9Rbf;

/// <summary>
/// In-place OS-9 RBF block mover. Moves sector-aligned extents within an OS-9
/// image and patches the file descriptor's segment list so the file remains
/// reachable at its new location.
///
/// <para>Each OS-9 file has a File Descriptor (FD) sector containing a segment
/// list of up to 48 (start-LSN, sector-count) pairs. Moving a file's data
/// requires updating the segment list in the FD sector and also adjusting the
/// allocation bitmap.</para>
/// </summary>
public sealed class Os9RbfBlockMover : IFilesystemBlockMover {

  /// <summary>Byte offset where user data typically begins (past ID + bitmap).</summary>
  public long DataOrigin => (long)(Os9Layout.BitmapLsn + Os9Layout.BitmapSectors) * Os9Layout.SectorSize;

  /// <summary>Allocation unit size (one 256-byte sector).</summary>
  public int UnitSize => Os9Layout.SectorSize;

  /// <summary>Converts a byte offset to a sector (LSN) number.</summary>
  public int OffsetToLsn(long offset) => (int)(offset / Os9Layout.SectorSize);

  /// <summary>Converts a sector (LSN) number to a byte offset.</summary>
  public long LsnToOffset(int lsn) => (long)lsn * Os9Layout.SectorSize;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var oldLsn = OffsetToLsn(oldOffset);
    var newLsn = OffsetToLsn(newOffset);
    var sectorCount = (int)((length + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize);

    // Read identification sector.
    var id = ReadSector(image, 0);
    var totalSectors = ReadU24Be(id, Os9Layout.Pd_DD_TOT);
    var bitmapBytes = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(Os9Layout.Pd_DD_MAP));
    var rootFdLsn = ReadU24Be(id, Os9Layout.Pd_DD_DIR);

    var bitmapSectors = (bitmapBytes + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    var bitmap = ReadSectors(image, Os9Layout.BitmapLsn, bitmapSectors);

    // Find the file's FD LSN by walking the root directory.
    var rootFd = ReadSector(image, rootFdLsn);
    var rootSize = (long)BinaryPrimitives.ReadUInt32BigEndian(rootFd.AsSpan(Os9Layout.FD_SIZ));

    var fileFdLsn = FindFileFd(image, rootFd, rootSize, fileName);
    if (fileFdLsn <= 0) return;

    // Read the file's FD sector.
    var fd = ReadSector(image, fileFdLsn);

    // Walk the segment list and patch entries that overlap the old range.
    var segOff = Os9Layout.FD_SEG;
    while (segOff + Os9Layout.SegmentBytes <= fd.Length) {
      var startLsn = ReadU24Be(fd, segOff);
      var sectors = BinaryPrimitives.ReadUInt16BigEndian(fd.AsSpan(segOff + 3));
      if (startLsn == 0) break;

      // Check if this segment overlaps the old range.
      if (startLsn == oldLsn && sectors == sectorCount) {
        // Exact match: replace with new location.
        WriteU24Be(fd, segOff, newLsn);
        BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(segOff + 3), (ushort)sectorCount);

        // Update bitmap: free old, allocate new.
        for (var i = 0; i < sectorCount; i++) {
          MarkFree(bitmap, oldLsn + i);
          MarkAllocated(bitmap, newLsn + i);
        }

        // Write back FD and bitmap.
        WriteSector(image, fileFdLsn, fd);
        image.Flush(); // Crash barrier: FD update durable before bitmap.
        WriteSectors(image, Os9Layout.BitmapLsn, bitmap);
        // Crash barrier: metadata commit durable before return.
        image.Flush();
        return;
      }

      // Partial overlap within this segment.
      if (startLsn <= oldLsn && startLsn + sectors >= oldLsn + sectorCount) {
        // The moved range is a sub-range of this segment. Split it.
        var beforeCount = oldLsn - startLsn;
        var afterStart = newLsn;
        var afterLsnOrigEnd = oldLsn + sectorCount;
        var afterCount = (startLsn + sectors) - afterLsnOrigEnd;

        // Rebuild segment list from this point.
        var newSegs = new List<(int Start, int Count)>();
        if (beforeCount > 0) newSegs.Add((startLsn, beforeCount));
        newSegs.Add((newLsn, sectorCount));
        if (afterCount > 0) newSegs.Add((afterLsnOrigEnd, afterCount));

        // Replace current segment and shift subsequent ones.
        var remainingSegs = new List<(int Start, int Count)>();
        var nextOff = segOff + Os9Layout.SegmentBytes;
        while (nextOff + Os9Layout.SegmentBytes <= fd.Length) {
          var ns = ReadU24Be(fd, nextOff);
          if (ns == 0) break;
          var nc = BinaryPrimitives.ReadUInt16BigEndian(fd.AsSpan(nextOff + 3));
          remainingSegs.Add((ns, nc));
          nextOff += Os9Layout.SegmentBytes;
        }

        // Write all segments starting at segOff.
        var writeOff = segOff;
        foreach (var (s, c) in newSegs) {
          if (writeOff + Os9Layout.SegmentBytes > fd.Length) break;
          WriteU24Be(fd, writeOff, s);
          BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(writeOff + 3), (ushort)c);
          writeOff += Os9Layout.SegmentBytes;
        }
        foreach (var (s, c) in remainingSegs) {
          if (writeOff + Os9Layout.SegmentBytes > fd.Length) break;
          WriteU24Be(fd, writeOff, s);
          BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(writeOff + 3), (ushort)c);
          writeOff += Os9Layout.SegmentBytes;
        }
        // Zero remaining segment slots.
        while (writeOff + Os9Layout.SegmentBytes <= fd.Length) {
          WriteU24Be(fd, writeOff, 0);
          BinaryPrimitives.WriteUInt16BigEndian(fd.AsSpan(writeOff + 3), 0);
          writeOff += Os9Layout.SegmentBytes;
        }

        // Update bitmap.
        for (var i = 0; i < sectorCount; i++) {
          MarkFree(bitmap, oldLsn + i);
          MarkAllocated(bitmap, newLsn + i);
        }

        WriteSector(image, fileFdLsn, fd);
        image.Flush(); // Crash barrier: FD update durable before bitmap.
        WriteSectors(image, Os9Layout.BitmapLsn, bitmap);
        // Crash barrier: metadata commit durable before return.
        image.Flush();
        return;
      }

      segOff += Os9Layout.SegmentBytes;
    }
  }

  // ── Root directory walking ────────────────────────────────────────────

  private static int FindFileFd(Stream image, byte[] rootFd, long rootSize, string fileName) {
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
          if (sec[off] == 0) continue;
          var name = ReadHighBitTerminatedAscii(sec.AsSpan(off, Os9Layout.DirEntryNameMaxBytes));
          if (string.Equals(name, fileName, StringComparison.Ordinal)) {
            return ReadU24Be(sec, off + Os9Layout.DirEntryFdLsnOffset);
          }
        }
      }
      segOff += Os9Layout.SegmentBytes;
    }
    return 0;
  }

  // ── Sector I/O ─────────────────────────────────────────────────────────

  private static byte[] ReadSector(Stream s, int lsn) {
    var buf = new byte[Os9Layout.SectorSize];
    s.Position = (long)lsn * Os9Layout.SectorSize;
    s.ReadExactly(buf);
    return buf;
  }

  private static byte[] ReadSectors(Stream s, int firstLsn, int count) {
    var buf = new byte[count * Os9Layout.SectorSize];
    s.Position = (long)firstLsn * Os9Layout.SectorSize;
    s.ReadExactly(buf);
    return buf;
  }

  private static void WriteSector(Stream s, int lsn, byte[] data) {
    s.Position = (long)lsn * Os9Layout.SectorSize;
    s.Write(data, 0, Os9Layout.SectorSize);
  }

  private static void WriteSectors(Stream s, int firstLsn, byte[] data) {
    s.Position = (long)firstLsn * Os9Layout.SectorSize;
    s.Write(data, 0, data.Length);
  }

  // ── Bitmap helpers ─────────────────────────────────────────────────────

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

  // ── Helpers ────────────────────────────────────────────────────────────

  private static int ReadU24Be(byte[] span, int offset)
    => (span[offset] << 16) | (span[offset + 1] << 8) | span[offset + 2];

  private static void WriteU24Be(byte[] span, int offset, int value) {
    span[offset + 0] = (byte)((value >> 16) & 0xFF);
    span[offset + 1] = (byte)((value >> 8) & 0xFF);
    span[offset + 2] = (byte)(value & 0xFF);
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
