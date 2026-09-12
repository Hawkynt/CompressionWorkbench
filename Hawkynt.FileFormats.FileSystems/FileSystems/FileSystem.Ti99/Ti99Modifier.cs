#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ti99;

/// <summary>
/// In-place modifier for TI-99/4A DSR sector-dump (.dsk) images. Performs
/// add / remove with strict <b>O(touched bytes)</b> I/O — only the VIB
/// (sector 0, holding the allocation bitmap), the FDIR (sector 1, holding the
/// File Descriptor Record pointers), the one FDR sector for the affected file,
/// and that file's contiguous data run are read or written. The rest of the
/// image is untouched, so existing files' data bytes stay byte-identical at
/// their original offsets and a same-size update never changes the image
/// length.
///
/// <para>The companion <see cref="Ti99Writer"/> rebuilds an image from
/// scratch; this class is the "I have an existing image, mutate it" path.</para>
///
/// <para>Layout reminders (256-byte sectors, big-endian):
/// <list type="bullet">
///   <item>VIB at sector 0: total sectors u16 BE @0x0A; allocation bitmap @0x38..0xFF (bit set = used).</item>
///   <item>FDIR at sector 1: 128 × u16 BE FDR-sector pointers (0 = empty slot).</item>
///   <item>FDR (256 bytes): name @0x00 (10 ASCII, space-padded), flags @0x0C, RPS @0x0D,
///         total-sectors u16 BE @0x0E, EOF byte @0x10, LRL @0x11, #records u16 BE @0x12,
///         cluster chain @0x1C (3-byte packed start-sector + offset).</item>
///   <item>The writer + reader lay file data out as one contiguous run starting at the
///         FDR's first cluster sector, so this modifier allocates contiguous runs too.</item>
/// </list></para>
/// </summary>
public static class Ti99Modifier {
  private const int SectorSize = 256;
  private const int VibSector = 0;
  private const int FdirSector = 1;
  private const int BitmapOffset = 0x38;
  private const int FdirEntries = 128;

  /// <summary>True if the stream looks like a parseable TI-99 sector dump
  /// (not a TIFiles wrapper, which has no allocation map to mutate).</summary>
  public static bool IsSectorDump(Stream image) {
    if (image.Length < SectorSize * 2) return false;
    var vib = ReadSector(image, VibSector);
    return vib[0x0D] == (byte)'D' && vib[0x0E] == (byte)'S' && vib[0x0F] == (byte)'K';
  }

  /// <summary>
  /// Adds a file to the existing sector-dump image. Allocates one FDR sector
  /// plus a contiguous data run from the bitmap, writes the FDR + data,
  /// records the FDR pointer in the FDIR, and marks the new sectors used.
  /// </summary>
  /// <exception cref="IOException">Disk full or directory full.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var vib = ReadSector(image, VibSector);
    var fdir = ReadSector(image, FdirSector);
    var totalSectors = BinaryPrimitives.ReadUInt16BigEndian(vib.AsSpan(0x0A, 2));
    if (totalSectors < 2) throw new InvalidDataException("TI-99: invalid total sector count.");

    // Find a free FDIR slot.
    var fdirSlot = -1;
    for (var i = 0; i < FdirEntries; i++) {
      if (BinaryPrimitives.ReadUInt16BigEndian(fdir.AsSpan(i * 2, 2)) == 0) { fdirSlot = i; break; }
    }
    if (fdirSlot < 0) throw new IOException("TI-99: directory full (128 FDR slots).");

    var dataSectors = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);

    // Allocate one sector for the FDR (anywhere free) + a contiguous run for the data.
    var fdrSector = AllocateOne(vib, totalSectors);
    if (fdrSector < 0) throw new IOException("TI-99: disk full (no free sector for FDR).");
    var startSector = AllocateContiguous(vib, totalSectors, dataSectors);
    if (startSector < 0) {
      // Roll back the FDR allocation before failing.
      ClearBitmap(vib, fdrSector);
      throw new IOException($"TI-99: disk full (no contiguous run of {dataSectors} sectors).");
    }

    // Build + write the FDR.
    var fdr = new byte[SectorSize];
    var fdrName = Encoding.ASCII.GetBytes(SanitizeName(name));
    fdrName.CopyTo(fdr.AsSpan(0));
    fdr[0x0C] = 0x02; // file-status: matches the writer / lenient reader
    fdr[0x0D] = 1;    // records per sector
    BinaryPrimitives.WriteUInt16BigEndian(fdr.AsSpan(0x0E, 2), (ushort)dataSectors);
    var eofByte = data.Length - (dataSectors - 1) * SectorSize;
    if (eofByte <= 0 || eofByte > 255) eofByte = 0;
    fdr[0x10] = (byte)eofByte;
    fdr[0x11] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(fdr.AsSpan(0x12, 2), (ushort)dataSectors);
    // Cluster chain entry (3-byte packed start-sector + offset) at 0x1C.
    fdr[0x1C] = (byte)(startSector & 0xFF);
    fdr[0x1D] = (byte)((startSector >> 8) & 0x0F);
    var offsetField = dataSectors - 1;
    fdr[0x1D] |= (byte)((offsetField & 0x0F) << 4);
    fdr[0x1E] = (byte)((offsetField >> 4) & 0xFF);
    WriteSector(image, fdrSector, fdr);

    // Write the data run (one contiguous span; pad the last sector with zeros).
    WriteRun(image, startSector, data, dataSectors);

    // Record the FDR pointer in the FDIR and persist it.
    BinaryPrimitives.WriteUInt16BigEndian(fdir.AsSpan(fdirSlot * 2, 2), (ushort)fdrSector);
    WriteSector(image, FdirSector, fdir);

    // Persist the bitmap (VIB).
    WriteSector(image, VibSector, vib);
  }

  /// <summary>
  /// Removes the named file. Frees the file's data sectors and its FDR sector
  /// in the bitmap, optionally wipes them, and clears the FDIR pointer slot.
  /// Returns true if the file was found and removed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var vib = ReadSector(image, VibSector);
    var fdir = ReadSector(image, FdirSector);
    var needle = SanitizeName(name).TrimEnd();

    for (var i = 0; i < FdirEntries; i++) {
      var fdrSector = BinaryPrimitives.ReadUInt16BigEndian(fdir.AsSpan(i * 2, 2));
      if (fdrSector == 0) continue;
      if ((long)(fdrSector + 1) * SectorSize > image.Length) continue;
      var fdr = ReadSector(image, fdrSector);
      var entryName = ReadAscii(fdr.AsSpan(0, 10)).TrimEnd();
      if (!string.Equals(entryName, needle, StringComparison.OrdinalIgnoreCase)) continue;

      var totalSectors = BinaryPrimitives.ReadUInt16BigEndian(fdr.AsSpan(0x0E, 2));
      var b0 = fdr[0x1C];
      var b1 = fdr[0x1D];
      var startSector = b0 | ((b1 & 0x0F) << 8);

      // Free + optionally wipe the data run.
      if (totalSectors > 0 && startSector > 0) {
        for (var s = 0; s < totalSectors; s++) ClearBitmap(vib, startSector + s);
        if (wipeData) {
          var zero = new byte[SectorSize];
          for (var s = 0; s < totalSectors; s++)
            if ((long)(startSector + s + 1) * SectorSize <= image.Length)
              WriteSector(image, startSector + s, zero);
        }
      }

      // Free + optionally wipe the FDR sector.
      ClearBitmap(vib, fdrSector);
      if (wipeData) WriteSector(image, fdrSector, new byte[SectorSize]);

      // Clear the FDIR pointer slot.
      BinaryPrimitives.WriteUInt16BigEndian(fdir.AsSpan(i * 2, 2), 0);
      WriteSector(image, FdirSector, fdir);
      WriteSector(image, VibSector, vib);
      return true;
    }
    return false;
  }

  // ── Bitmap helpers ──────────────────────────────────────────────────

  private static bool IsUsed(byte[] vib, int sector) {
    var byteIdx = BitmapOffset + sector / 8;
    if (byteIdx >= SectorSize) return true; // out of bitmap range → treat as unusable
    return (vib[byteIdx] & (1 << (sector & 7))) != 0;
  }

  private static void SetBitmap(byte[] vib, int sector) {
    var byteIdx = BitmapOffset + sector / 8;
    if (byteIdx >= SectorSize) return;
    vib[byteIdx] |= (byte)(1 << (sector & 7));
  }

  private static void ClearBitmap(byte[] vib, int sector) {
    var byteIdx = BitmapOffset + sector / 8;
    if (byteIdx >= SectorSize) return;
    vib[byteIdx] &= (byte)~(1 << (sector & 7));
  }

  /// <summary>Allocate a single free sector (used for the FDR). Marks it used.</summary>
  private static int AllocateOne(byte[] vib, int totalSectors) {
    for (var s = 2; s < totalSectors; s++) {
      if (!IsUsed(vib, s)) { SetBitmap(vib, s); return s; }
    }
    return -1;
  }

  /// <summary>Allocate the lowest contiguous run of <paramref name="count"/>
  /// free sectors. Marks them all used. Returns the start sector, or -1.</summary>
  private static int AllocateContiguous(byte[] vib, int totalSectors, int count) {
    var run = 0;
    var start = -1;
    for (var s = 2; s < totalSectors; s++) {
      if (!IsUsed(vib, s)) {
        if (run == 0) start = s;
        run++;
        if (run == count) {
          for (var k = 0; k < count; k++) SetBitmap(vib, start + k);
          return start;
        }
      } else {
        run = 0;
        start = -1;
      }
    }
    return -1;
  }

  // ── Sector I/O ──────────────────────────────────────────────────────

  private static byte[] ReadSector(Stream image, int sector) {
    var buf = new byte[SectorSize];
    image.Position = (long)sector * SectorSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteSector(Stream image, int sector, byte[] data) {
    image.Position = (long)sector * SectorSize;
    image.Write(data, 0, SectorSize);
  }

  private static void WriteRun(Stream image, int startSector, byte[] data, int sectors) {
    image.Position = (long)startSector * SectorSize;
    if (data.Length > 0) image.Write(data, 0, data.Length);
    var totalBytes = sectors * SectorSize;
    var pad = totalBytes - data.Length;
    if (pad > 0) image.Write(new byte[pad], 0, pad);
  }

  // ── Name helpers ────────────────────────────────────────────────────

  private static string SanitizeName(string raw) {
    var leaf = (raw ?? "").Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (leaf.Length > 10) leaf = leaf[..10];
    return leaf.ToUpperInvariant().PadRight(10);
  }

  private static string ReadAscii(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    for (var i = 0; i < span.Length; i++) {
      var c = span[i];
      chars[i] = c is >= 0x20 and < 0x7F ? (char)c : ' ';
    }
    return new string(chars);
  }
}
