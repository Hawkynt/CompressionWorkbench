using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Udf;

/// <summary>
/// The small ECMA-167 building blocks every UDF descriptor is assembled from:
/// the descriptor tag, the entity identifier, the character specification and
/// the timestamp.
/// </summary>
internal static class UdfDescriptors {

  /// <summary>
  /// Descriptor version stamped into every tag. ECMA-167 defines 2; OSTA UDF
  /// §2.2.1.2 requires 3 from revision 2.00 onwards, and the volumes this
  /// writer produces declare 2.01.
  /// </summary>
  public const ushort DescriptorVersion = 3;

  /// <summary>Revision this writer records, little-endian BCD as UDF stores it.</summary>
  public const ushort UdfRevision = 0x0201;

  /// <summary>
  /// Suffix of the <c>*OSTA UDF Compliant</c> domain entity identifier
  /// (OSTA UDF §2.1.5.3): UDF revision, domain flags, five reserved bytes.
  /// </summary>
  public static readonly byte[] DomainSuffix =
    [UdfRevision & 0xFF, UdfRevision >> 8, 0, 0, 0, 0, 0, 0];

  /// <summary>
  /// Suffix of a UDF-defined entity identifier (OSTA UDF §2.1.5.3): UDF
  /// revision, operating-system class, operating-system identifier, reserved.
  /// Class 0 is "undefined", which is what a portable writer can honestly claim.
  /// </summary>
  public static readonly byte[] UdfEntitySuffix =
    [UdfRevision & 0xFF, UdfRevision >> 8, 0, 0, 0, 0, 0, 0];

  /// <summary>
  /// Suffix of an implementation entity identifier (OSTA UDF §2.1.5.3):
  /// operating-system class, operating-system identifier, six free bytes.
  /// </summary>
  public static readonly byte[] ImplementationSuffix = [0, 0, 0, 0, 0, 0, 0, 0];

  /// <summary>
  /// Names the UDF implementation rather than the product, so an image says
  /// which filesystem code shaped it whichever tool drove that code.
  /// </summary>
  public const string ImplementationId = "*Linux UDFFS";

  /// <summary>
  /// Recording timestamp stamped into every descriptor. A fixed instant keeps
  /// two runs over the same input byte-identical, which the streaming writer's
  /// contract with the buffered one depends on.
  /// </summary>
  public static readonly DateTime RecordingTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Writes the fixed part of an ECMA-167 §7.2 descriptor tag. The checksum,
  /// CRC and CRC length are filled in by <see cref="FinalizeTag" /> once the
  /// body exists.
  /// </summary>
  public static void WriteTag(byte[] buffer, int offset, ushort tagIdentifier, uint tagLocation) {
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), tagIdentifier);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2), DescriptorVersion);
    buffer[offset + 4] = 0;                                                     // TagChecksum
    buffer[offset + 5] = 0;                                                     // Reserved
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 6), 1);     // TagSerialNumber
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 12), tagLocation);
  }

  /// <summary>
  /// Completes an ECMA-167 §7.2 descriptor tag: the CRC-16/CCITT (init 0,
  /// polynomial 0x1021, non-reflected) over <paramref name="bodyLength" />
  /// bytes following the tag, that length, and finally the byte-sum-mod-256
  /// checksum over the tag's other fifteen bytes.
  /// </summary>
  public static void FinalizeTag(byte[] buffer, int tagOffset, int bodyLength) {
    var bodyStart = tagOffset + 16;
    if (bodyStart + bodyLength > buffer.Length)
      bodyLength = buffer.Length - bodyStart;
    if (bodyLength < 0)
      bodyLength = 0;

    var crc = Crc16Ccitt.Compute(buffer.AsSpan(bodyStart, bodyLength));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(tagOffset + 8), crc);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(tagOffset + 10), (ushort)bodyLength);

    buffer[tagOffset + 4] = 0;
    byte sum = 0;
    for (var i = 0; i < 16; ++i) {
      if (i == 4) continue;
      sum = (byte)(sum + buffer[tagOffset + i]);
    }

    buffer[tagOffset + 4] = sum;
  }

  /// <summary>
  /// Writes an ECMA-167 §1/7.4 entity identifier: one flags byte, a 23-byte
  /// identifier and an 8-byte suffix whose shape depends on which kind of
  /// identifier this is.
  /// </summary>
  public static void WriteEntityId(byte[] buffer, int offset, string identifier, ReadOnlySpan<byte> suffix) {
    Array.Clear(buffer, offset, 32);
    var bytes = Encoding.ASCII.GetBytes(identifier);
    Array.Copy(bytes, 0, buffer, offset + 1, Math.Min(bytes.Length, 23));
    if (!suffix.IsEmpty)
      suffix[..Math.Min(suffix.Length, 8)].CopyTo(buffer.AsSpan(offset + 24, 8));
  }

  /// <summary>
  /// Writes an ECMA-167 §1/7.2.1 character specification naming OSTA
  /// Compressed Unicode, the only character set UDF volumes use.
  /// </summary>
  public static void WriteCharacterSet(byte[] buffer, int offset) {
    Array.Clear(buffer, offset, 64);
    buffer[offset] = 0;
    Encoding.ASCII.GetBytes("OSTA Compressed Unicode").CopyTo(buffer, offset + 1);
  }

  /// <summary>Writes an ECMA-167 §1/7.3 timestamp, recorded as UTC.</summary>
  public static void WriteTimestamp(byte[] buffer, int offset, DateTime utc) {
    // Type 1 is "local time", and with a zero offset that is UTC. Type 0 would
    // mean the interpretation is not specified at all.
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), 1 << 12);
    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + 2), (short)utc.Year);
    buffer[offset + 4] = (byte)utc.Month;
    buffer[offset + 5] = (byte)utc.Day;
    buffer[offset + 6] = (byte)utc.Hour;
    buffer[offset + 7] = (byte)utc.Minute;
    buffer[offset + 8] = (byte)utc.Second;
    buffer[offset + 9] = 0;
    buffer[offset + 10] = 0;
    buffer[offset + 11] = 0;
  }

  /// <summary>Writes an ECMA-167 §1/7.1 extent descriptor: byte length, then block.</summary>
  public static void WriteExtent(byte[] buffer, int offset, uint length, uint location) {
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), length);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), location);
  }

  /// <summary>
  /// Writes an ECMA-167 §4/14.14.2 long allocation descriptor addressing
  /// <paramref name="length" /> bytes at <paramref name="block" /> of the first
  /// partition, with <paramref name="uniqueId" /> in the UDF-defined part of
  /// its implementation-use area (OSTA UDF §2.3.4.3).
  /// </summary>
  public static void WriteLongAd(byte[] buffer, int offset, uint length, uint block, uint uniqueId = 0) {
    Array.Clear(buffer, offset, 16);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), length);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), block);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 8), 0); // partition reference
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 12), uniqueId);
  }
}
