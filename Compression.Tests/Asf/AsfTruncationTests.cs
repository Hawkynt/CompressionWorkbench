#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfTruncationTests {

  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  [Test]
  public void Add_TruncatedDataObjectWithCompletePrefix_IsTransactionalFailure() {
    var payload = Pattern(1600, 0x31);
    var complete = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", BuildVideoStreamProperties(1, 320, 240, "WMV3")),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", payload),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((900, 1000u, true), (700, 1040u, false))),
    ], packetSize: 512);

    // 900 and 700 payload bytes over 512-byte packets (480 usable each) is four packets: the
    // first media object fills two, the second fills a third and 220 bytes of a fourth. Cutting
    // inside that fourth packet leaves object one complete and object two unrecoverable.
    // Chopping the last byte of the FILE would only shorten the trailing Simple Index Object,
    // which the writer began emitting after this fixture was written, and leave both media
    // objects intact.
    var truncated = TruncateInsideDataObject(complete, packetSize: 512, wholePacketsKept: 3, trailingBytesKept: 10);
    var salvaged = Extract(truncated, "streams/stream_01.bin");
    Assert.That(salvaged.Length, Is.EqualTo(900),
      "fixture must retain exactly the first complete media object while truncating the second");

    using var archive = new MemoryStream(truncated, writable: true);
    var before = archive.ToArray();
    var descriptor = (IArchiveModifiable)new AsfFormatDescriptor();

    Assert.That(() => descriptor.Add(archive, [
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(salvaged.Length, 0xA0)),
    ]), Throws.TypeOf<InvalidDataException>().With.Message.Contains("truncated"));

    Assert.That(archive.ToArray(), Is.EqualTo(before),
      "truncated input must be rejected before any transactional remux commit can replace it");
  }

  /// <summary>
  /// Cuts <paramref name="container"/> inside its Data Object, keeping
  /// <paramref name="wholePacketsKept"/> intact packets plus <paramref name="trailingBytesKept"/>
  /// bytes of the next one, and dropping every top-level object that followed the data.
  /// </summary>
  private static byte[] TruncateInsideDataObject(byte[] container, int packetSize, int wholePacketsKept, int trailingBytesKept) {
    var dataObjectStart = IndexOfGuid(container, DataObjectGuid);
    Assert.That(dataObjectStart, Is.GreaterThanOrEqualTo(0), "fixture container has no Data Object");
    // GUID(16) + size(8) + file id(16) + total data packets(8) + reserved(2).
    var packetsStart = dataObjectStart + 50;
    var cut = packetsStart + wholePacketsKept * packetSize + trailingBytesKept;
    Assert.That(cut, Is.LessThan(container.Length), "fixture cut point must lie inside the container");
    return container[..cut];
  }

  private static int IndexOfGuid(byte[] container, byte[] guid) {
    for (var i = 0; i + guid.Length <= container.Length; ++i)
      if (container.AsSpan(i, guid.Length).SequenceEqual(guid))
        return i;
    return -1;
  }

  private static readonly byte[] DataObjectGuid =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, int packetSize) {
    using var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["packet-size"] = packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
      },
    };
    ((IArchiveCreatable)new AsfFormatDescriptor()).Create(output, inputs, options);
    return output.ToArray();
  }

  private static byte[] Extract(byte[] container, string name) {
    using var output = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(container), name, output, null);
    return output.ToArray();
  }

  private static byte[] BuildVideoStreamProperties(int streamNumber, int width, int height, string fourCc) {
    if (fourCc.Length != 4)
      throw new ArgumentException("FourCC must be exactly four characters.", nameof(fourCc));

    using var typeSpecific = new MemoryStream();
    WriteU32(typeSpecific, (uint)width);
    WriteU32(typeSpecific, (uint)height);
    typeSpecific.WriteByte(0);
    WriteU16(typeSpecific, 40);
    WriteU32(typeSpecific, 40);
    WriteI32(typeSpecific, width);
    WriteI32(typeSpecific, height);
    WriteU16(typeSpecific, 1);
    WriteU16(typeSpecific, 24);
    typeSpecific.Write(Encoding.ASCII.GetBytes(fourCc));
    WriteU32(typeSpecific, 0);
    WriteI32(typeSpecific, 0);
    WriteI32(typeSpecific, 0);
    WriteU32(typeSpecific, 0);
    WriteU32(typeSpecific, 0);
    var typeBytes = typeSpecific.ToArray();

    using var body = new MemoryStream();
    body.Write(VideoStreamType);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0);
    WriteU32(body, (uint)typeBytes.Length);
    WriteU32(body, 0);
    WriteU16(body, (ushort)streamNumber);
    WriteU32(body, 0);
    body.Write(typeBytes);
    return body.ToArray();
  }

  private static byte[] Manifest(params (int Length, uint TimeMs, bool KeyFrame)[] objects) {
    var sb = new StringBuilder("offset,length,presentation_time_ms,keyframe\n");
    var offset = 0;
    foreach (var (length, time, keyFrame) in objects) {
      sb.Append(offset).Append(',').Append(length).Append(',').Append(time).Append(',')
        .AppendLine(keyFrame ? "true" : "false");
      offset += length;
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] Pattern(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 37 + (i >> 3)));
    return result;
  }

  private static void WriteU16(Stream stream, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteU32(Stream stream, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteI32(Stream stream, int value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteU64(Stream stream, ulong value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
    stream.Write(buffer);
  }
}
