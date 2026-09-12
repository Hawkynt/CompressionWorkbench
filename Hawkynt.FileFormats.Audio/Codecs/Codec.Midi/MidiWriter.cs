#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Midi;

/// <summary>Standard MIDI File emitter for already-encoded <c>MTrk</c> payloads.</summary>
public static class MidiWriter {

  /// <summary>
  /// Builds an SMF from raw <c>MTrk</c> payloads. Format 0 requires exactly one track;
  /// formats 1 and 2 may contain multiple tracks. Event bytes are preserved verbatim.
  /// </summary>
  public static byte[] BuildFile(IReadOnlyList<byte[]> trackBodies, int division, int format = 1) {
    ArgumentNullException.ThrowIfNull(trackBodies);
    if (format is < 0 or > 2)
      throw new ArgumentOutOfRangeException(nameof(format), "SMF format must be 0, 1, or 2.");
    if (trackBodies.Count == 0)
      throw new ArgumentException("An SMF must contain at least one track.", nameof(trackBodies));
    if (format == 0 && trackBodies.Count != 1)
      throw new ArgumentException("SMF format 0 requires exactly one track.", nameof(trackBodies));
    if (trackBodies.Count > ushort.MaxValue)
      throw new ArgumentException("SMF track count exceeds the 16-bit header field.", nameof(trackBodies));
    if (division is < short.MinValue or > short.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(division));
    if (trackBodies.Any(static track => track is null))
      throw new ArgumentException("Track payloads cannot be null.", nameof(trackBodies));

    using var output = new MemoryStream();
    output.Write("MThd"u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(size, 6);
    output.Write(size);

    Span<byte> header = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)format);
    BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)trackBodies.Count);
    BinaryPrimitives.WriteUInt16BigEndian(header[4..], unchecked((ushort)(short)division));
    output.Write(header);

    foreach (var track in trackBodies) {
      output.Write("MTrk"u8);
      BinaryPrimitives.WriteInt32BigEndian(size, track.Length);
      output.Write(size);
      output.Write(track);
    }

    return output.ToArray();
  }
}
