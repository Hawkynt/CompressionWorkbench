#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Sup;

/// <summary>
/// Writes Blu-ray PGS (<c>.sup</c>) segment envelopes while preserving the raw
/// presentation/decode timestamps, segment type and body bytes.
/// </summary>
public static class SupWriter {
  private const int HeaderSize = 13;

  /// <summary>Writes the supplied PGS segments to <paramref name="output"/>.</summary>
  public static void Write(Stream output, IEnumerable<SupReader.Segment> segments) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(segments);
    if (!output.CanWrite) throw new ArgumentException("SUP output stream must be writable.", nameof(output));

    Span<byte> header = stackalloc byte[HeaderSize];
    foreach (var segment in segments) {
      ArgumentNullException.ThrowIfNull(segment);
      if (segment.Body.Length > ushort.MaxValue)
        throw new InvalidDataException(
          $"PGS: segment type 0x{segment.Type:X2} has {segment.Body.Length} body bytes; the SUP envelope allows at most {ushort.MaxValue}.");

      header[0] = (byte)'P';
      header[1] = (byte)'G';
      BinaryPrimitives.WriteUInt32BigEndian(header[2..6], segment.PtsRaw);
      BinaryPrimitives.WriteUInt32BigEndian(header[6..10], segment.DtsRaw);
      header[10] = segment.Type;
      BinaryPrimitives.WriteUInt16BigEndian(header[11..13], (ushort)segment.Body.Length);
      output.Write(header);
      output.Write(segment.Body);
    }
  }

  /// <summary>Serializes the supplied PGS segments into a new byte array.</summary>
  public static byte[] Write(IReadOnlyList<SupReader.Segment> segments) {
    ArgumentNullException.ThrowIfNull(segments);
    using var output = new MemoryStream();
    Write(output, segments);
    return output.ToArray();
  }
}
