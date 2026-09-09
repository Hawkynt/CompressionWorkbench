#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfHeaderPreservationTests {
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] HeaderExtensionObject =
    [0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11, 0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionReserved1 =
    [0x11, 0xD2, 0xD3, 0xAB, 0xBA, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] ContentDescriptionObject =
    [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] VendorObject =
    [0x61, 0x5A, 0x24, 0xE7, 0xA1, 0x9C, 0x4D, 0x42, 0xB1, 0xAF, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00];
  private static readonly byte[] NestedVendorObject =
    [0x7A, 0xC4, 0x33, 0x10, 0x18, 0xBA, 0x48, 0x77, 0x92, 0x22, 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02];

  [Test]
  public void PayloadOnlyRemux_PreservesOpaqueHeaderExtensionAndVendorObjectsByteExactly() {
    var properties = BuildAudioStreamProperties(1);
    var originalPayload = Pattern(256, 0x11);
    var replacementPayload = Pattern(256, 0x91);
    var extension = BuildHeaderExtension(BuildObject(NestedVendorObject, [1, 3, 3, 7, 9]));
    var vendor = BuildObject(VendorObject, [0xDE, 0xAD, 0xBE, 0xEF]);
    var preserved = extension.Concat(vendor).ToArray();

    using var archive = new MemoryStream(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", originalPayload),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((256, 0u, false))),
      ArchiveInputInfo.InMemory("metadata/preserved-header.bin", preserved),
    ]), writable: true);

    Assert.That(Extract(archive.ToArray(), "metadata/preserved-header.bin"), Is.EqualTo(preserved));

    ((IArchiveModifiable)new AsfFormatDescriptor()).Add(archive,
      [ArchiveInputInfo.InMemory("streams/stream_01.bin", replacementPayload)]);

    var changed = archive.ToArray();
    Assert.Multiple(() => {
      Assert.That(Extract(changed, "streams/stream_01.bin"), Is.EqualTo(replacementPayload));
      Assert.That(Extract(changed, "metadata/preserved-header.bin"), Is.EqualTo(preserved));
    });
  }

  [Test]
  public void TopologyChange_DropsInheritedStreamDependentOpaqueHeaders_ButKeepsContentDescription() {
    var contentDescription = BuildContentDescription("multi-track title");
    var extension = BuildHeaderExtension(BuildObject(NestedVendorObject, [8, 6, 7, 5, 3, 0, 9]));
    var vendor = BuildObject(VendorObject, [1, 2, 3, 4]);
    var preserved = contentDescription.Concat(extension).Concat(vendor).ToArray();

    using var archive = new MemoryStream(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", BuildAudioStreamProperties(1)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(256, 0x10)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((256, 0u, false))),
      ArchiveInputInfo.InMemory("streams/stream_02.properties.bin", BuildVideoStreamProperties(2)),
      ArchiveInputInfo.InMemory("streams/stream_02.bin", Pattern(400, 0x70)),
      ArchiveInputInfo.InMemory("streams/stream_02.objects.csv", Manifest((400, 0u, true))),
      ArchiveInputInfo.InMemory("metadata/preserved-header.bin", preserved),
    ]), writable: true);

    ((IArchiveModifiable)new AsfFormatDescriptor()).Remove(archive, ["streams/stream_02.bin"]);
    var changedPreserved = Extract(archive.ToArray(), "metadata/preserved-header.bin");

    Assert.Multiple(() => {
      Assert.That(changedPreserved.AsSpan().IndexOf(contentDescription), Is.GreaterThanOrEqualTo(0),
        "stream-independent content description must survive topology changes");
      Assert.That(changedPreserved.AsSpan().IndexOf(vendor), Is.EqualTo(-1),
        "opaque vendor metadata may contain stale stream references and must be dropped");
      Assert.That(changedPreserved.AsSpan().IndexOf(extension), Is.EqualTo(-1),
        "the old Header Extension may refer to a removed stream and must be replaced");
      Assert.That(changedPreserved.AsSpan().IndexOf(HeaderExtensionObject), Is.GreaterThanOrEqualTo(0),
        "writer must synthesize the mandatory empty Header Extension after dropping the stale one");
    });
  }

  /// <summary>
  /// ASF sets the File Properties Seekable flag (0x02) for a fixed-packet file carrying audio, and
  /// for an audio/video file only once every separately declared video stream has a matching Simple
  /// Index Object. A video-only file never claims it. Before per-video Simple Index generation
  /// existed this fixture asserted that mixed A/V is never seekable, which the writer now earns.
  /// </summary>
  [Test]
  public void FileProperties_SeekableFlag_TracksAudioPresenceAndVideoIndexCoverage() {
    var audioOnly = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", BuildAudioStreamProperties(1)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(128, 0x20)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((128, 0u, false))),
    ]);
    var videoOnly = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", BuildVideoStreamProperties(1)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(128, 0x30)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((128, 0u, true))),
    ]);
    var mixed = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", BuildAudioStreamProperties(1)),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(128, 0x40)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((128, 0u, false))),
      ArchiveInputInfo.InMemory("streams/stream_02.properties.bin", BuildVideoStreamProperties(2)),
      ArchiveInputInfo.InMemory("streams/stream_02.bin", Pattern(128, 0x50)),
      ArchiveInputInfo.InMemory("streams/stream_02.objects.csv", Manifest((128, 0u, true))),
    ]);

    Assert.Multiple(() => {
      Assert.That(ReadFilePropertiesFlags(audioOnly), Is.EqualTo(0x00000002u));
      Assert.That(ReadFilePropertiesFlags(videoOnly), Is.Zero);
      Assert.That(ReadFilePropertiesFlags(mixed), Is.EqualTo(0x00000002u),
        "mixed A/V is seekable once every declared video stream has a matching Simple Index Object");
    });
  }

  private static uint ReadFilePropertiesFlags(byte[] asf)
    => BinaryPrimitives.ReadUInt32LittleEndian(asf.AsSpan(118, 4));

  private static byte[] BuildAudioStreamProperties(int streamNumber) {
    using var typeSpecific = new MemoryStream();
    WriteU16(typeSpecific, 0x0161);
    WriteU16(typeSpecific, 2);
    WriteU32(typeSpecific, 8000);
    WriteU32(typeSpecific, 16000);
    WriteU16(typeSpecific, 256);
    WriteU16(typeSpecific, 16);
    WriteU16(typeSpecific, 0);
    return BuildStreamProperties(streamNumber, AudioStreamType, typeSpecific.ToArray());
  }

  private static byte[] BuildVideoStreamProperties(int streamNumber) {
    using var typeSpecific = new MemoryStream();
    WriteU32(typeSpecific, 320);
    WriteU32(typeSpecific, 240);
    typeSpecific.WriteByte(0);
    WriteU16(typeSpecific, 40);
    WriteU32(typeSpecific, 40);
    WriteI32(typeSpecific, 320);
    WriteI32(typeSpecific, 240);
    WriteU16(typeSpecific, 1);
    WriteU16(typeSpecific, 24);
    typeSpecific.Write(Encoding.ASCII.GetBytes("WMV3"));
    WriteU32(typeSpecific, 0);
    WriteI32(typeSpecific, 0);
    WriteI32(typeSpecific, 0);
    WriteU32(typeSpecific, 0);
    WriteU32(typeSpecific, 0);
    return BuildStreamProperties(streamNumber, VideoStreamType, typeSpecific.ToArray());
  }

  private static byte[] BuildStreamProperties(int streamNumber, byte[] type, byte[] typeSpecific) {
    using var body = new MemoryStream();
    body.Write(type);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0);
    WriteU32(body, (uint)typeSpecific.Length);
    WriteU32(body, 0);
    WriteU16(body, (ushort)streamNumber);
    WriteU32(body, 0);
    body.Write(typeSpecific);
    return body.ToArray();
  }

  private static byte[] BuildHeaderExtension(byte[] nestedObjects) {
    using var body = new MemoryStream();
    body.Write(HeaderExtensionReserved1);
    WriteU16(body, 6);
    WriteU32(body, (uint)nestedObjects.Length);
    body.Write(nestedObjects);
    return BuildObject(HeaderExtensionObject, body.ToArray());
  }

  private static byte[] BuildContentDescription(string title) {
    var titleBytes = Encoding.Unicode.GetBytes(title + "\0");
    using var body = new MemoryStream();
    WriteU16(body, (ushort)titleBytes.Length);
    WriteU16(body, 0);
    WriteU16(body, 0);
    WriteU16(body, 0);
    WriteU16(body, 0);
    body.Write(titleBytes);
    return BuildObject(ContentDescriptionObject, body.ToArray());
  }

  private static byte[] BuildObject(byte[] guid, byte[] body) {
    using var output = new MemoryStream();
    output.Write(guid);
    WriteU64(output, (ulong)(24 + body.Length));
    output.Write(body);
    return output.ToArray();
  }

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs) {
    using var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["packet-size"] = "512",
      },
    };
    ((IArchiveCreatable)new AsfFormatDescriptor()).Create(output, inputs, options);
    return output.ToArray();
  }

  private static byte[] Extract(byte[] container, string path) {
    using var output = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(container), path, output, null);
    return output.ToArray();
  }

  private static byte[] Manifest(params (int Length, uint TimeMs, bool KeyFrame)[] objects) {
    var sb = new StringBuilder("offset,length,presentation_time_ms,keyframe\n");
    var offset = 0;
    foreach (var (length, timeMs, keyFrame) in objects) {
      sb.Append(offset).Append(',').Append(length).Append(',').Append(timeMs).Append(',')
        .AppendLine(keyFrame ? "true" : "false");
      offset += length;
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] Pattern(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 17));
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