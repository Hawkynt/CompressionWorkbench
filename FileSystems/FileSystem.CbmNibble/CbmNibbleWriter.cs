#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CbmNibble;

/// <summary>
/// Writer for Commodore 1541 GCR-encoded G64 disk images, the canonical
/// emulator format consumed by VICE, CCS64 and related toolchains.
///
/// <para><b>What this writer emits.</b> A G64 v2 container with the standard
/// 35-track / 84-half-track layout (the 1541's physical capacity), each
/// half-track tagged with its correct speed zone (tracks 1-17 = zone 3 ⇒
/// 21 sectors; 18-24 = zone 2 ⇒ 19 sectors; 25-30 = zone 1 ⇒ 18 sectors;
/// 31-35 = zone 0 ⇒ 17 sectors). Track 18 (the BAM/directory track) hosts
/// a minimal but well-formed CBM-DOS BAM block at sector 0 plus a single
/// 8-entry directory block at sector 1; data files are placed on tracks
/// 1-17 in track-order and GCR-encoded with full SYNC + header gap +
/// data gap framing exactly as the 1541 ROM lays them out.</para>
///
/// <para><b>Scope.</b> Real CBM DOS allocation, file-link sector chaining
/// and BAM bitmap updates are intentionally simplified — this writer's
/// goal is to produce a G64 image that VICE will <i>mount</i> and that the
/// matching reader will round-trip, not to be byte-identical with a
/// 1541-formatted disk produced by a real C64. The header, speed table,
/// track offset table, SYNC/gap framing and 4-to-5 GCR nibble encoding
/// are spec-correct (see VICE's <c>g64.txt</c> and the 1541 Service
/// Manual); the DOS-level metadata is the minimum required for the
/// reader to surface the per-track payload.</para>
///
/// <para><b>Spec sources.</b> VICE source tree <c>doc/g64.txt</c>
/// (G64 v2 file format), Commodore 1541 Service Manual (GCR encoding
/// tables, speed zones), Pasi Ojala's "C2N232 documentation" notes on
/// 4-to-5 GCR nibble pairs, "Inside Commodore DOS" (Immers/Neufeld, 1984)
/// for sector framing and BAM layout.</para>
/// </summary>
public sealed class CbmNibbleWriter {

  /// <summary>G64 v2 magic "GCR-1541" + NUL (9 bytes).</summary>
  internal static readonly byte[] G64Signature = "GCR-1541"u8.ToArray();

  /// <summary>Default G64 v2 maximum-track-size field (bytes). 7928 is the
  /// reference value VICE uses for full unprotected images; it leaves room
  /// for 21 standard sectors plus all gap/SYNC framing on zone-3 tracks.</summary>
  public const int DefaultMaxTrackSize = 7928;

  /// <summary>1541 physical half-track count: tracks 1..35 plus an unused
  /// 36 half-tracks slot at index 0 — totals 84 half-track entries.</summary>
  public const int StandardHalfTrackCount = 84;

  /// <summary>Standard CBM 1541 track count.</summary>
  public const int StandardTrackCount = 35;

  /// <summary>Sector count per speed zone (1541 ROM table).
  /// Index 0 unused; indices 1..35 = physical track number. Array length 36.</summary>
  internal static readonly int[] SectorsPerTrack = [
    0,                                                      // 0  (unused — track numbering starts at 1)
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21,                // 1-10 zone 3
    21, 21, 21, 21, 21, 21, 21,                            // 11-17 zone 3
    19, 19, 19, 19, 19, 19, 19,                            // 18-24 zone 2
    18, 18, 18, 18, 18, 18,                                // 25-30 zone 1
    17, 17, 17, 17, 17,                                    // 31-35 zone 0
  ];

  /// <summary>Speed-zone table indexed by physical track (1..35).
  /// Index 0 unused; array length 36.</summary>
  internal static readonly uint[] SpeedZonePerTrack = [
    0,                                                      // 0  (unused)
    3, 3, 3, 3, 3, 3, 3, 3, 3, 3,                          // 1-10 zone 3
    3, 3, 3, 3, 3, 3, 3,                                   // 11-17 zone 3
    2, 2, 2, 2, 2, 2, 2,                                   // 18-24 zone 2
    1, 1, 1, 1, 1, 1,                                      // 25-30 zone 1
    0, 0, 0, 0, 0,                                         // 31-35 zone 0
  ];

  /// <summary>
  /// 1541 4-to-5 GCR nibble encoding table. Maps a 4-bit nibble (0..15)
  /// to the canonical 5-bit GCR code that the 1541's read-head accepts
  /// without spurious SYNC matches. From the 1541 Service Manual.
  /// </summary>
  internal static readonly byte[] GcrNibble = [
    0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17,
    0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15,
  ];

  /// <summary>1541 standard sector payload (raw user bytes per sector).</summary>
  public const int SectorPayloadBytes = 256;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the volume. Up to 8 files fit in the single
  /// directory block; further entries are accepted but the directory will
  /// only surface the first eight when read back.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>
  /// Builds the G64 v2 image.
  /// </summary>
  /// <param name="diskName">CBM disk label (max 16 chars; PETSCII-mapped to
  /// ASCII uppercase here for portability).</param>
  /// <param name="diskId">Two-character disk ID (e.g. "01"). Stored verbatim
  /// in the BAM block at offsets 0xA2..0xA3.</param>
  /// <param name="maxTrackSize">Per-track byte budget (header field at
  /// offset 0x0A). Defaults to <see cref="DefaultMaxTrackSize"/>.</param>
  public byte[] Build(string diskName = "WORMDISK", string diskId = "WW",
      int maxTrackSize = DefaultMaxTrackSize) {

    var halfTracks = StandardHalfTrackCount;

    // Build the per-track GCR byte buffers. Index 0..83 = half-track 0..83;
    // even-numbered half-tracks (0,2,4,...) correspond to whole tracks 1..35.
    // Odd half-tracks are left empty so the writer matches the 1541's physical
    // step pattern (whole-track-step images).
    var trackData = new byte[halfTracks][];
    for (var i = 0; i < halfTracks; i++) trackData[i] = [];

    // Lay files out across tracks 1..17 (zone 3, 21 sectors each).
    // Track 18 hosts the BAM + directory. Tracks 19..35 are left blank.
    var sectorQueue = new Queue<(int Track, int Sector)>();
    for (var t = 1; t <= 17; t++)
      for (var s = 0; s < SectorsPerTrack[t]; s++)
        sectorQueue.Enqueue((t, s));

    // Per-sector raw payloads, keyed by (track, sector).
    var sectorPayloads = new Dictionary<(int T, int S), byte[]>();
    var fileDirEntries = new List<(string Name, int StartTrack, int StartSector, int SectorCount)>();

    foreach (var (name, data) in this._files) {
      var sectorsNeeded = (data.Length + SectorPayloadBytes - 1) / SectorPayloadBytes;
      if (sectorsNeeded == 0) sectorsNeeded = 1;
      if (sectorQueue.Count < sectorsNeeded)
        throw new InvalidOperationException(
          $"G64: file '{name}' needs {sectorsNeeded} sectors but only {sectorQueue.Count} are free on tracks 1..17.");

      var allocated = new List<(int T, int S)>(sectorsNeeded);
      for (var i = 0; i < sectorsNeeded; i++) allocated.Add(sectorQueue.Dequeue());

      for (var i = 0; i < sectorsNeeded; i++) {
        var (t, s) = allocated[i];
        var payload = new byte[SectorPayloadBytes];
        var byteOffset = i * SectorPayloadBytes;
        var copyLen = Math.Min(SectorPayloadBytes, data.Length - byteOffset);
        if (copyLen > 0) Array.Copy(data, byteOffset, payload, 0, copyLen);
        sectorPayloads[(t, s)] = payload;
      }
      var (startT, startS) = allocated[0];
      fileDirEntries.Add((name, startT, startS, sectorsNeeded));
    }

    // Build the BAM block at track 18, sector 0.
    sectorPayloads[(18, 0)] = BuildBamSector(diskName, diskId);
    // Build the directory block at track 18, sector 1 (single block, holds 8 entries).
    sectorPayloads[(18, 1)] = BuildDirectorySector(fileDirEntries);

    // Now GCR-encode every populated track in full (all 21/19/18/17 sectors)
    // with SYNC + header + data framing. Empty sectors get a payload of zeros.
    for (var t = 1; t <= StandardTrackCount; t++) {
      var nSectors = SectorsPerTrack[t];
      using var ms = new MemoryStream();
      for (var s = 0; s < nSectors; s++) {
        var payload = sectorPayloads.TryGetValue((t, s), out var p) ? p : new byte[SectorPayloadBytes];
        WriteEncodedSector(ms, (byte)t, (byte)s, diskId, payload);
      }
      // Pad to maxTrackSize so VICE-style readers see a full physical revolution.
      var encoded = ms.ToArray();
      if (encoded.Length > maxTrackSize)
        throw new InvalidOperationException(
          $"G64: encoded track {t} ({encoded.Length} bytes) exceeds maxTrackSize {maxTrackSize}.");
      var padded = new byte[maxTrackSize];
      encoded.CopyTo(padded, 0);
      for (var i = encoded.Length; i < padded.Length; i++) padded[i] = 0x55;  // gap pattern
      // Whole-track-step image: half-track index 2*(t-1) holds the data.
      trackData[2 * (t - 1)] = padded;
    }

    return AssembleContainer(trackData, maxTrackSize);
  }

  /// <summary>Frames + GCR-encodes a single 256-byte sector and writes the
  /// resulting nibble stream into <paramref name="sink"/>. Layout per 1541
  /// ROM:
  /// <list type="bullet">
  ///   <item>5 × 0xFF SYNC bytes</item>
  ///   <item>header: $08 tag, checksum, sector#, track#, id-lo, id-hi, $0F, $0F</item>
  ///   <item>9-byte header gap (0x55)</item>
  ///   <item>5 × 0xFF SYNC bytes</item>
  ///   <item>data: $07 tag, 256 payload bytes, checksum, $00, $00</item>
  ///   <item>8-byte tail gap (0x55)</item>
  /// </list>
  /// All header + data fields are GCR-encoded with 4-to-5 nibble mapping.</summary>
  private static void WriteEncodedSector(MemoryStream sink, byte track, byte sector,
      string diskId, byte[] payload) {

    // Header (8 raw bytes pre-GCR).
    var idLo = diskId.Length > 0 ? (byte)diskId[0] : (byte)'0';
    var idHi = diskId.Length > 1 ? (byte)diskId[1] : (byte)'0';
    var headerChecksum = (byte)(sector ^ track ^ idLo ^ idHi);
    var header = new byte[] { 0x08, headerChecksum, sector, track, idLo, idHi, 0x0F, 0x0F };

    // Data block (260 raw bytes pre-GCR: tag + 256 payload + checksum + 2 × 00).
    var dataChecksum = (byte)0;
    foreach (var b in payload) dataChecksum ^= b;
    var dataBlock = new byte[260];
    dataBlock[0] = 0x07;
    Array.Copy(payload, 0, dataBlock, 1, 256);
    dataBlock[257] = dataChecksum;
    dataBlock[258] = 0x00;
    dataBlock[259] = 0x00;

    // Frame: SYNC, GCR(header), gap, SYNC, GCR(data), gap.
    for (var i = 0; i < 5; i++) sink.WriteByte(0xFF);
    var encodedHeader = GcrEncodeBlock(header);
    sink.Write(encodedHeader);
    for (var i = 0; i < 9; i++) sink.WriteByte(0x55);
    for (var i = 0; i < 5; i++) sink.WriteByte(0xFF);
    var encodedData = GcrEncodeBlock(dataBlock);
    sink.Write(encodedData);
    for (var i = 0; i < 8; i++) sink.WriteByte(0x55);
  }

  /// <summary>4-to-5 GCR encode every byte in <paramref name="raw"/>: each
  /// byte's high and low nibble emit a 5-bit GCR code, eight bytes of input
  /// expand to ten bytes of output (8 × 8 bits = 64 → 8 × 10 bits = 80).</summary>
  internal static byte[] GcrEncodeBlock(ReadOnlySpan<byte> raw) {
    // Total bits = raw.Length * 10. Round up to whole bytes.
    var totalBits = raw.Length * 10;
    var outLen = (totalBits + 7) / 8;
    var output = new byte[outLen];
    var bitPos = 0;

    foreach (var b in raw) {
      var hi = GcrNibble[(b >> 4) & 0x0F];
      var lo = GcrNibble[b & 0x0F];
      EmitBits(output, ref bitPos, hi, 5);
      EmitBits(output, ref bitPos, lo, 5);
    }
    return output;
  }

  private static void EmitBits(byte[] dst, ref int bitPos, byte value, int width) {
    for (var i = width - 1; i >= 0; i--) {
      var bit = (value >> i) & 1;
      var byteIdx = bitPos / 8;
      var bitIdx = 7 - (bitPos % 8);
      if (bit != 0) dst[byteIdx] |= (byte)(1 << bitIdx);
      bitPos++;
    }
  }

  /// <summary>
  /// Builds the BAM (Block Availability Map) sector for track 18, sector 0.
  /// Layout (per "Inside Commodore DOS"):
  /// <list type="bullet">
  ///   <item>0x00 next track (0x12)</item>
  ///   <item>0x01 next sector (0x01)</item>
  ///   <item>0x02 DOS version 'A'</item>
  ///   <item>0x03 NUL</item>
  ///   <item>0x04..0x8F BAM bitmap — 4 bytes per track × 35 tracks</item>
  ///   <item>0x90..0x9F disk name PETSCII padded with 0xA0</item>
  ///   <item>0xA0..0xA1 0xA0 padding</item>
  ///   <item>0xA2..0xA3 disk ID</item>
  ///   <item>0xA4 0xA0</item>
  ///   <item>0xA5..0xA6 DOS type "2A"</item>
  /// </list>
  /// </summary>
  private byte[] BuildBamSector(string diskName, string diskId) {
    var sector = new byte[SectorPayloadBytes];
    for (var i = 0; i < sector.Length; i++) sector[i] = 0xA0;  // PETSCII shifted-space pad

    sector[0] = 0x12;            // next track = 18
    sector[1] = 0x01;            // next sector = 1
    sector[2] = (byte)'A';       // DOS version
    sector[3] = 0x00;

    // BAM bitmap: 4 bytes per track. byte 0 = free-sector count, bytes 1..3 = sector bitmap.
    for (var t = 1; t <= StandardTrackCount; t++) {
      var off = 4 + (t - 1) * 4;
      var nSectors = SectorsPerTrack[t];
      // Mark all sectors free initially.
      sector[off] = (byte)nSectors;
      // Bitmap: bit set = free. 24 bits cover sectors 0..23.
      var bm = (1u << nSectors) - 1;
      sector[off + 1] = (byte)(bm & 0xFF);
      sector[off + 2] = (byte)((bm >> 8) & 0xFF);
      sector[off + 3] = (byte)((bm >> 16) & 0xFF);
    }

    // Mark sectors 0 and 1 of track 18 as allocated (BAM + directory blocks).
    var bamOff = 4 + (18 - 1) * 4;
    sector[bamOff] = (byte)(SectorsPerTrack[18] - 2);
    sector[bamOff + 1] &= 0b11111100;  // clear bits 0 and 1

    // Mark file-occupied sectors on tracks 1..17 as allocated.
    foreach (var (track, sectors) in this.AllocatedFileSectors())
      foreach (var s in sectors) {
        var trkOff = 4 + (track - 1) * 4;
        if (s < 8) sector[trkOff + 1] &= (byte)~(1 << s);
        else if (s < 16) sector[trkOff + 2] &= (byte)~(1 << (s - 8));
        else if (s < 24) sector[trkOff + 3] &= (byte)~(1 << (s - 16));
        sector[trkOff]--;
      }

    // Disk name (16 PETSCII bytes, padded with 0xA0).
    var nameBytes = Encoding.ASCII.GetBytes(diskName.ToUpperInvariant());
    Array.Copy(nameBytes, 0, sector, 0x90, Math.Min(nameBytes.Length, 16));

    // Disk ID at 0xA2..0xA3, DOS type "2A" at 0xA5..0xA6.
    sector[0xA2] = (byte)diskId[0];
    sector[0xA3] = diskId.Length > 1 ? (byte)diskId[1] : (byte)'0';
    sector[0xA4] = 0xA0;
    sector[0xA5] = (byte)'2';
    sector[0xA6] = (byte)'A';
    return sector;
  }

  /// <summary>Recomputes the set of (track, sector) pairs that the file
  /// layout pass would allocate for the current <see cref="_files"/> list,
  /// so the BAM bitmap-clear pass can mark them used.</summary>
  private IEnumerable<(int Track, int[] Sectors)> AllocatedFileSectors() {
    var queue = new Queue<(int T, int S)>();
    for (var t = 1; t <= 17; t++)
      for (var s = 0; s < SectorsPerTrack[t]; s++)
        queue.Enqueue((t, s));
    foreach (var (_, data) in this._files) {
      var sectorsNeeded = Math.Max(1, (data.Length + SectorPayloadBytes - 1) / SectorPayloadBytes);
      var bySector = new Dictionary<int, List<int>>();
      for (var i = 0; i < sectorsNeeded; i++) {
        if (queue.Count == 0) break;
        var (t, s) = queue.Dequeue();
        if (!bySector.TryGetValue(t, out var list)) bySector[t] = list = [];
        list.Add(s);
      }
      foreach (var kv in bySector) yield return (kv.Key, kv.Value.ToArray());
    }
  }

  /// <summary>
  /// Builds the single directory sector at track 18, sector 1. Holds up to
  /// 8 entries × 32 bytes each. Layout per entry:
  /// <list type="bullet">
  ///   <item>0x00 next-block track (0 for first entry, then 0xFF after last)</item>
  ///   <item>0x01 next-block sector (0xFF if no more)</item>
  ///   <item>0x02 file type: 0x82 = PRG closed</item>
  ///   <item>0x03 start track</item>
  ///   <item>0x04 start sector</item>
  ///   <item>0x05..0x14 filename (16 bytes, padded with 0xA0)</item>
  ///   <item>0x1E..0x1F file size in blocks (u16 LE)</item>
  /// </list>
  /// </summary>
  private static byte[] BuildDirectorySector(
      List<(string Name, int StartTrack, int StartSector, int SectorCount)> entries) {

    var sector = new byte[SectorPayloadBytes];
    // First two bytes are the chain pointer for *this* directory sector. We use
    // a single dir block so set them to (0, 0xFF) = end of directory chain.
    sector[0] = 0x00;
    sector[1] = 0xFF;

    for (var i = 0; i < entries.Count && i < 8; i++) {
      var e = entries[i];
      var off = i * 32;
      // After the first entry's track/sector header, each subsequent entry's
      // 0x00..0x01 acts as a within-block continuation marker — zero them out.
      if (i > 0) {
        sector[off + 0] = 0x00;
        sector[off + 1] = 0x00;
      }
      sector[off + 2] = 0x82;  // PRG closed
      sector[off + 3] = (byte)e.StartTrack;
      sector[off + 4] = (byte)e.StartSector;
      // Filename area: 16 PETSCII bytes padded with 0xA0.
      for (var j = 0; j < 16; j++) sector[off + 5 + j] = 0xA0;
      var nameBytes = Encoding.ASCII.GetBytes(e.Name.ToUpperInvariant());
      Array.Copy(nameBytes, 0, sector, off + 5, Math.Min(nameBytes.Length, 16));
      // Block count at 0x1E..0x1F (little-endian).
      sector[off + 0x1E] = (byte)(e.SectorCount & 0xFF);
      sector[off + 0x1F] = (byte)((e.SectorCount >> 8) & 0xFF);
    }
    return sector;
  }

  /// <summary>Assembles the final G64 v2 container: 12-byte header, offset
  /// table, speed table, then padded per-track payloads in offset order.</summary>
  private static byte[] AssembleContainer(byte[][] trackData, int maxTrackSize) {
    var halfTracks = trackData.Length;
    var headerSize = 12;
    var offsetTableSize = halfTracks * 4;
    var speedTableSize = halfTracks * 4;
    var tablesEnd = headerSize + offsetTableSize + speedTableSize;

    // For tracks with data, allocate a (u16 length + maxTrackSize) block back-to-back.
    var trackOffsets = new uint[halfTracks];
    var cursor = (uint)tablesEnd;
    for (var i = 0; i < halfTracks; i++) {
      if (trackData[i].Length == 0) {
        trackOffsets[i] = 0;
        continue;
      }
      trackOffsets[i] = cursor;
      cursor += 2 + (uint)maxTrackSize;
    }

    var total = (int)cursor;
    var image = new byte[total];

    // 0x00..0x07 "GCR-1541"
    G64Signature.CopyTo(image, 0);
    // 0x08 version = 0 (G64 v2)
    image[8] = 0x00;
    // 0x09 track count (half-track count, 84 standard)
    image[9] = (byte)halfTracks;
    // 0x0A..0x0B max track size (u16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10, 2), (ushort)maxTrackSize);

    // Offset table
    for (var i = 0; i < halfTracks; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerSize + i * 4, 4), trackOffsets[i]);

    // Speed table: every populated half-track gets the right zone code; empty
    // half-tracks get 0 by default (matches VICE's "no speed override" sentinel).
    for (var i = 0; i < halfTracks; i++) {
      var wholeTrack = (i / 2) + 1;       // half-track index → whole track number
      var speed = (i % 2 == 0 && wholeTrack <= StandardTrackCount && trackData[i].Length > 0)
        ? SpeedZonePerTrack[wholeTrack]
        : 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(
        image.AsSpan(headerSize + offsetTableSize + i * 4, 4), speed);
    }

    // Per-track payload blocks.
    for (var i = 0; i < halfTracks; i++) {
      if (trackOffsets[i] == 0) continue;
      var off = (int)trackOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off, 2), (ushort)trackData[i].Length);
      trackData[i].CopyTo(image, off + 2);
    }
    return image;
  }
}
