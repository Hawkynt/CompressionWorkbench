#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CbmNibble;

/// <summary>
/// Reader for Commodore 1541/1571 nibble dumps — both raw .nib fixed-slot
/// images and VICE .g64 track containers. The pseudo-archive surface is one
/// opaque GCR payload per half-track; callers that need filesystem semantics
/// can explicitly decode those tracks to a D64 through <see cref="CbmNibbleWriter.DecodeToD64"/>.
/// </summary>
public sealed class CbmNibbleReader {

  /// <summary>
  /// Provides the g 64 signature value.
  /// </summary>
  public static readonly byte[] G64Signature = "GCR-1541"u8.ToArray();
  /// <summary>
  /// Defines the nib track count constant value.
  /// </summary>
  public const int NibTrackCount = 84;          // standard nibtools: 84 half-tracks
  /// <summary>
  /// Defines the nib track size constant value.
  /// </summary>
  public const int NibTrackSize = 0x2000;       // 8192 bytes per half-track
  /// <summary>
  /// Defines the nib expected file size constant value.
  /// </summary>
  public const int NibExpectedFileSize = NibTrackCount * NibTrackSize; // 688128

  /// <summary>
  /// The nibble-level Commodore disk image layout the stream holds.
  /// </summary>
  public enum ImageKind { Nib, G64 }

  /// <summary>
  /// Represents a track.
  /// </summary>
  /// <param name="PhysicalOffset">Start of the physical slot/block in the container.</param>
  /// <param name="PhysicalLength">Physical bytes occupied by this track block/slot.</param>
  public sealed record Track(
    int Index,
    byte[] Data,
    uint SpeedZone,
    long PhysicalOffset = -1,
    long PhysicalLength = 0);

  /// <summary>
  /// Represents a nibble image.
  /// </summary>
  public sealed record NibbleImage(
    ImageKind Kind,
    byte Version,
    int TrackCount,
    int MaxTrackSize,
    List<Track> Tracks,
    long TotalFileSize);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public static NibbleImage Read(ReadOnlySpan<byte> data, string? fileName = null) {
    if (data.Length >= G64Signature.Length && data[..G64Signature.Length].SequenceEqual(G64Signature))
      return ReadG64(data);

    if (fileName is not null && fileName.EndsWith(".nib", StringComparison.OrdinalIgnoreCase))
      return ReadNib(data);
    if (data.Length == NibExpectedFileSize)
      return ReadNib(data);

    throw new InvalidDataException(
      "CBM nibble: not a recognised G64 (missing 'GCR-1541' magic) or NIB dump " +
      $"(expected {NibExpectedFileSize}-byte file, got {data.Length}).");
  }

  private static NibbleImage ReadNib(ReadOnlySpan<byte> data) {
    var trackCount = Math.Min(NibTrackCount, data.Length / NibTrackSize);
    var tracks = new List<Track>(trackCount);
    for (var i = 0; i < trackCount; i++) {
      var offset = i * NibTrackSize;
      var slot = data.Slice(offset, NibTrackSize);
      // NIB has no absent-track marker. CompressionWorkbench reserves an
      // all-zero fixed slot as its canonical empty representation: a useful
      // GCR track cannot be all zero, and the physical slot is still retained.
      var empty = true;
      foreach (var b in slot)
        if (b != 0) { empty = false; break; }
      tracks.Add(new Track(i, empty ? [] : slot.ToArray(), SpeedZone: 0,
        PhysicalOffset: offset, PhysicalLength: NibTrackSize));
    }
    return new NibbleImage(
      ImageKind.Nib, Version: 0, trackCount, NibTrackSize, tracks, data.Length);
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
        tracks.Add(new Track(i, [], speed));
        continue;
      }
      if (offset + 2 > (uint)data.Length) {
        tracks.Add(new Track(i, [], speed, offset, 0));
        continue;
      }
      var trackLen = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)offset..]);
      var payloadStart = (int)offset + 2;
      var copyLen = Math.Min(trackLen, Math.Max(0, data.Length - payloadStart));
      var buf = copyLen > 0 ? data.Slice(payloadStart, copyLen).ToArray() : [];
      tracks.Add(new Track(i, buf, speed, offset, 2L + copyLen));
    }

    return new NibbleImage(
      ImageKind.G64, version, trackCount, maxTrackSize, tracks, data.Length);
  }

  /// <summary>
  /// Performs the build metadata operation.
  /// </summary>
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
      var halfTrack = t.Index;
      var track = (halfTrack / 2) + 1;
      var half = halfTrack % 2 == 0 ? "" : ".5";
      sb.AppendLine($"track_{halfTrack:D2} = track={track}{half} size={t.Data.Length} speed_zone={t.SpeedZone}");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
