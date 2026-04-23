#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Writer for Microware OS-9 RBF (Random-Block-File) disk images. Emits a
/// 35-track DSDD CoCo geometry (322 560 bytes / ~315 KB, 1260 sectors of 256
/// bytes, cluster size 1). Files are placed contiguously starting after the
/// bitmap + root descriptor + root directory; each file gets one file
/// descriptor sector and a single contiguous segment.
/// </summary>
public sealed class Os9RbfWriter {

  /// <summary>
  /// Builds an OS-9 RBF image. Filenames may be at most 28 ASCII characters.
  /// </summary>
  /// <param name="files">Files to embed in the root directory.</param>
  /// <param name="volumeName">Volume label (max 31 ASCII chars).</param>
  public static byte[] Build(
    IReadOnlyList<(string Name, byte[] Data)> files,
    string volumeName = "CWBOS9") {

    foreach (var (name, _) in files) {
      if (name.Length > Os9Layout.DirEntryNameMaxBytes - 1)
        throw new InvalidOperationException(
          $"OS-9 RBF: filename \"{name}\" exceeds {Os9Layout.DirEntryNameMaxBytes - 1} characters.");
      if (!IsAsciiPrintable(name))
        throw new InvalidOperationException(
          $"OS-9 RBF: filename \"{name}\" contains non-printable ASCII characters.");
    }

    var image = new byte[Os9Layout.TotalBytes];

    // ── Allocate sectors ─────────────────────────────────────────────────
    // Plan layout:
    //   LSN 0           : identification sector
    //   LSN 1..1+B-1    : allocation bitmap (B sectors)
    //   LSN dirFdLsn    : root directory descriptor (1 sector)
    //   LSN dirDataLsn..: root directory data (one or more sectors)
    //   LSN dataStart.. : per-file FD sector + file data (contiguous)
    var dirFdLsn = Os9Layout.BitmapLsn + Os9Layout.BitmapSectors;

    // Each directory entry is 32 bytes; we add "." and ".." plus user files.
    var dirEntryCount = files.Count + 2;
    var dirDataSectors = (dirEntryCount * Os9Layout.DirEntryBytes + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    if (dirDataSectors == 0) dirDataSectors = 1;
    var dirDataLsn = dirFdLsn + 1;
    var nextLsn = dirDataLsn + dirDataSectors;

    // For each file: 1 FD sector + ceil(size / 256) data sectors.
    var fileFdLsn = new int[files.Count];
    var fileDataLsn = new int[files.Count];
    var fileDataSectors = new int[files.Count];
    for (var i = 0; i < files.Count; i++) {
      fileFdLsn[i] = nextLsn++;
      var dataLen = files[i].Data.Length;
      var dataSec = (dataLen + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
      if (dataSec == 0) {
        fileDataLsn[i] = 0;
        fileDataSectors[i] = 0;
      } else {
        fileDataLsn[i] = nextLsn;
        fileDataSectors[i] = dataSec;
        nextLsn += dataSec;
      }
    }

    if (nextLsn > Os9Layout.TotalSectors)
      throw new ArgumentException(
        $"OS-9 RBF: layout requires {nextLsn} sectors, exceeds {Os9Layout.TotalSectors} sector capacity.", nameof(files));

    // ── Identification sector ────────────────────────────────────────────
    var id = image.AsSpan(0, Os9Layout.SectorSize);
    WriteU24Be(id, Os9Layout.Pd_DD_TOT, Os9Layout.TotalSectors);
    id[Os9Layout.Pd_DD_TKS] = (byte)Os9Layout.SectorsPerTrack;
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_MAP..], (ushort)Os9Layout.BitmapBytes);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_BIT..], (ushort)Os9Layout.ClusterSizeSectors);
    WriteU24Be(id, Os9Layout.Pd_DD_DIR, dirFdLsn);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_OWN..], 0);
    id[Os9Layout.Pd_DD_ATT] = 0xFF; // permissions
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_DSK..], (ushort)Random.Shared.Next(0, ushort.MaxValue));
    id[Os9Layout.Pd_DD_FMT] = 0x03; // double-sided, double-density
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_SPT..], (ushort)Os9Layout.SectorsPerTrack);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_RES..], 0);
    WriteU24Be(id, Os9Layout.Pd_DD_BT, 0);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_BSZ..], 0);

    var now = DateTime.Now;
    id[Os9Layout.Pd_DD_DAT + 0] = (byte)(now.Year % 100);
    id[Os9Layout.Pd_DD_DAT + 1] = (byte)now.Month;
    id[Os9Layout.Pd_DD_DAT + 2] = (byte)now.Day;
    id[Os9Layout.Pd_DD_DAT + 3] = (byte)now.Hour;
    id[Os9Layout.Pd_DD_DAT + 4] = (byte)now.Minute;
    WriteHighBitTerminatedAscii(id[Os9Layout.Pd_DD_NAM..], volumeName, Os9Layout.SectorSize - Os9Layout.Pd_DD_NAM);

    // ── Allocation bitmap ───────────────────────────────────────────────
    // Bit i = 1 means cluster i is allocated. Cluster size = 1 sector.
    var bitmap = image.AsSpan(Os9Layout.BitmapLsn * Os9Layout.SectorSize, Os9Layout.BitmapSectors * Os9Layout.SectorSize);
    for (var lsn = 0; lsn < nextLsn; lsn++) MarkAllocated(bitmap, lsn);
    // Trailing tail of bitmap covering non-existent sectors should also read as 1
    // so allocators don't try to use them. Set bits past TotalSectors.
    for (var bit = Os9Layout.TotalSectors; bit < Os9Layout.BitmapBytes * 8; bit++) MarkAllocated(bitmap, bit);

    // ── Root directory file descriptor ──────────────────────────────────
    var rootFd = image.AsSpan(dirFdLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
    rootFd[Os9Layout.FD_ATT] = Os9Layout.DefaultDirAttr;
    BinaryPrimitives.WriteUInt16BigEndian(rootFd[Os9Layout.FD_OWN..], 0);
    rootFd[Os9Layout.FD_DAT + 0] = (byte)(now.Year % 100);
    rootFd[Os9Layout.FD_DAT + 1] = (byte)now.Month;
    rootFd[Os9Layout.FD_DAT + 2] = (byte)now.Day;
    rootFd[Os9Layout.FD_DAT + 3] = (byte)now.Hour;
    rootFd[Os9Layout.FD_DAT + 4] = (byte)now.Minute;
    rootFd[Os9Layout.FD_LNK] = 1;
    var dirByteLen = (uint)(dirEntryCount * Os9Layout.DirEntryBytes);
    BinaryPrimitives.WriteUInt32BigEndian(rootFd[Os9Layout.FD_SIZ..], dirByteLen);
    rootFd[Os9Layout.FD_CRE + 0] = (byte)(now.Year % 100);
    rootFd[Os9Layout.FD_CRE + 1] = (byte)now.Month;
    rootFd[Os9Layout.FD_CRE + 2] = (byte)now.Day;
    // Single segment list: dirDataLsn / dirDataSectors.
    WriteU24Be(rootFd, Os9Layout.FD_SEG + 0, dirDataLsn);
    BinaryPrimitives.WriteUInt16BigEndian(rootFd[(Os9Layout.FD_SEG + 3)..], (ushort)dirDataSectors);
    // (segments terminator is implicit zero LSN — already 0 from initialisation)

    // ── Root directory entries ──────────────────────────────────────────
    var dirData = image.AsSpan(dirDataLsn * Os9Layout.SectorSize, dirDataSectors * Os9Layout.SectorSize);
    var entryOff = 0;
    // "." → root itself
    WriteHighBitTerminatedAscii(dirData[entryOff..], ".", Os9Layout.DirEntryNameMaxBytes);
    WriteU24Be(dirData, entryOff + Os9Layout.DirEntryFdLsnOffset, dirFdLsn);
    entryOff += Os9Layout.DirEntryBytes;
    // ".." → root again (no parent on top-level)
    WriteHighBitTerminatedAscii(dirData[entryOff..], "..", Os9Layout.DirEntryNameMaxBytes);
    WriteU24Be(dirData, entryOff + Os9Layout.DirEntryFdLsnOffset, dirFdLsn);
    entryOff += Os9Layout.DirEntryBytes;

    for (var i = 0; i < files.Count; i++) {
      WriteHighBitTerminatedAscii(dirData[entryOff..], files[i].Name, Os9Layout.DirEntryNameMaxBytes);
      WriteU24Be(dirData, entryOff + Os9Layout.DirEntryFdLsnOffset, fileFdLsn[i]);
      entryOff += Os9Layout.DirEntryBytes;
    }

    // ── Per-file FD + payload ───────────────────────────────────────────
    for (var i = 0; i < files.Count; i++) {
      var fd = image.AsSpan(fileFdLsn[i] * Os9Layout.SectorSize, Os9Layout.SectorSize);
      fd[Os9Layout.FD_ATT] = Os9Layout.DefaultFileAttr;
      BinaryPrimitives.WriteUInt16BigEndian(fd[Os9Layout.FD_OWN..], 0);
      fd[Os9Layout.FD_DAT + 0] = (byte)(now.Year % 100);
      fd[Os9Layout.FD_DAT + 1] = (byte)now.Month;
      fd[Os9Layout.FD_DAT + 2] = (byte)now.Day;
      fd[Os9Layout.FD_DAT + 3] = (byte)now.Hour;
      fd[Os9Layout.FD_DAT + 4] = (byte)now.Minute;
      fd[Os9Layout.FD_LNK] = 1;
      BinaryPrimitives.WriteUInt32BigEndian(fd[Os9Layout.FD_SIZ..], (uint)files[i].Data.Length);
      fd[Os9Layout.FD_CRE + 0] = (byte)(now.Year % 100);
      fd[Os9Layout.FD_CRE + 1] = (byte)now.Month;
      fd[Os9Layout.FD_CRE + 2] = (byte)now.Day;
      if (fileDataSectors[i] > 0) {
        WriteU24Be(fd, Os9Layout.FD_SEG + 0, fileDataLsn[i]);
        BinaryPrimitives.WriteUInt16BigEndian(fd[(Os9Layout.FD_SEG + 3)..], (ushort)fileDataSectors[i]);
        // Copy file data
        files[i].Data.CopyTo(image.AsSpan(fileDataLsn[i] * Os9Layout.SectorSize));
      }
    }

    return image;
  }

  private static bool IsAsciiPrintable(string s) {
    foreach (var c in s) if (c is < (char)0x20 or > (char)0x7E) return false;
    return true;
  }

  private static void MarkAllocated(Span<byte> bitmap, int bit) {
    var byteIdx = bit / 8;
    if (byteIdx >= bitmap.Length) return;
    bitmap[byteIdx] |= (byte)(0x80 >> (bit % 8));
  }

  private static void WriteU24Be(Span<byte> span, int offset, int value) {
    span[offset + 0] = (byte)((value >> 16) & 0xFF);
    span[offset + 1] = (byte)((value >> 8) & 0xFF);
    span[offset + 2] = (byte)(value & 0xFF);
  }

  internal static void WriteHighBitTerminatedAscii(Span<byte> dest, string text, int maxBytes) {
    if (string.IsNullOrEmpty(text)) return;
    var bytes = Encoding.ASCII.GetBytes(text);
    var n = Math.Min(bytes.Length, maxBytes);
    if (n == 0) return;
    for (var i = 0; i < n; i++) dest[i] = (byte)(bytes[i] & 0x7F);
    dest[n - 1] |= 0x80; // last char carries MSB
  }
}
