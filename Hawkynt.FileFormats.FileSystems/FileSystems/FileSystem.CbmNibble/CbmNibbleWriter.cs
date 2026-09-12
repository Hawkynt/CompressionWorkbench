#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.D64;

namespace FileSystem.CbmNibble;

/// <summary>
/// Writer for Commodore nibble containers. It supports two distinct layers:
/// ordinary Commodore files are first placed into a D64 and GCR-encoded, while
/// pseudo-archive callers can directly build G64/NIB containers from opaque
/// <c>track_XX.bin</c> payloads without touching the filesystem inside them.
/// </summary>
public sealed class CbmNibbleWriter {

  private const int SectorSize = 256;
  private const int TotalTracks = 35;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  private static byte SpeedZoneFor(int track) => track switch {
    >= 1 and <= 17 => 3,
    >= 18 and <= 24 => 2,
    >= 25 and <= 30 => 1,
    _ => 0,
  };

  private const int SyncLength = 5;
  private const byte GapByte = 0x55;
  private const int HeaderGap = 9;
  private const int TailGap = 8;

  private readonly D64Writer _d64 = new();
  private byte _diskId1 = (byte)'0';
  private byte _diskId2 = (byte)'0';
  private string _diskName = "DISK";

  public void SetDisk(string name, char id1 = '0', char id2 = '0') {
    _diskName = name ?? "DISK";
    _diskId1 = (byte)id1;
    _diskId2 = (byte)id2;
  }

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var trimmed = name.Length > 16 ? name[..16] : name;
    _d64.AddFile(trimmed, data);
  }

  /// <summary>Builds a VICE G64 image from the ordinary files added above.</summary>
  public byte[] Build() {
    var d64 = _d64.Build(_diskName);
    return BuildG64(d64);
  }

  /// <summary>
  /// Builds a raw 84×8192-byte NIB image from the ordinary files added above.
  /// Whole-track G64 payloads are copied to their corresponding even half-track
  /// slots and padded with the conventional 0x55 gap byte; unused half-tracks
  /// remain the workbench's all-zero empty-slot representation.
  /// </summary>
  public byte[] BuildNib() {
    var g64 = CbmNibbleReader.Read(this.Build(), "image.g64");
    var result = new byte[CbmNibbleReader.NibExpectedFileSize];
    foreach (var track in g64.Tracks) {
      if (track.Data.Length == 0 || track.Index >= CbmNibbleReader.NibTrackCount) continue;
      if (track.Data.Length > CbmNibbleReader.NibTrackSize)
        throw new InvalidOperationException(
          $"Encoded GCR track {track.Index} is {track.Data.Length} bytes and does not fit a NIB slot.");
      var slot = result.AsSpan(track.Index * CbmNibbleReader.NibTrackSize, CbmNibbleReader.NibTrackSize);
      slot.Fill(GapByte);
      track.Data.CopyTo(slot);
    }
    return result;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = this.Build();
    output.Write(image, 0, image.Length);
  }

  /// <summary>
  /// Builds a compact G64 directly from opaque half-track payloads. This is the
  /// canonical mutation/re-layout path for the pseudo-archive surface: track
  /// bytes are preserved exactly and placed back-to-back with no obsolete
  /// fixed-stride padding. Constant speed zones 0..3 are preserved; pointer-based
  /// variable-speed maps are intentionally refused until their auxiliary speed
  /// blocks are modeled.
  /// </summary>
  public static byte[] BuildG64FromTracks(
      IReadOnlyList<CbmNibbleReader.Track> tracks,
      byte version = 0,
      int? trackCount = null) {
    ArgumentNullException.ThrowIfNull(tracks);
    var byIndex = tracks.GroupBy(t => t.Index).ToDictionary(g => g.Key, g => g.Last());
    foreach (var track in byIndex.Values) {
      if (track.Index is < 0 or >= 84)
        throw new ArgumentOutOfRangeException(nameof(tracks), $"G64 half-track index {track.Index} is outside 0..83.");
      if (track.Data.Length > ushort.MaxValue)
        throw new NotSupportedException($"G64 track {track.Index} exceeds the 16-bit track-length field.");
      if (track.SpeedZone > 3)
        throw new NotSupportedException(
          $"G64 track {track.Index} uses variable-speed map pointer 0x{track.SpeedZone:X8}; direct mutation requires constant speed zones 0..3.");
    }

    var highest = byIndex.Count == 0 ? 0 : byIndex.Keys.Max() + 1;
    var count = trackCount ?? Math.Max(1, highest);
    if (count is < 1 or > 84 || count < highest)
      throw new ArgumentOutOfRangeException(nameof(trackCount), "G64 track count must be 1..84 and cover every supplied track index.");

    var maxTrackSize = byIndex.Values.Where(t => t.Data.Length > 0)
      .Select(t => t.Data.Length).DefaultIfEmpty(0).Max();
    const int headerSize = 12;
    var tablesEnd = headerSize + count * 8;
    var payloadBytes = byIndex.Values.Where(t => t.Index < count && t.Data.Length > 0)
      .Sum(t => checked(2 + t.Data.Length));
    var buffer = new byte[checked(tablesEnd + payloadBytes)];

    CbmNibbleReader.G64Signature.CopyTo(buffer, 0);
    buffer[8] = version;
    buffer[9] = (byte)count;
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)maxTrackSize);

    var offsetTable = headerSize;
    var speedTable = headerSize + count * 4;
    var cursor = tablesEnd;
    for (var i = 0; i < count; ++i) {
      if (!byIndex.TryGetValue(i, out var track)) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(speedTable + i * 4), track.SpeedZone);
      if (track.Data.Length == 0) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetTable + i * 4), (uint)cursor);
      BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(cursor), (ushort)track.Data.Length);
      track.Data.CopyTo(buffer, cursor + 2);
      cursor += 2 + track.Data.Length;
    }
    return buffer;
  }

  /// <summary>
  /// Builds a fixed-size NIB directly from opaque track slots. A non-empty
  /// replacement must be exactly 8192 bytes so extracting it again yields the
  /// same pseudo-entry bytes. Missing/empty tracks are encoded as all-zero slots.
  /// </summary>
  public static byte[] BuildNibFromTracks(IReadOnlyList<CbmNibbleReader.Track> tracks) {
    ArgumentNullException.ThrowIfNull(tracks);
    var result = new byte[CbmNibbleReader.NibExpectedFileSize];
    foreach (var track in tracks) {
      if (track.Index is < 0 or >= CbmNibbleReader.NibTrackCount)
        throw new ArgumentOutOfRangeException(nameof(tracks), $"NIB half-track index {track.Index} is outside 0..83.");
      if (track.Data.Length == 0) continue;
      if (track.Data.Length != CbmNibbleReader.NibTrackSize)
        throw new NotSupportedException(
          $"NIB track_{track.Index:D2}.bin must be exactly {CbmNibbleReader.NibTrackSize} bytes; got {track.Data.Length}.");
      track.Data.CopyTo(result, track.Index * CbmNibbleReader.NibTrackSize);
    }
    return result;
  }

  private byte[] BuildG64(byte[] d64) {
    var halfTrackCount = TotalTracks * 2 - 1;
    var encodedTracks = new List<CbmNibbleReader.Track>(TotalTracks);
    for (var track = 1; track <= TotalTracks; track++) {
      var half = (track - 1) * 2;
      encodedTracks.Add(new CbmNibbleReader.Track(
        half, EncodeTrack(d64, track), SpeedZoneFor(track)));
    }
    return BuildG64FromTracks(encodedTracks, version: 0, trackCount: halfTrackCount);
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
    Span<byte> header = stackalloc byte[8];
    header[0] = 0x08;
    header[2] = (byte)sector;
    header[3] = (byte)track;
    header[4] = _diskId2;
    header[5] = _diskId1;
    header[6] = 0x0F;
    header[7] = 0x0F;
    header[1] = (byte)(header[2] ^ header[3] ^ header[4] ^ header[5]);

    Span<byte> dataBlock = stackalloc byte[260];
    dataBlock[0] = 0x07;
    data[..SectorSize].CopyTo(dataBlock[1..]);
    byte checksum = 0;
    for (var i = 0; i < SectorSize; i++) checksum ^= dataBlock[1 + i];
    dataBlock[257] = checksum;

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

  /// <summary>
  /// Reconstructs a standard 174848-byte D64 image from the GCR tracks of a
  /// nibble image. Whole-track entries are rescanned for sync/header/data blocks.
  /// </summary>
  public static byte[] DecodeToD64(CbmNibbleReader.NibbleImage image) {
    ArgumentNullException.ThrowIfNull(image);
    var d64 = new byte[174848];

    foreach (var trackEntry in image.Tracks) {
      if (trackEntry.Data.Length == 0) continue;
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
      while (pos < gcr.Length && gcr[pos] != 0xFF) pos++;
      while (pos < gcr.Length && gcr[pos] == 0xFF) pos++;
      if (pos >= gcr.Length) break;

      if (pos + 10 > gcr.Length) break;
      byte[] firstBlock;
      try {
        firstBlock = CbmGcr.Decode(gcr.AsSpan(pos, 10));
      } catch (InvalidDataException) {
        pos++;
        continue;
      }

      if (firstBlock[0] != 0x08) {
        pos++;
        continue;
      }

      var sector = firstBlock[2];
      var blockTrack = firstBlock[3];
      pos += 10;

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
