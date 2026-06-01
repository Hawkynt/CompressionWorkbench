#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ti99;

/// <summary>
/// Writes TI-99/4A disk images in either of two formats:
/// <list type="number">
///   <item><description><b>TIFiles wrapper</b> — 128-byte header (magic 0x07 + "TIFILES")
///   followed by a single file's raw bytes. The image holds exactly one file by
///   spec; if multiple inputs are supplied only the first is honoured.</description></item>
///   <item><description><b>Sector dump (.dsk)</b> — VIB at sector 0 (with "DSK" tag at
///   offset 0x0D), File Descriptor Index Record (FDIR) at sector 1, then one
///   File Descriptor Record per file, plus the file data laid out contiguously
///   starting after the FDR slots.</description></item>
/// </list>
///
/// <para><b>Flat by spec.</b> The TI-99/4A DSR filesystem (Disk Subsystem
/// Resource) has no subdirectory concept — the FDIR is a flat array of FDR
/// pointers. Hierarchical inputs are flattened to their leaf names.</para>
///
/// <para><b>Spec.</b> TI-99/4A Disk Manager and the published DSR format docs;
/// TIFiles per the standard cross-platform interchange format (Cory's TIFiles
/// docs).</para>
/// </summary>
public sealed class Ti99Writer {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Add a file. Filename will be uppercased and truncated to 10
  /// chars (the TI-99 DSR limit); subdir prefixes are stripped.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = name.Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (leaf.Length > 10) leaf = leaf[..10];
    _files.Add((leaf.ToUpperInvariant(), data));
  }

  /// <summary>Builds a TIFiles wrapper around the first added file. Multi-file
  /// inputs collapse to the first file (TIFiles is single-file by spec).</summary>
  public byte[] BuildTifiles() {
    if (_files.Count == 0)
      throw new InvalidOperationException("Ti99 TIFiles requires at least one file.");
    var (name, data) = _files[0];
    var sectors = (data.Length + Ti99Reader.SectorSize - 1) / Ti99Reader.SectorSize;
    if (sectors == 0) sectors = 1;
    var img = new byte[Ti99Reader.TifilesHeaderSize + sectors * Ti99Reader.SectorSize];
    // 0x07 + "TIFILES"
    img[0] = 0x07;
    Encoding.ASCII.GetBytes("TIFILES").CopyTo(img.AsSpan(1));
    // u16 sectors-used (BE) at offset 8
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(8, 2), (ushort)sectors);
    // flags + records/sector
    img[10] = 0x80; // variable-record-length flag (most common for arbitrary data)
    img[11] = 1;    // records per sector
    // name at offset 16..25 (10 bytes space-padded ASCII)
    var nameBytes = Encoding.ASCII.GetBytes(name.PadRight(10).Substring(0, 10));
    nameBytes.CopyTo(img.AsSpan(16));
    // Copy payload.
    data.CopyTo(img.AsSpan(Ti99Reader.TifilesHeaderSize, data.Length));
    return img;
  }

  /// <summary>
  /// Builds a sector-dump (.dsk) image with the standard TI-99 layout: VIB at
  /// sector 0, FDIR at sector 1, FDR records at sectors 2..(2+N-1), payload
  /// data laid out contiguously starting at the first sector after the FDRs.
  /// </summary>
  /// <param name="tracks">35 or 40 (typical TI-99 floppy geometry).</param>
  /// <param name="sectorsPerTrack">9 (SS/SD), 18 (DS/DD), or 8.</param>
  /// <param name="sides">1 or 2.</param>
  /// <param name="diskName">Volume name (uppercased, 10 chars space-padded).</param>
  public byte[] BuildSectorDump(int tracks = 40, int sectorsPerTrack = 9, int sides = 2, string diskName = "DISK") {
    if (_files.Count > 127)
      throw new InvalidOperationException("TI-99 FDIR holds at most 127 file pointers (sector 1, 254 bytes / 2 each).");
    var totalSectors = tracks * sectorsPerTrack * sides;
    if (totalSectors < 16) totalSectors = 720; // default fallback
    var img = new byte[totalSectors * Ti99Reader.SectorSize];

    // ── VIB at sector 0 ───────────────────────────────────────────────
    var vib = img.AsSpan(0, Ti99Reader.SectorSize);
    var nameBytes = Encoding.ASCII.GetBytes(diskName.ToUpperInvariant().PadRight(10).Substring(0, 10));
    nameBytes.CopyTo(vib);
    BinaryPrimitives.WriteUInt16BigEndian(vib.Slice(0x0A, 2), (ushort)totalSectors);
    vib[0x0C] = (byte)sectorsPerTrack;
    vib[0x0D] = (byte)'D'; vib[0x0E] = (byte)'S'; vib[0x0F] = (byte)'K';
    vib[0x10] = 0;                              // protection
    vib[0x11] = (byte)tracks;
    vib[0x12] = (byte)sides;
    vib[0x13] = sectorsPerTrack >= 16 ? (byte)2 : (byte)1;  // density: 1=SD, 2=DD
    // Allocation bitmap at offset 0x38 — mark sectors 0 (VIB), 1 (FDIR),
    // 2..(2+files) (FDRs), and the file-data sectors as in-use.
    var bitmap = vib.Slice(0x38);

    // ── Lay out files ──────────────────────────────────────────────────
    // FDRs occupy sectors 2..(2+N-1). File data starts at first free sector
    // immediately after the FDRs.
    var fdrCount = _files.Count;
    var nextFreeSector = 2 + fdrCount;

    // FDIR at sector 1: array of up to 127 BE u16 sector pointers to FDRs.
    var fdir = img.AsSpan(1 * Ti99Reader.SectorSize, Ti99Reader.SectorSize);

    MarkBitmap(bitmap, 0);
    MarkBitmap(bitmap, 1);

    for (var i = 0; i < fdrCount; i++) {
      var (fname, data) = _files[i];
      var fdrSector = 2 + i;
      MarkBitmap(bitmap, fdrSector);
      BinaryPrimitives.WriteUInt16BigEndian(fdir.Slice(i * 2, 2), (ushort)fdrSector);

      var sectorsUsed = (data.Length + Ti99Reader.SectorSize - 1) / Ti99Reader.SectorSize;
      if (sectorsUsed == 0) sectorsUsed = 1;
      if (nextFreeSector + sectorsUsed > totalSectors)
        throw new InvalidOperationException(
          $"TI-99: not enough space for {fname} — need {sectorsUsed} sectors, " +
          $"have {totalSectors - nextFreeSector}.");

      // FDR layout (256 bytes; minimal subset matching the reader).
      var fdr = img.AsSpan(fdrSector * Ti99Reader.SectorSize, Ti99Reader.SectorSize);
      var fdrName = Encoding.ASCII.GetBytes(fname.ToUpperInvariant().PadRight(10).Substring(0, 10));
      fdrName.CopyTo(fdr);
      fdr[0x0C] = 0x02; // file-status: internal/program-like; this matches reader's lenient parser
      fdr[0x0D] = 1;    // records per sector
      BinaryPrimitives.WriteUInt16BigEndian(fdr.Slice(0x0E, 2), (ushort)sectorsUsed);
      var eofByte = data.Length - (sectorsUsed - 1) * Ti99Reader.SectorSize;
      if (eofByte <= 0 || eofByte > 255) eofByte = 0;
      fdr[0x10] = (byte)eofByte;
      fdr[0x11] = 0; // logical record length (variable)
      BinaryPrimitives.WriteUInt16BigEndian(fdr.Slice(0x12, 2), (ushort)sectorsUsed);

      // Cluster chain at +0x1C — minimal single-run 3-byte entry encoding
      // (matches Ti99Reader.ParseFdr): packed start_sector + offset.
      var startSector = nextFreeSector;
      fdr[0x1C] = (byte)(startSector & 0xFF);
      fdr[0x1D] = (byte)((startSector >> 8) & 0x0F);
      // offset bits in fdr[0x1D] high nibble + fdr[0x1E] = (offset = sectorsUsed - 1)
      var offsetField = sectorsUsed - 1;
      fdr[0x1D] |= (byte)((offsetField & 0x0F) << 4);
      fdr[0x1E] = (byte)((offsetField >> 4) & 0xFF);

      // Copy payload into sectors [startSector, startSector+sectorsUsed).
      data.CopyTo(img.AsSpan(startSector * Ti99Reader.SectorSize, data.Length));
      for (var s = 0; s < sectorsUsed; s++) MarkBitmap(bitmap, startSector + s);
      nextFreeSector += sectorsUsed;
    }
    return img;
  }

  private static void MarkBitmap(Span<byte> bitmap, int sector) {
    var byteIdx = sector / 8;
    if (byteIdx >= bitmap.Length) return;
    bitmap[byteIdx] |= (byte)(1 << (sector & 7));
  }
}
