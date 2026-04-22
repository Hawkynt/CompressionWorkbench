#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Udf;

/// <summary>
/// Writes a minimal UDF 1.02 filesystem image (ECMA-167). Flat file layout,
/// short allocation descriptors. Computes ECMA-167 §7.2.1 DescriptorCRC
/// (CRC-16/CCITT, init=0, poly=0x1021, non-reflected) and TagChecksum for
/// every descriptor tag so that strict readers (xorriso, Linux udf.ko,
/// mkudffs fsck) accept the produced images.
///
/// Layout:
/// <code>
/// Sectors   0-15:  System area
/// Sector   16:     VRS BEA01
/// Sector   17:     VRS NSR02
/// Sector   18:     VRS TEA01
/// Sector   32-34:  Main VDS (PVD + Partition + LVD + Terminator)
/// Sector  256:     AVDP
/// Sector  257:     Partition start: File Set Descriptor (FSD)
/// Sector  258:     Root directory File Entry
/// Sector  259+:    Root directory FID data
/// Then per file:   File Entry sector + data sectors
/// </code>
/// </summary>
public sealed class UdfWriter {
  private const int Sector = 2048;
  private const int PartitionStartSector = 257;

  // Descriptor body sizes per ECMA-167 §10. The body starts at offset 16
  // (after the 16-byte descriptor tag) and DescriptorCRCLength covers
  // exactly these many bytes. Using fixed structure sizes (rather than
  // the full sector) keeps us compatible with real UDF implementations.
  private const int PvdBodySize = 496;          // AVDP/PVD/PD sector size 512 - 16 tag
  private const int AvdpBodySize = 496;
  private const int PdBodySize = 496;
  private const int LvdBodySize = 440 - 16;     // 440 header + zero partition maps
  private const int TerminatorBodySize = 496;
  private const int FsdBodySize = 496;
  private const int FeBodyHeader = 160;         // 176 - 16 (up to L_EA), plus L_EA + L_AD content

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  public void WriteTo(Stream output) {
    // Pre-compute layout within partition (LBNs relative to partition start).
    //   LBN 0: FSD
    //   LBN 1: Root dir FE
    //   LBN 2: Root dir FID data (may span multiple sectors)
    var fidData = BuildFidData(out var fileEntryLbns, out var fileDataLbns);
    var fidSectors = (fidData.Length + Sector - 1) / Sector;
    // fileEntryLbns[i] and fileDataLbns[i] are assigned inside BuildFidData.

    var totalPartitionSectors = 2 + fidSectors; // FSD + rootFE + FIDs
    foreach (var (_, data) in _files)
      totalPartitionSectors += 1 + (data.Length + Sector - 1) / Sector; // FE + data
    var totalImageSectors = PartitionStartSector + totalPartitionSectors;

    // ── Write system area (sectors 0-15) ──
    WritePadding(output, 16);

    // ── Write VRS (sectors 16-18) ──
    WriteVrs(output, "BEA01");
    WriteVrs(output, "NSR02");
    WriteVrs(output, "TEA01");

    // ── Padding to sector 32 ──
    WritePadding(output, 32 - 19);

    // ── Main VDS at sectors 32-35 ──
    WritePvd(output, 32, totalImageSectors);
    WritePartitionDescriptor(output, 33, PartitionStartSector, totalPartitionSectors);
    WriteLvd(output, 34);
    WriteTerminator(output, 35);

    // ── Padding to sector 256 ──
    WritePadding(output, 256 - 36);

    // ── AVDP at sector 256 ──
    WriteAvdp(output, 256, mainVdsLoc: 32, mainVdsLen: 4 * Sector);

    // ── Partition data (starting at sector 257 = LBN 0) ──
    WriteFsd(output, lbn: 0, rootIcbLbn: 1);
    WriteDirectoryFe(output, lbn: 1, fidData.Length);
    // FID data (padded to sector boundary)
    output.Write(fidData);
    var fidPad = fidSectors * Sector - fidData.Length;
    if (fidPad > 0) output.Write(new byte[fidPad]);

    // Per-file: File Entry + data
    for (var i = 0; i < _files.Count; i++) {
      var (_, data) = _files[i];
      WriteFileFe(output, fileEntryLbns[i], data.Length, fileDataLbns[i]);
      output.Write(data);
      var dataPad = ((data.Length + Sector - 1) / Sector) * Sector - data.Length;
      if (dataPad > 0) output.Write(new byte[dataPad]);
    }
  }

  // ── FID building ──────────────────────────────────────────────────────────

  private byte[] BuildFidData(out int[] feEntryLbns, out int[] dataLbns) {
    feEntryLbns = new int[_files.Count];
    dataLbns = new int[_files.Count];

    using var ms = new MemoryStream();

    // Parent FID (flags=0x0A: parent + directory)
    WriteFid(ms, 0x0A, 1, "");

    // Compute where file FEs and data will go.
    // After FSD (1) + rootFE (1) + fidSectors(?), we have file entries.
    // This is circular: fidData length depends on file count, and file LBNs
    // depend on fidData length. Pre-compute fidData size to break the loop.
    var fidSizeEstimate = FidSize(""); // parent FID
    foreach (var (name, _) in _files)
      fidSizeEstimate += FidSize(name);
    var fidSectors = (fidSizeEstimate + Sector - 1) / Sector;
    var nextLbn = 2 + fidSectors; // after FSD(0) + rootFE(1) + fids(2..2+fidSectors-1)

    for (var i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      feEntryLbns[i] = nextLbn++;
      dataLbns[i] = nextLbn;
      nextLbn += Math.Max(1, (data.Length + Sector - 1) / Sector);
      WriteFid(ms, 0x00, feEntryLbns[i], name); // flags=0 = file
    }

    return ms.ToArray();
  }

  private static int FidSize(string name) {
    var nameBytes = name.Length == 0 ? 0 : 1 + Encoding.UTF8.GetByteCount(name); // CS0 byte + UTF8
    var size = 38 + nameBytes;
    return (size + 3) & ~3; // pad to 4
  }

  private static void WriteFid(Stream s, byte flags, int icbLbn, string name) {
    var nameBytes = name.Length == 0 ? [] : EncodeCs0(name);
    var fidLen = 38 + nameBytes.Length;
    var padded = (fidLen + 3) & ~3;
    var buf = new byte[padded];

    // Tag: FID = 257
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), 257);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2); // descriptor version
    buf[18] = flags;
    buf[19] = (byte)nameBytes.Length; // identifier length
    // ICB at offset 20: long_ad (16 bytes) — length(4) + lbn(4) + partRef(2) + impl(6)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)Sector);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), (uint)icbLbn);
    // lIU at offset 36 = 0
    // Name at offset 38
    nameBytes.CopyTo(buf, 38);

    // ECMA-167 §14.4: FID DescriptorCRCLength covers the entire padded FID
    // minus the 16-byte tag.
    FinalizeTag(buf, 0, padded - 16);

    s.Write(buf);
  }

  private static byte[] EncodeCs0(string name) {
    var utf8 = Encoding.UTF8.GetBytes(name);
    var result = new byte[1 + utf8.Length];
    result[0] = 8; // CS0 compression ID = UTF-8
    utf8.CopyTo(result, 1);
    return result;
  }

  // ── Descriptor writers ────────────────────────────────────────────────────

  private static void WriteTag(byte[] buf, int off, ushort tagId, uint tagLocation) {
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), tagId);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), 2); // descriptor version
    // DescriptorCRC (off+8..9), DescriptorCRCLength (off+10..11), TagChecksum (off+4)
    // filled in by FinalizeTag after the descriptor body is populated.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 12), tagLocation);
  }

  /// <summary>
  /// Finalizes a UDF descriptor tag per ECMA-167 §7.2.1 by computing the
  /// CRC-16/CCITT (init=0, poly=0x1021, non-reflected) over <paramref name="bodyLength"/>
  /// bytes starting at <c>tagOffset + 16</c>, storing it in the tag at offsets 8..9,
  /// writing the DescriptorCRCLength at offsets 10..11, and finally computing the
  /// byte-sum-mod-256 TagChecksum at offset 4.
  /// </summary>
  private static void FinalizeTag(byte[] buf, int tagOffset, int bodyLength) {
    var bodyStart = tagOffset + 16;
    if (bodyStart + bodyLength > buf.Length)
      bodyLength = buf.Length - bodyStart;
    if (bodyLength < 0) bodyLength = 0;

    var crc = Crc16Ccitt.Compute(buf.AsSpan(bodyStart, bodyLength));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 8), crc);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 10), (ushort)bodyLength);

    // TagChecksum = (sum of bytes [0..3, 5..15]) mod 256. Byte at offset 4
    // is excluded (it IS the checksum) and must be zero while computing.
    buf[tagOffset + 4] = 0;
    byte sum = 0;
    for (var i = 0; i < 16; i++) {
      if (i == 4) continue;
      sum = (byte)(sum + buf[tagOffset + i]);
    }
    buf[tagOffset + 4] = sum;
  }

  private static void WritePadding(Stream output, int sectors) {
    for (var i = 0; i < sectors; i++) output.Write(new byte[Sector]);
  }

  private static void WriteVrs(Stream output, string id) {
    var buf = new byte[Sector];
    buf[0] = 0; // structure type
    Encoding.ASCII.GetBytes(id).CopyTo(buf, 1);
    buf[6] = 1; // structure version
    output.Write(buf);
  }

  private static void WriteAvdp(Stream output, int sectorNum, int mainVdsLoc, int mainVdsLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 2, (uint)sectorNum);
    // Main VDS extent: length(4) + location(4) at offset 16
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), (uint)mainVdsLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)mainVdsLoc);
    FinalizeTag(buf, 0, AvdpBodySize);
    output.Write(buf);
  }

  private static void WritePvd(Stream output, int sectorNum, int totalSectors) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 1, (uint)sectorNum); // Primary Volume Descriptor
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 1); // VDS number
    Encoding.ASCII.GetBytes("UDF Volume").CopyTo(buf, 24); // volume identifier (dstring at 24, 32 bytes)
    FinalizeTag(buf, 0, PvdBodySize);
    output.Write(buf);
  }

  private static void WritePartitionDescriptor(Stream output, int sectorNum, int partStart, int partLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 5, (uint)sectorNum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(188), (uint)partStart);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(192), (uint)partLen);
    FinalizeTag(buf, 0, PdBodySize);
    output.Write(buf);
  }

  private void WriteLvd(Stream output, int sectorNum) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 6, (uint)sectorNum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(212), (uint)Sector); // logical block size
    // FSD location: long_ad at offset 248 (length=4, lbn=4, partRef=2, impl=6)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(248), (uint)Sector); // extent length
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(252), 0); // FSD LBN = 0
    // partRef at 256 = 0 (default)
    FinalizeTag(buf, 0, LvdBodySize);
    output.Write(buf);
  }

  private static void WriteTerminator(Stream output, int sectorNum) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 8, (uint)sectorNum);
    FinalizeTag(buf, 0, TerminatorBodySize);
    output.Write(buf);
  }

  private static void WriteFsd(Stream output, int lbn, int rootIcbLbn) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 256, (uint)lbn);
    // Root ICB: long_ad at offset 400
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(400), (uint)Sector);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(404), (uint)rootIcbLbn);
    FinalizeTag(buf, 0, FsdBodySize);
    output.Write(buf);
  }

  private static void WriteDirectoryFe(Stream output, int lbn, int dirDataLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 261, (uint)lbn);
    // ICB tag at offset 16: strategy type etc — keep zeros
    buf[27] = 4; // file type = directory
    // icb flags at offset 34: adType=0 (short ADs)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0);
    // info length at offset 56
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)dirDataLen);
    // L_EA at 168 = 0
    // L_AD at 172 = 8 (one short AD)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), 8);
    // Short AD at 176: length(4) + position/LBN(4)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(176), (uint)dirDataLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(180), 2); // LBN 2 = FID data
    // File Entry body: fixed 176-byte header + L_EA + L_AD bytes of variable part.
    // Tag covers 16 bytes, so CRC body = 176 - 16 (header) + L_EA(0) + L_AD(8) = 168.
    FinalizeTag(buf, 0, FeBodyHeader + 0 + 8);
    output.Write(buf);
  }

  private static void WriteFileFe(Stream output, int lbn, int fileSize, int dataLbn) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 261, (uint)lbn);
    buf[27] = 5; // file type = file (regular)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0); // adType=0 short
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)fileSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), 8); // L_AD = 8
    var allocLen = Math.Max(fileSize, Sector); // at least one sector
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(176), (uint)allocLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(180), (uint)dataLbn);
    FinalizeTag(buf, 0, FeBodyHeader + 0 + 8);
    output.Write(buf);
  }
}
