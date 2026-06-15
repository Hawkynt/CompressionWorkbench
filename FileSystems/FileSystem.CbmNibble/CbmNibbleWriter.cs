#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.D64;

namespace FileSystem.CbmNibble;

/// <summary>
/// From-scratch writer for the Commodore nibble container the
/// <see cref="CbmNibbleReader"/> consumes. The Commodore 1541 filesystem is
/// flat — files live in the single directory on track 18 with a BAM — so the
/// writer first builds a standard sectored D64 image (reusing
/// <see cref="D64Writer"/> for the BAM, directory and linked sector chains)
/// and then GCR-encodes every track into the VICE <c>.g64</c> wire format,
/// framing each sector with sync marks, a header block and a data block exactly
/// as a real 1541 lays them down on disk.
/// </summary>
/// <remarks>
/// <para>
/// The reader surfaces each G64 track as an opaque GCR byte buffer; it does not
/// decode GCR. <see cref="DecodeToD64"/> performs the inverse transform so a
/// caller can recover the sectored image (and thus the directory and file
/// contents) from the tracks the reader hands back.
/// </para>
/// </remarks>
public sealed class CbmNibbleWriter {

  private const int SectorSize = 256;
  private const int TotalTracks = 35;

  // 1541 sector layout per track (track 0 is unused).
  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  // 1541 speed zones: outer tracks spin faster (more sectors), inner slower.
  private static byte SpeedZoneFor(int track) => track switch {
    >= 1 and <= 17 => 3,
    >= 18 and <= 24 => 2,
    >= 25 and <= 30 => 1,
    _ => 0,
  };

  // Sync mark: a run of 0xFF bytes the controller uses to lock onto a block.
  private const int SyncLength = 5;
  // Gap bytes between blocks; 0x55 is the conventional 1541 inter-sector gap.
  private const byte GapByte = 0x55;
  private const int HeaderGap = 9;
  private const int TailGap = 8;

  private readonly D64Writer _d64 = new();
  private byte _diskId1 = (byte)'0';
  private byte _diskId2 = (byte)'0';
  private string _diskName = "DISK";

  /// <summary>Sets the on-disk volume name (PETSCII, ≤16 chars) and the 2-byte disk id.</summary>
  public void SetDisk(string name, char id1 = '0', char id2 = '0') {
    _diskName = name ?? "DISK";
    _diskId1 = (byte)id1;
    _diskId2 = (byte)id2;
  }

  /// <summary>
  /// Adds a file to the flat directory. Commodore names are PETSCII and at most
  /// 16 characters; longer names are truncated. The default file type is PRG.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var trimmed = name.Length > 16 ? name[..16] : name;
    _d64.AddFile(trimmed, data);
  }

  /// <summary>Builds the G64 GCR nibble image holding all added files.</summary>
  public byte[] Build() {
    var d64 = _d64.Build(_diskName);
    return BuildG64(d64);
  }

  /// <summary>Writes the G64 image to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = this.Build();
    output.Write(image, 0, image.Length);
  }

  // ── G64 assembly ────────────────────────────────────────────────────────

  private byte[] BuildG64(byte[] d64) {
    // One G64 half-track entry per physical track (1..35) → indices 0,2,4,...
    // (the reader maps half-track index N to track N/2 + 1). Half-tracks
    // between real tracks stay empty (offset 0).
    var halfTrackCount = TotalTracks * 2 - 1; // 69 half-tracks cover tracks 1..35

    // Encode each real track first to learn the maximum block size.
    var encodedTracks = new byte[TotalTracks + 1][];
    var maxTrackSize = 0;
    for (var track = 1; track <= TotalTracks; track++) {
      var gcr = EncodeTrack(d64, track);
      encodedTracks[track] = gcr;
      if (gcr.Length > maxTrackSize) maxTrackSize = gcr.Length;
    }

    const int headerSize = 12;
    var offsetTableSize = halfTrackCount * 4;
    var speedTableSize = halfTrackCount * 4;
    var tableEnd = headerSize + offsetTableSize + speedTableSize;

    // Each populated half-track stores a u16 length prefix + its GCR payload.
    var blockStride = 2 + maxTrackSize;
    var populated = TotalTracks;
    var total = tableEnd + populated * blockStride;
    var buf = new byte[total];

    CbmNibbleReader.G64Signature.CopyTo(buf, 0);
    buf[8] = 0;                                   // version 0
    buf[9] = (byte)halfTrackCount;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), (ushort)maxTrackSize);

    var offsetTableStart = headerSize;
    var speedTableStart = offsetTableStart + offsetTableSize;
    var nextBlock = tableEnd;

    for (var half = 0; half < halfTrackCount; half++) {
      var isWholeTrack = half % 2 == 0;
      var track = half / 2 + 1;
      if (!isWholeTrack || track > TotalTracks) {
        // Empty half-track: offset 0, speed 0.
        continue;
      }

      var gcr = encodedTracks[track];
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offsetTableStart + half * 4), (uint)nextBlock);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(speedTableStart + half * 4), SpeedZoneFor(track));

      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(nextBlock), (ushort)gcr.Length);
      gcr.CopyTo(buf, nextBlock + 2);
      // Remaining bytes of the fixed-stride block stay as gap (0x00 padding is
      // acceptable; the length prefix bounds the meaningful GCR data).
      nextBlock += blockStride;
    }

    return buf;
  }

  private byte[] EncodeTrack(byte[] d64, int track) {
    using var ms = new MemoryStream();
    var sectorCount = SectorsPerTrack[track];
    for (var sector = 0; sector < sectorCount; sector++) {
      var sectorData = ReadSector(d64, track, sector);
      WriteSectorImage(ms, track, sector, sectorData);
    }
    return ms.ToArray();
  }

  private void WriteSectorImage(Stream output, int track, int sector, ReadOnlySpan<byte> data) {
    // Header block (8 bytes pre-GCR): 0x08, checksum, sector, track, id2, id1, 0x0F, 0x0F.
    Span<byte> header = stackalloc byte[8];
    header[0] = 0x08;
    header[2] = (byte)sector;
    header[3] = (byte)track;
    header[4] = _diskId2;
    header[5] = _diskId1;
    header[6] = 0x0F;
    header[7] = 0x0F;
    header[1] = (byte)(header[2] ^ header[3] ^ header[4] ^ header[5]); // header checksum

    // Data block (260 bytes pre-GCR): 0x07 marker, 256 data bytes, checksum, 2 × 0x00.
    Span<byte> dataBlock = stackalloc byte[260];
    dataBlock[0] = 0x07;
    data[..SectorSize].CopyTo(dataBlock[1..]);
    byte checksum = 0;
    for (var i = 0; i < SectorSize; i++) checksum ^= dataBlock[1 + i];
    dataBlock[257] = checksum;
    dataBlock[258] = 0x00;
    dataBlock[259] = 0x00;

    WriteSync(output);
    output.Write(CbmGcr.Encode(header));
    WriteGap(output, HeaderGap);
    WriteSync(output);
    output.Write(CbmGcr.Encode(dataBlock));
    WriteGap(output, TailGap);
  }

  private static void WriteSync(Stream output) {
    for (var i = 0; i < SyncLength; i++) output.WriteByte(0xFF);
  }

  private static void WriteGap(Stream output, int count) {
    for (var i = 0; i < count; i++) output.WriteByte(GapByte);
  }

  private static byte[] ReadSector(byte[] d64, int track, int sector) {
    var offset = SectorOffset(track, sector);
    var result = new byte[SectorSize];
    if (offset >= 0 && offset + SectorSize <= d64.Length)
      Array.Copy(d64, offset, result, 0, SectorSize);
    return result;
  }

  private static int SectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; t++) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  // ── GCR → D64 recovery ────────────────────────────────────────────────────

  /// <summary>
  /// Reconstructs a standard 174 848-byte D64 image from the GCR tracks of a
  /// nibble image previously parsed by <see cref="CbmNibbleReader"/>. Each
  /// track is rescanned for sync marks and its header/data blocks GCR-decoded
  /// back into the correct sector slots.
  /// </summary>
  public static byte[] DecodeToD64(CbmNibbleReader.NibbleImage image) {
    ArgumentNullException.ThrowIfNull(image);
    var d64 = new byte[174848];

    foreach (var trackEntry in image.Tracks) {
      if (trackEntry.Data.Length == 0) continue;
      // Half-track index N → physical track N/2 + 1; ignore half-step tracks.
      if (trackEntry.Index % 2 != 0) continue;
      var track = trackEntry.Index / 2 + 1;
      if (track < 1 || track > TotalTracks) continue;
      DecodeTrack(trackEntry.Data, track, d64);
    }
    return d64;
  }

  private static void DecodeTrack(byte[] gcr, int track, byte[] d64) {
    var pos = 0;
    while (pos < gcr.Length) {
      // Skip to the start of a sync run (0xFF bytes).
      while (pos < gcr.Length && gcr[pos] != 0xFF) pos++;
      while (pos < gcr.Length && gcr[pos] == 0xFF) pos++;
      if (pos >= gcr.Length) break;

      // After a sync we expect a GCR block. A header block is 8 raw bytes → 10
      // GCR bytes; a data block is 260 raw bytes → 325 GCR bytes. Peek the
      // decoded marker to tell them apart.
      if (pos + 10 > gcr.Length) break;
      byte[] firstBlock;
      try {
        firstBlock = CbmGcr.Decode(gcr.AsSpan(pos, 10));
      } catch (InvalidDataException) {
        pos++;
        continue;
      }

      if (firstBlock[0] != 0x08) {
        // Not a recognised header marker — advance and keep scanning.
        pos++;
        continue;
      }

      var sector = firstBlock[2];
      var blockTrack = firstBlock[3];
      pos += 10;

      // Advance past the header gap to the data-block sync.
      while (pos < gcr.Length && gcr[pos] != 0xFF) pos++;
      while (pos < gcr.Length && gcr[pos] == 0xFF) pos++;
      if (pos + 325 > gcr.Length) break;

      byte[] dataBlock;
      try {
        dataBlock = CbmGcr.Decode(gcr.AsSpan(pos, 325));
      } catch (InvalidDataException) {
        pos++;
        continue;
      }
      pos += 325;

      if (dataBlock[0] != 0x07) continue;
      if (blockTrack != track) continue;
      if (sector >= SectorsPerTrack[track]) continue;

      var offset = SectorOffset(track, sector);
      if (offset >= 0 && offset + SectorSize <= d64.Length)
        Array.Copy(dataBlock, 1, d64, offset, SectorSize);
    }
  }
}
