#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

/// <summary>
/// Pins the ASF/WMV mux/remux contract independently of a WMV decoder. The payload bytes
/// are deliberately opaque: container writing must preserve them exactly while retaining
/// Stream Properties, media-object boundaries, timestamps and key-frame flags.
/// </summary>
[TestFixture]
public sealed class AsfContainerMuxTests {

  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

  [Test]
  public void Descriptor_AdvertisesMuxAndRemux() {
    var descriptor = new AsfFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(((IArchivePurgeable)descriptor).CanPurgeToEmpty, Is.False);
    });
  }

  [Test]
  public void Create_Wmv3_CanonicalArtifactsRoundTripAndEmitRequiredEnvelopeFields() {
    var payload = Pattern(1600, 0x31);
    var properties = BuildVideoStreamProperties(streamNumber: 1, width: 320, height: 240, fourCc: "WMV3");
    var manifest = Manifest((900, 1000u, true), (700, 1040u, false));

    var blob = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", payload),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", manifest),
    ], packetSize: 512);

    var descriptor = new AsfFormatDescriptor();
    var entries = descriptor.List(new MemoryStream(blob), null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "streams/stream_01.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "streams/stream_01.properties.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "streams/stream_01.objects.csv"), Is.True);
    });

    Assert.That(Extract(blob, "streams/stream_01.bin"), Is.EqualTo(payload));
    Assert.That(Extract(blob, "streams/stream_01.properties.bin"), Is.EqualTo(properties));
    var remuxManifest = Encoding.UTF8.GetString(Extract(blob, "streams/stream_01.objects.csv"));
    Assert.Multiple(() => {
      Assert.That(remuxManifest, Does.Contain("0,900,1000,true"));
      Assert.That(remuxManifest, Does.Contain("900,700,1040,false"));
    });

    var info = Encoding.UTF8.GetString(Extract(blob, "streams/stream_01.info.txt"));
    Assert.Multiple(() => {
      Assert.That(info, Does.Contain("type = video"));
      Assert.That(info, Does.Contain("codec = wmv3"));
      Assert.That(info, Does.Contain("codec_fourcc = WMV3"));
      Assert.That(info, Does.Contain("width = 320"));
      Assert.That(info, Does.Contain("height = 240"));
    });

    Assert.Multiple(() => {
      Assert.That(blob[28], Is.EqualTo(0x01), "Header Object Reserved1");
      Assert.That(blob[29], Is.EqualTo(0x02), "Header Object Reserved2");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(122, 4)), Is.EqualTo(512u), "minimum packet size");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(126, 4)), Is.EqualTo(512u), "maximum packet size");
    });

    var dataOffset = IndexOf(blob, DataObject);
    Assert.That(dataOffset, Is.GreaterThan(0), "Data Object must follow the Header Object");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(dataOffset + 48, 2)), Is.EqualTo(0x0101),
      "Data Object reserved word");
  }

  [Test]
  public void Add_SameLengthPayload_PreservesObjectTimingAndProperties() {
    var original = Pattern(1600, 0x20);
    var replacement = Pattern(1600, 0x90);
    var properties = BuildVideoStreamProperties(1, 640, 360, "WMV3");
    var manifest = Manifest((900, 250u, true), (700, 290u, false));
    using var archive = new MemoryStream(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", original),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", manifest),
    ], 512), writable: true);

    var beforeManifest = Extract(archive.ToArray(), "streams/stream_01.objects.csv");
    ((IArchiveModifiable)new AsfFormatDescriptor()).Add(archive,
      [ArchiveInputInfo.InMemory("streams/stream_01.bin", replacement)]);
    var changed = archive.ToArray();

    Assert.Multiple(() => {
      Assert.That(Extract(changed, "streams/stream_01.bin"), Is.EqualTo(replacement));
      Assert.That(Extract(changed, "streams/stream_01.properties.bin"), Is.EqualTo(properties));
      Assert.That(Extract(changed, "streams/stream_01.objects.csv"), Is.EqualTo(beforeManifest));
    });
  }

  [Test]
  public void Add_DifferentLengthMultiObjectPayloadWithoutManifest_IsTransactionalFailure() {
    var payload = Pattern(1600, 0x42);
    var properties = BuildVideoStreamProperties(1, 320, 240, "WMV3");
    using var archive = new MemoryStream(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", payload),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((900, 0u, true), (700, 40u, false))),
    ], 512), writable: true);
    var before = archive.ToArray();

    Assert.That(() => ((IArchiveModifiable)new AsfFormatDescriptor()).Add(archive,
      [ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(1700, 0x77))]),
      Throws.TypeOf<NotSupportedException>());

    Assert.That(archive.ToArray(), Is.EqualTo(before), "failed remux must leave the caller's archive untouched");
  }

  [Test]
  public void Add_DifferentLengthPayloadWithNewManifest_RoundTripsNewLayout() {
    var properties = BuildVideoStreamProperties(1, 320, 240, "WMV3");
    // The replacement payload is longer than the original, so the archive stream has to grow.
    // A MemoryStream wrapping a fixed buffer cannot, so this fixture seeds a growable one.
    using var archive = new MemoryStream();
    archive.Write(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(1600, 0x22)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((900, 0u, true), (700, 40u, false))),
    ], 512));
    archive.Position = 0;

    var replacement = Pattern(1700, 0xA0);
    var replacementManifest = Manifest((1000, 500u, false), (700, 540u, true));
    ((IArchiveModifiable)new AsfFormatDescriptor()).Add(archive, [
      ArchiveInputInfo.InMemory("streams/stream_01.bin", replacement),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", replacementManifest),
    ]);

    var changed = archive.ToArray();
    Assert.That(Extract(changed, "streams/stream_01.bin"), Is.EqualTo(replacement));
    var manifestText = Encoding.UTF8.GetString(Extract(changed, "streams/stream_01.objects.csv"));
    Assert.Multiple(() => {
      Assert.That(manifestText, Does.Contain("0,1000,500,false"));
      Assert.That(manifestText, Does.Contain("1000,700,540,true"));
    });
  }

  /// <summary>
  /// A destination that cannot be resized to hold the remuxed file must fail before a single byte
  /// of it is overwritten. Truncating first and only then discovering the limit would hand the
  /// caller a half-written archive, which is the outcome the staging file exists to prevent.
  /// </summary>
  [Test]
  public void Add_DestinationThatCannotGrow_FailsWithoutTouchingTheArchive() {
    var properties = BuildVideoStreamProperties(1, 320, 240, "WMV3");
    var original = Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(1600, 0x22)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((900, 0u, true), (700, 40u, false))),
    ], 512);

    // Fixed-capacity: writable, but MemoryStream cannot expand a caller-supplied buffer.
    using var archive = new MemoryStream(original, writable: true);
    var before = archive.ToArray();

    Assert.That(() => ((IArchiveModifiable)new AsfFormatDescriptor()).Add(archive, [
      ArchiveInputInfo.InMemory("streams/stream_01.bin", Pattern(1700, 0xA0)),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((1000, 500u, false), (700, 540u, true))),
    ]), Throws.TypeOf<NotSupportedException>().With.Message.Contains("resized"));

    Assert.That(archive.ToArray(), Is.EqualTo(before),
      "a destination that cannot take the new length must be left byte-for-byte unchanged");
  }

  [Test]
  public void Remove_OneStream_RemuxesSurvivorByteExactly() {
    var props1 = BuildVideoStreamProperties(1, 320, 240, "WMV3");
    var props2 = BuildVideoStreamProperties(2, 160, 120, "WMV2");
    var payload1 = Pattern(700, 0x11);
    var payload2 = Pattern(900, 0xD0);
    using var archive = new MemoryStream(Create([
      ArchiveInputInfo.InMemory("streams/stream_01.properties.bin", props1),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", payload1),
      ArchiveInputInfo.InMemory("streams/stream_01.objects.csv", Manifest((700, 0u, true))),
      ArchiveInputInfo.InMemory("streams/stream_02.properties.bin", props2),
      ArchiveInputInfo.InMemory("streams/stream_02.bin", payload2),
      ArchiveInputInfo.InMemory("streams/stream_02.objects.csv", Manifest((900, 20u, true))),
    ], 512), writable: true);

    ((IArchiveModifiable)new AsfFormatDescriptor()).Remove(archive, ["streams/stream_01.bin"]);
    var changed = archive.ToArray();
    var entries = new AsfFormatDescriptor().List(new MemoryStream(changed), null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name.Contains("stream_01", StringComparison.OrdinalIgnoreCase)), Is.False);
      Assert.That(entries.Any(e => e.Name == "streams/stream_02.bin"), Is.True);
      Assert.That(Extract(changed, "streams/stream_02.bin"), Is.EqualTo(payload2));
      Assert.That(Extract(changed, "streams/stream_02.properties.bin"), Is.EqualTo(props2));
    });
  }

  [Test]
  public void Create_EncryptedStream_IsRejected() {
    var properties = BuildVideoStreamProperties(3, 320, 240, "WMV3", encrypted: true);
    using var output = new MemoryStream();
    var descriptor = (IArchiveCreatable)new AsfFormatDescriptor();

    Assert.That(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("streams/stream_03.properties.bin", properties),
      ArchiveInputInfo.InMemory("streams/stream_03.bin", Pattern(100, 0x55)),
      ArchiveInputInfo.InMemory("streams/stream_03.objects.csv", Manifest((100, 0u, true))),
    ], new FormatCreateOptions()), Throws.TypeOf<NotSupportedException>());
  }

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

  private static byte[] BuildVideoStreamProperties(int streamNumber, int width, int height, string fourCc, bool encrypted = false) {
    if (fourCc.Length != 4)
      throw new ArgumentException("FourCC must be exactly four characters.", nameof(fourCc));

    using var typeSpecific = new MemoryStream();
    WriteU32(typeSpecific, (uint)width);
    WriteU32(typeSpecific, (uint)height);
    typeSpecific.WriteByte(0);
    WriteU16(typeSpecific, 40);
    WriteU32(typeSpecific, 40); // BITMAPINFOHEADER.biSize
    WriteI32(typeSpecific, width);
    WriteI32(typeSpecific, height);
    WriteU16(typeSpecific, 1);  // planes
    WriteU16(typeSpecific, 24); // nominal bit count
    typeSpecific.Write(Encoding.ASCII.GetBytes(fourCc));
    WriteU32(typeSpecific, 0);  // image size
    WriteI32(typeSpecific, 0);  // XPelsPerMeter
    WriteI32(typeSpecific, 0);  // YPelsPerMeter
    WriteU32(typeSpecific, 0);  // colors used
    WriteU32(typeSpecific, 0);  // colors important
    var typeBytes = typeSpecific.ToArray();
    Assert.That(typeBytes.Length, Is.EqualTo(51), "test builder must emit VIDEOINFOHEADER-sized type data");

    using var body = new MemoryStream();
    body.Write(VideoStreamType);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0);
    WriteU32(body, (uint)typeBytes.Length);
    WriteU32(body, 0);
    WriteU16(body, (ushort)(streamNumber | (encrypted ? 0x8000 : 0)));
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

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i <= haystack.Length - needle.Length; ++i)
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
        return i;
    return -1;
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
