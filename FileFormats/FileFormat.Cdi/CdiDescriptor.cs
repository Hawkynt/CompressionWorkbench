using System.Buffers.Binary;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI trailer helpers derived from the observable v2/v3/v3.5
/// container contract. The final eight bytes are the version plus the descriptor
/// locator; v3.5 stores the descriptor length while v2/v3 store an absolute
/// descriptor offset.
/// </summary>
internal static class CdiDescriptor {
  internal const uint Version2 = 0x80000004;
  internal const uint Version3 = 0x80000005;
  internal const uint Version35 = 0x80000006;

  private static ReadOnlySpan<byte> TrackStartMarker => [
    0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  internal readonly record struct Footer(
    uint Version,
    long DescriptorOffset,
    long DescriptorLength,
    bool IsLegacyFooterOnly
  );

  internal static bool TryReadFooter(Stream stream, out Footer footer) {
    ArgumentNullException.ThrowIfNull(stream);
    footer = default;
    if (!stream.CanRead || !stream.CanSeek || stream.Length < 8)
      return false;

    var originalPosition = stream.Position;
    try {
      stream.Position = stream.Length - 8;
      Span<byte> bytes = stackalloc byte[8];
      stream.ReadExactly(bytes);

      var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
      if (version is not (Version2 or Version3 or Version35))
        return false;

      var locator = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);

      // CompressionWorkbench used a version+zero trailer before it emitted a
      // session descriptor. Keep those images readable/editable, but never
      // confuse them with a genuine DiscJuggler descriptor.
      if (locator == 0) {
        footer = new(version, stream.Length - 8, 8, IsLegacyFooterOnly: true);
        return true;
      }

      var descriptorOffset = version == Version35
        ? stream.Length - locator
        : locator;
      if (descriptorOffset < 0 || descriptorOffset > stream.Length - 8)
        return false;

      var descriptorLength = stream.Length - descriptorOffset;
      if (descriptorLength < 8 || descriptorLength > uint.MaxValue)
        return false;

      footer = new(version, descriptorOffset, descriptorLength, IsLegacyFooterOnly: false);
      return true;
    } catch (EndOfStreamException) {
      return false;
    } finally {
      stream.Position = originalPosition;
    }
  }

  /// <summary>
  /// Builds the minimal single-session/single-track v3.5 descriptor accepted by
  /// the CDI v3.5 descriptor contract used by CDIrip. The payload itself is one
  /// cooked Mode-1 track of 2,048-byte sectors starting at file offset zero.
  /// </summary>
  internal static byte[] BuildSingleTrackV35(uint sectorCount) {
    using var descriptor = new MemoryStream(capacity: 192);

    WriteUInt16(descriptor, 1); // session count
    WriteUInt16(descriptor, 1); // track count in session 1

    WriteUInt32(descriptor, 0); // no extended preamble
    descriptor.Write(TrackStartMarker);
    descriptor.Write(TrackStartMarker);
    WriteZeros(descriptor, 4);

    descriptor.WriteByte(0); // embedded source filename length
    WriteZeros(descriptor, 11 + 4 + 4);
    WriteUInt32(descriptor, 0); // no DJ4 extension
    WriteZeros(descriptor, 2);

    WriteUInt32(descriptor, 0); // pregap sectors
    WriteUInt32(descriptor, sectorCount);
    WriteZeros(descriptor, 6);
    WriteUInt32(descriptor, 1); // Mode 1
    WriteZeros(descriptor, 12);
    WriteUInt32(descriptor, 0); // start LBA
    WriteUInt32(descriptor, sectorCount); // bytes occupied by the track, in sectors
    WriteZeros(descriptor, 16);
    WriteUInt32(descriptor, 0); // sector-size selector: cooked 2048

    WriteZeros(descriptor, 29);
    WriteZeros(descriptor, 5); // v3+ extension prefix
    WriteUInt32(descriptor, 0); // no optional extension block
    WriteZeros(descriptor, 13); // session trailer

    var descriptorLength = checked((uint)(descriptor.Length + 8));
    WriteUInt32(descriptor, Version35);
    WriteUInt32(descriptor, descriptorLength);
    return descriptor.ToArray();
  }

  private static void WriteUInt16(Stream stream, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteZeros(Stream stream, int count) {
    Span<byte> zeros = stackalloc byte[32];
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      stream.Write(zeros[..chunk]);
      count -= chunk;
    }
  }
}
