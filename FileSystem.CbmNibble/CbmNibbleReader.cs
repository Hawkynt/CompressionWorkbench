#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CbmNibble;

/// <summary>
/// Reader for Commodore 1541/1571 nibble dumps — both the raw .nib format
/// (used by nibtools and ZoomFloppy) and the .g64 GCR track container
/// produced by emulators like VICE. Converting GCR back to a cleanly
/// sectored D64 is outside scope for this sweep; this reader detects the
/// format variant and surfaces each track as a raw byte buffer for
/// downstream tools to consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>NIB format</b>: a flat dump of 84 half-tracks × 0x2000 (8192) bytes
/// each — the raw 1541 read-head stream including sync marks and jitter.
/// There is no magic header; detection is extension-only, and the typical
/// file size is exactly <c>84 × 8192 = 688 128</c> bytes.
/// </para>
/// <para>
/// <b>G64 format</b>: per VICE spec, a 12-byte signature
/// <c>"GCR-1541\0\x00\x00\xA2\xA2"</c> (byte 8 = version, byte 9 = track
/// count, bytes 10-11 = max track-data size in bytes little-endian),
/// followed by an offset table of <c>track_count</c> u32 LE entries
/// (0 = empty track), an equal-length u32 LE speed-zone table, and the
/// raw track data blocks. Each track block starts with a u16 LE length
/// followed by the GCR bytes.
/// </para>
/// </remarks>
public sealed class CbmNibbleReader {

  public static readonly byte[] G64Signature = "GCR-1541"u8.ToArray();
  public const int NibTrackCount = 84;          // standard nibtools: 84 half-tracks
  public const int NibTrackSize = 0x2000;       // 8192 bytes per half-track
  public const int NibExpectedFileSize = NibTrackCount * NibTrackSize; // 688128

  public enum ImageKind { Nib, G64 }

  public sealed record Track(int Index, byte[] Data, uint SpeedZone);

  public sealed record NibbleImage(
    ImageKind Kind,
    byte Version,
    int TrackCount,
    int MaxTrackSize,
    List<Track> Tracks,
    long TotalFileSize);

  public static NibbleImage Read(ReadOnlySpan<byte> data, string? fileName = null) {
    if (data.Length >= G64Signature.Length && data[..G64Signature.Length].SequenceEqual(G64Signature))
      return ReadG64(data);

    // NIB has no magic — fall back on extension or raw size.
    if (fileName is not null && fileName.EndsWith(".nib", StringComparison.OrdinalIgnoreCase))
      return ReadNib(data);
    if (data.Length == NibExpectedFileSize)
      return ReadNib(data);

    throw new InvalidDataException(
      "CBM nibble: not a recognised G64 (missing 'GCR-1541' magic) or NIB dump " +
      $"(expected {NibExpectedFileSize}-byte file, got {data.Length}).");
  }

  private static NibbleImage ReadNib(ReadOnlySpan<byte> data) {
    // Most nib dumps are exactly 84 × 8192; truncated / short dumps are
    // tolerated by surfacing however many whole tracks fit.
    var trackCount = data.Length / NibTrackSize;
    var tracks = new List<Track>(trackCount);
    for (var i = 0; i < trackCount; i++) {
      var offset = i * NibTrackSize;
      tracks.Add(new Track(i, data.Slice(offset, NibTrackSize).ToArray(), SpeedZone: 0));
    }
    return new NibbleImage(
      Kind: ImageKind.Nib,
      Version: 0,
      TrackCount: trackCount,
      MaxTrackSize: NibTrackSize,
      Tracks: tracks,
      TotalFileSize: data.Length);
  }

  private static NibbleImage ReadG64(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("G64: file shorter than 12-byte header.");

    var version = data[8];
    int trackCount = data[9];
    var maxTrackSize = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);

    if (trackCount == 0 || trackCount > 84)
      throw new InvalidDataException($"G64: implausible track count {trackCount} (valid range 1-84).");

    var offsetTableStart = 12;
    var speedTableStart = offsetTableStart + trackCount * 4;
    if (speedTableStart + trackCount * 4 > data.Length)
      throw new InvalidDataException("G64: offset/speed tables extend past end of file.");

    var tracks = new List<Track>(trackCount);
    for (var i = 0; i < trackCount; i++) {
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(offsetTableStart + i * 4)..]);
      var speed = BinaryPrimitives.ReadUInt32LittleEndian(data[(speedTableStart + i * 4)..]);
      if (offset == 0) {
        // Empty track — no data recorded for this half-track.
        tracks.Add(new Track(i, [], speed));
        continue;
      }
      if (offset + 2 > (uint)data.Length) {
        tracks.Add(new Track(i, [], speed));
        continue;
      }
      var trackLen = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)offset..]);
      var payloadStart = (int)offset + 2;
      var copyLen = Math.Min(trackLen, Math.Max(0, data.Length - payloadStart));
      var buf = copyLen > 0 ? data.Slice(payloadStart, copyLen).ToArray() : [];
      tracks.Add(new Track(i, buf, speed));
    }

    return new NibbleImage(
      Kind: ImageKind.G64,
      Version: version,
      TrackCount: trackCount,
      MaxTrackSize: maxTrackSize,
      Tracks: tracks,
      TotalFileSize: data.Length);
  }

  public static byte[] BuildMetadata(NibbleImage img) {
    var sb = new StringBuilder();
    sb.AppendLine("[cbm_nibble]");
    sb.AppendLine($"kind = {img.Kind}");
    sb.AppendLine($"file_size = {img.TotalFileSize}");
    sb.AppendLine($"version = {img.Version}");
    sb.AppendLine($"track_count = {img.TrackCount}");
    sb.AppendLine($"max_track_size = {img.MaxTrackSize}");

    var nonEmpty = 0;
    long totalBytes = 0;
    foreach (var t in img.Tracks) {
      if (t.Data.Length > 0) nonEmpty++;
      totalBytes += t.Data.Length;
    }
    sb.AppendLine($"non_empty_tracks = {nonEmpty}");
    sb.AppendLine($"total_track_bytes = {totalBytes}");

    sb.AppendLine();
    sb.AppendLine("[tracks]");
    foreach (var t in img.Tracks) {
      // Half-tracks 0,2,4... are whole tracks 1,2,3... — expose both numbers
      // so operators can cross-reference with nibtools / c64 docs.
      var halfTrack = t.Index;
      var track = (halfTrack / 2) + 1;
      var half = halfTrack % 2 == 0 ? "" : ".5";
      sb.AppendLine($"track_{halfTrack:D2} = track={track}{half} size={t.Data.Length} speed_zone={t.SpeedZone}");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
