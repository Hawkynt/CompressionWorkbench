#pragma warning disable CS1591
namespace FileFormat.BitLocker;

/// <summary>
/// One piece of the decrypted view, in 512-byte units: either zeros (FVE metadata areas and the
/// relocated volume header's home) or ciphertext read from <see cref="IvOffset"/> on the device,
/// whose IVs count from <see cref="IvOffset"/> too.
/// </summary>
public readonly record struct BitLockerSegment(long Offset, long Length, long IvOffset, bool IsZero) {
  public long End => this.Offset + this.Length;
}

/// <summary>
/// The decrypted view exactly as cryptsetup's <c>BITLK_activate_by_volume_key</c> builds its
/// device-mapper table: zero segments over the three 64 KiB FVE metadata areas and over the
/// relocated volume header, the volume header itself mapped back to sector 0, and crypt segments
/// filling every gap with IVs equal to their own position.
/// </summary>
public static class BitLockerLayout {
  public const int Unit = 512;
  public const int MaxSegments = 10;

  /// <summary>Segments of <paramref name="volume"/> over a device of <paramref name="deviceLength"/> bytes, sorted by offset.</summary>
  /// <exception cref="InvalidDataException">The metadata places a segment outside the device, or the segments cannot cover it.</exception>
  public static IReadOnlyList<BitLockerSegment> Segments(BitLockerVolume volume, long deviceLength) {
    ArgumentNullException.ThrowIfNull(volume);
    var size = deviceLength / Unit;
    var headerLength = volume.VolumeHeaderSize / Unit;
    var segments = new List<BitLockerSegment>(MaxSegments);
    foreach (var offset in volume.MetadataOffsets)
      segments.Add(new(offset / Unit, BitLockerVolume.MetadataAreaSize / Unit, 0, IsZero: true));
    segments.Add(new(volume.VolumeHeaderOffset / Unit, headerLength, 0, IsZero: true));
    segments.Add(new(0, headerLength, volume.VolumeHeaderOffset / Unit, IsZero: false));

    foreach (var s in segments)
      if (s.Offset < 0 || s.Offset >= size || s.IvOffset >= size || s.Length < 0 || s.Length > size - s.Offset)
        throw new InvalidDataException("BitLocker: a metadata or header segment lies outside the device.");

    // Fill the gaps: from the end of the last segment added, the next gap starts at the nearest
    // segment end and runs to the nearest segment start at or after it.
    var last = headerLength;
    while (true) {
      long nextStart = size, nextEnd = size;
      foreach (var s in segments)
        if (s.End < nextStart && s.End >= last) nextStart = s.End;
      foreach (var s in segments)
        if (s.Offset < nextEnd && s.Offset >= nextStart) nextEnd = s.Offset;

      if (nextEnd - nextStart == 0) {
        if (nextStart >= size) throw new InvalidDataException("BitLocker: the segment layout does not cover the device.");
        last = nextEnd + 1;
        continue;
      }
      segments.Add(new(nextStart, nextEnd - nextStart, nextStart, IsZero: false));
      last = nextEnd;
      if (nextEnd == size) break;
      if (segments.Count == MaxSegments) throw new InvalidDataException("BitLocker: the volume needs more device-mapper segments than cryptsetup builds.");
    }

    segments.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    return segments;
  }
}
