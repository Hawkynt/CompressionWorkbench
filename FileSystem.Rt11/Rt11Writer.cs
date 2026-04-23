#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Rt11;

/// <summary>
/// Writer for RT-11 disk images using the canonical RX01 reference geometry
/// (256 256 bytes, 1 directory segment of 2 blocks at block 6, files stored
/// contiguously in 512-byte blocks). Filenames are 6.3 RAD-50 encoded.
/// </summary>
public sealed class Rt11Writer {

  /// <summary>
  /// Builds a fresh RT-11 image. <paramref name="files"/> are stored contiguously
  /// starting after the directory segment; sizes are rounded up to whole 512-byte
  /// blocks. Filenames must consist only of RAD-50 characters (A-Z, 0-9, $, .).
  /// </summary>
  /// <param name="files">Files to embed; names are split into 6-char stem and 3-char extension.</param>
  /// <param name="volumeId">Volume identifier (12 chars max, ASCII, padded with spaces).</param>
  /// <param name="dirSegments">Number of directory segments to allocate (default 1; max 31).</param>
  public static byte[] Build(
    IReadOnlyList<(string Name, byte[] Data)> files,
    string volumeId = "RT11A   ",
    int dirSegments = 1) {

    if (dirSegments is < 1 or > 31)
      throw new ArgumentOutOfRangeException(nameof(dirSegments), "RT-11 supports 1..31 directory segments.");

    // Validate filenames upfront so we don't half-write an image then throw.
    var splitNames = new (string Stem, string Ext)[files.Count];
    for (var i = 0; i < files.Count; i++) {
      splitNames[i] = SplitName(files[i].Name);
      if (!Rad50.IsValid(splitNames[i].Stem) || !Rad50.IsValid(splitNames[i].Ext))
        throw new InvalidOperationException(
          $"RT-11: filename \"{files[i].Name}\" contains characters outside the RAD-50 alphabet (A-Z, 0-9, $, .).");
    }

    var maxFilesPerSeg = Rt11Layout.EntriesPerSegment - 1; // last entry is EOS terminator
    if (files.Count > dirSegments * maxFilesPerSeg)
      throw new ArgumentException(
        $"RT-11: too many files ({files.Count} > {dirSegments * maxFilesPerSeg}); increase dirSegments.", nameof(files));

    var image = new byte[Rt11Layout.ImageBytes];

    // ── Home block (block 1) ───────────────────────────────────────────────
    var homeOff = Rt11Layout.HomeBlock * Rt11Layout.BlockSize;
    // First-directory-segment block number at canonical offset 0x1D4.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(homeOff + 0x1D4), Rt11Layout.FirstDirSegment);
    // System version "V3A" RAD-50 at 0x1D6 (per manual; not strictly required).
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(homeOff + 0x1D6), Rad50.EncodeWord("V3A", 0));
    // Volume identifier (12 ASCII chars, space-padded) at 0x1D8.
    var volIdBytes = Encoding.ASCII.GetBytes(SanitizeAscii(volumeId, 12));
    volIdBytes.CopyTo(image.AsSpan(homeOff + 0x1D8));
    // Owner name (12 ASCII, space-padded) at 0x1E4.
    Encoding.ASCII.GetBytes(SanitizeAscii("CWB         ", 12)).CopyTo(image.AsSpan(homeOff + 0x1E4));
    // System ident "DECRT11A    " at 0x1F0.
    Encoding.ASCII.GetBytes(Rt11Layout.HomeBlockSignature).CopyTo(image.AsSpan(homeOff + Rt11Layout.HomeBlockSignatureOffset));

    // ── Directory segments + file data ─────────────────────────────────────
    // Files start immediately after the directory segments.
    var dataStartBlock = Rt11Layout.FirstDirSegment + dirSegments * Rt11Layout.DirSegmentBlocks;
    var nextFileBlock = dataStartBlock;

    var fileExtents = new (int StartBlock, int LengthBlocks)[files.Count];
    for (var i = 0; i < files.Count; i++) {
      var lenBlocks = (files[i].Data.Length + Rt11Layout.BlockSize - 1) / Rt11Layout.BlockSize;
      if (lenBlocks == 0) lenBlocks = 0; // RT-11 allows zero-block files
      fileExtents[i] = (nextFileBlock, lenBlocks);
      nextFileBlock += lenBlocks;
    }

    if (nextFileBlock > Rt11Layout.ImageBlocks)
      throw new ArgumentException(
        $"RT-11: total data ({(nextFileBlock - dataStartBlock) * Rt11Layout.BlockSize} bytes) exceeds RX01 capacity.", nameof(files));

    // Today's RT-11 date.
    var today = DateTime.Today;
    var dateWord = EncodeDate(today);

    // For now, we write a single segment that contains all entries (we capped above).
    // Each segment's `start_data_block` field points to the first data block managed
    // by that segment. With a single segment, that's `dataStartBlock`.
    var segIdx = 0; // segment number 1
    var segByteOff = (Rt11Layout.FirstDirSegment + segIdx * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(segByteOff + 0), (ushort)dirSegments);   // seg_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(segByteOff + 2), 0);                     // next_seg (0 = last)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(segByteOff + 4), 1);                     // highest_seg_in_use
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(segByteOff + 6), 0);                     // extra_bytes_per_entry
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(segByteOff + 8), (ushort)dataStartBlock);// start_data_block

    var entryOff = segByteOff + Rt11Layout.DirSegmentHeaderBytes;
    for (var i = 0; i < files.Count; i++) {
      var (stem, ext) = splitNames[i];
      var (nh, nl) = Rad50.EncodeName6(stem);
      var tw = Rad50.EncodeType3(ext);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0), Rt11Layout.E_PERM);  // status: permanent
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 2), nh);                 // name high
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 4), nl);                 // name low
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 6), tw);                 // file type
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 8), (ushort)fileExtents[i].LengthBlocks);
      image[entryOff + 10] = 0;                                                                  // channel byte
      image[entryOff + 11] = 0;                                                                  // job byte
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 12), dateWord);          // creation date
      entryOff += Rt11Layout.DirEntryBytes;
    }

    // EOS terminator.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0), Rt11Layout.E_EOS);

    // ── File payloads ──────────────────────────────────────────────────────
    for (var i = 0; i < files.Count; i++) {
      var startByte = (long)fileExtents[i].StartBlock * Rt11Layout.BlockSize;
      var data = files[i].Data;
      data.CopyTo(image.AsSpan((int)startByte));
    }

    return image;
  }

  internal static (string Stem, string Ext) SplitName(string fileName) {
    if (string.IsNullOrEmpty(fileName)) return ("", "");
    var dot = fileName.LastIndexOf('.');
    var stem = dot < 0 ? fileName : fileName[..dot];
    var ext = dot < 0 ? "" : fileName[(dot + 1)..];
    if (stem.Length > 6) stem = stem[..6];
    if (ext.Length > 3) ext = ext[..3];
    return (stem.ToUpperInvariant(), ext.ToUpperInvariant());
  }

  private static ushort EncodeDate(DateTime d) {
    var year = d.Year;
    if (year < 1972) year = 1972;
    if (year > 2027) year = 2027;
    var age = (year - 1972) / 32;          // 0..1
    var yearLow = (year - 1972) % 32;       // 0..31
    var word = (ushort)(((age & 0x3) << 14) | ((yearLow & 0x1F) << 9) | ((d.Day & 0x1F) << 5) | (d.Month & 0x1F));
    return word;
  }

  private static string SanitizeAscii(string s, int width) {
    var chars = new char[width];
    for (var i = 0; i < width; i++) chars[i] = ' ';
    var max = Math.Min(s.Length, width);
    for (var i = 0; i < max; i++) {
      var c = s[i];
      chars[i] = c is >= (char)0x20 and < (char)0x7F ? c : '?';
    }
    return new string(chars);
  }
}
