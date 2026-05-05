#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Rt11;

/// <summary>
/// Reader for DEC RT-11 disk images. Files are described by a chain of 1024-byte
/// directory segments starting at block 6; each entry references a starting
/// block + length in 512-byte blocks. Filenames are 6.3 RAD-50 encoded.
/// </summary>
public sealed class Rt11Reader {

  /// <summary>One file entry parsed from an RT-11 directory segment.</summary>
  public sealed record FileEntry(
    string Name,             // 6.3 form, e.g. "HELLO.TXT"
    string NameStem,         // 6 chars max
    string Extension,        // 3 chars max
    int StartBlock,
    int LengthBlocks,
    long ByteLength,
    DateTime? Created
  );

  /// <summary>Parsed RT-11 volume — all file entries plus the raw image.</summary>
  public sealed record Volume(
    string VolumeId,
    int FirstSegmentBlock,
    int SegmentsAllocated,
    IReadOnlyList<FileEntry> Files,
    byte[] Image
  );

  /// <summary>
  /// Parses an RT-11 image. Throws <see cref="InvalidDataException"/> if the
  /// home-block signature isn't present.
  /// </summary>
  public static Volume Read(ReadOnlySpan<byte> image) {
    if (image.Length < (Rt11Layout.HomeBlock + 1) * Rt11Layout.BlockSize)
      throw new InvalidDataException("RT-11: image shorter than home block.");

    // Validate home-block signature at block 1 + offset 0x1F0.
    var sigOff = Rt11Layout.HomeBlock * Rt11Layout.BlockSize + Rt11Layout.HomeBlockSignatureOffset;
    if (sigOff + 12 > image.Length)
      throw new InvalidDataException("RT-11: home block signature out of range.");
    var sig = Encoding.ASCII.GetString(image.Slice(sigOff, 12));
    if (sig != Rt11Layout.HomeBlockSignature)
      throw new InvalidDataException($"RT-11: bad home-block signature \"{sig}\" (expected \"{Rt11Layout.HomeBlockSignature}\").");

    // Volume identifier and owner are 12-byte ASCII strings stored at home-block
    // offsets 0x1D6 and 0x1E2 respectively (canonical layout). Read them best-effort.
    var volIdOff = Rt11Layout.HomeBlock * Rt11Layout.BlockSize + 0x1D6;
    var volumeId = volIdOff + 12 <= image.Length
      ? Encoding.ASCII.GetString(image.Slice(volIdOff, 12)).TrimEnd(' ', '\0')
      : "";

    var files = new List<FileEntry>();
    var firstSegBlock = Rt11Layout.FirstDirSegment;
    var segNum = 1; // 1-based segment numbering (segment numbers are 1..seg_count)
    int segmentsAllocated = 0;

    while (segNum != 0) {
      var segByteOff = (firstSegBlock + (segNum - 1) * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;
      if (segByteOff + Rt11Layout.DirSegmentBytes > image.Length) break;
      var seg = image.Slice(segByteOff, Rt11Layout.DirSegmentBytes);

      var segCount = BinaryPrimitives.ReadUInt16LittleEndian(seg);
      var nextSeg = BinaryPrimitives.ReadUInt16LittleEndian(seg[2..]);
      var highest = BinaryPrimitives.ReadUInt16LittleEndian(seg[4..]);
      var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(seg[6..]);
      var dataStart = BinaryPrimitives.ReadUInt16LittleEndian(seg[8..]);
      _ = highest;
      if (segmentsAllocated == 0) segmentsAllocated = segCount;

      var entryStride = Rt11Layout.DirEntryBytes + extraBytes;
      var entryOff = Rt11Layout.DirSegmentHeaderBytes;
      var nextFileBlock = (int)dataStart;

      while (entryOff + entryStride <= seg.Length) {
        var e = seg.Slice(entryOff, Rt11Layout.DirEntryBytes);
        var status = BinaryPrimitives.ReadUInt16LittleEndian(e);
        if ((status & Rt11Layout.E_EOS) != 0) break; // end of segment

        var nameHigh = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
        var nameLow = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
        var typeWord = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
        var sizeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        var dateWord = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);

        // Permanent files: status has E_PERM (0x08) or E_PRE (0x01) set.
        var isPermanent = (status & (Rt11Layout.E_PERM | Rt11Layout.E_PRE)) != 0;
        var isEmpty = (status & Rt11Layout.E_MPTY) != 0;

        if (isPermanent && !isEmpty) {
          var stem = Rad50.DecodeName6(nameHigh, nameLow);
          var ext = Rad50.DecodeType3(typeWord);
          var full = string.IsNullOrEmpty(ext) ? stem : $"{stem}.{ext}";
          var byteLen = (long)sizeBlocks * Rt11Layout.BlockSize;
          files.Add(new FileEntry(
            Name: full,
            NameStem: stem,
            Extension: ext,
            StartBlock: nextFileBlock,
            LengthBlocks: sizeBlocks,
            ByteLength: byteLen,
            Created: DecodeDate(dateWord)));
        }

        nextFileBlock += sizeBlocks;
        entryOff += entryStride;
      }

      segNum = nextSeg;
      // Cap iterations for safety.
      if (files.Count > 10_000) break;
    }

    return new Volume(
      VolumeId: volumeId,
      FirstSegmentBlock: firstSegBlock,
      SegmentsAllocated: segmentsAllocated,
      Files: files,
      Image: image.ToArray());
  }

  /// <summary>Returns the bytes occupied by <paramref name="entry"/> on disk
  /// (always a multiple of 512 bytes, no trailing-byte length information is
  /// stored in RT-11).</summary>
  public static byte[] Extract(Volume v, FileEntry entry) {
    var startByte = (long)entry.StartBlock * Rt11Layout.BlockSize;
    var len = entry.LengthBlocks * Rt11Layout.BlockSize;
    if (startByte + len > v.Image.Length)
      len = (int)Math.Max(0, v.Image.Length - startByte);
    var buf = new byte[len];
    Array.Copy(v.Image, startByte, buf, 0, len);
    return buf;
  }

  /// <summary>
  /// Decodes the RT-11 packed creation date. Bit 14 = age (0 = 1972-1999,
  /// 1 = 2000-2027). Bits 9-13 = year-of-age (0..31), bits 5-8 = day, bits 0-4
  /// = month. Returns null if the fields aren't a valid Gregorian date.
  /// </summary>
  private static DateTime? DecodeDate(ushort w) {
    if (w == 0) return null;
    var month = w & 0x1F;
    var day = (w >> 5) & 0x1F;
    var yearLow = (w >> 9) & 0x1F;
    var age = (w >> 14) & 0x03;
    var year = 1972 + age * 32 + yearLow;
    if (month is < 1 or > 12 || day is < 1 or > 31) return null;
    try { return new DateTime(year, month, day); }
    catch { return null; }
  }
}
