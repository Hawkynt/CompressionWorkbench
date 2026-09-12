#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfSimpleIndexTests {
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] SimpleIndexObject =
    [0x90, 0x08, 0x00, 0x33, 0xB1, 0xE5, 0xCF, 0x11, 0x89, 0xF4, 0x00, 0xA0, 0xC9, 0x03, 0x49, 0xCB];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  [Test]
  public void Create_MultipleVideoStreams_WritesOrderedSimpleIndexes_FromActualPacketLayout() {
    var video1 = BuildVideoStreamProperties(1, 640, 360, "WMV3");
    var audio = BuildAudioStreamProperties(2);
    var video3 = BuildVideoStreamProperties(3, 640, 360, "WMV3");

    var container = Create([
      .. StreamArtifacts(1, video1, Pattern(260, 0x11), Manifest(
        (80, 0u, true),
        (20, 900u, false),
        (150, 1500u, true),
        (10, 4200u, false))),
      .. StreamArtifacts(2, audio, Pattern(30, 0x61), Manifest((30, 500u, false))),
      .. StreamArtifacts(3, video3, Pattern(190, 0xA1), Manifest(
        (40, 0u, true),
        (140, 2500u, true),
        (10, 4200u, false))),
    ], packetSize: 100);

    var headerSize = checked((int)ReadU64(container, 16));
    var fileProperties = FindHeaderChild(container, FilePropertiesObject);
    var declaredFileSize = ReadU64(container, fileProperties + 40);
    var flags = ReadU32(container, fileProperties + 88);

    Assert.That(container.AsSpan(headerSize, 16).SequenceEqual(DataObject), Is.True);
    var dataSize = checked((int)ReadU64(container, headerSize + 16));
    var dataFileId = container.AsSpan(headerSize + 24, 16).ToArray();
    var firstIndexOffset = checked(headerSize + dataSize);
    var first = ReadSimpleIndex(container, firstIndexOffset);
    var second = ReadSimpleIndex(container, checked(firstIndexOffset + first.Size));

    Assert.Multiple(() => {
      Assert.That(declaredFileSize, Is.EqualTo((ulong)container.Length), "File Properties must include top-level indexes");
      Assert.That(flags & 0x02u, Is.EqualTo(0x02u), "indexed A/V must advertise seekability");
      Assert.That(first.FileId, Is.EqualTo(dataFileId));
      Assert.That(second.FileId, Is.EqualTo(dataFileId));
      Assert.That(first.Interval100Ns, Is.EqualTo(10_000_000UL));
      Assert.That(second.Interval100Ns, Is.EqualTo(10_000_000UL));
      Assert.That(first.MaximumPacketCount, Is.EqualTo(3u));
      Assert.That(second.MaximumPacketCount, Is.EqualTo(3u));
      Assert.That(firstIndexOffset + first.Size + second.Size, Is.EqualTo(container.Length),
        "Simple Index Objects must be the last top-level objects");
    });

    // Packet size 100 leaves 68 payload bytes per packet. Global presentation-time
    // interleaving produces:
    // s1 key@0 -> packets 0..1, s3 key@0 -> 2, audio@500 -> 3,
    // s1 non-key@900 -> 4, s1 key@1500 -> 5..7, s3 key@2500 -> 8..10.
    Assert.That(first.Entries, Is.EqualTo(new[] {
      new IndexEntry(0, 2), new IndexEntry(0, 2), new IndexEntry(5, 3),
      new IndexEntry(5, 3), new IndexEntry(5, 3),
    }), "first Simple Index must correspond to video stream 1");
    Assert.That(second.Entries, Is.EqualTo(new[] {
      new IndexEntry(2, 1), new IndexEntry(2, 1), new IndexEntry(2, 1),
      new IndexEntry(8, 3), new IndexEntry(8, 3),
    }), "second Simple Index must correspond to video stream 3");
  }

  [Test]
  public void Create_VideoWithoutCleanpoints_WritesEmptyIndex_AndDoesNotClaimSeekable() {
    var container = Create([
      .. StreamArtifacts(1, BuildVideoStreamProperties(1, 320, 240, "WMV3"),
        Pattern(50, 0x33), Manifest((50, 0u, false))),
      .. StreamArtifacts(2, BuildAudioStreamProperties(2),
        Pattern(20, 0x73), Manifest((20, 0u, false))),
    ], packetSize: 100);

    var fileProperties = FindHeaderChild(container, FilePropertiesObject);
    var flags = ReadU32(container, fileProperties + 88);
    var headerSize = checked((int)ReadU64(container, 16));
    var dataSize = checked((int)ReadU64(container, headerSize + 16));
    var index = ReadSimpleIndex(container, checked(headerSize + dataSize));

    Assert.Multiple(() => {
      Assert.That(flags & 0x02u, Is.Zero);
      Assert.That(index.Size, Is.EqualTo(56));
      Assert.That(index.MaximumPacketCount, Is.Zero);
      Assert.That(index.Entries, Is.Empty);
      Assert.That(headerSize + dataSize + index.Size, Is.EqualTo(container.Length));
    });
  }

  private readonly record struct IndexEntry(uint PacketNumber, ushort PacketCount);

  private sealed record ParsedIndex(
    int Size,
    byte[] FileId,
    ulong Interval100Ns,
    uint MaximumPacketCount,
    IReadOnlyList<IndexEntry> Entries);

  private static ParsedIndex ReadSimpleIndex(byte[] container, int offset) {
    Assert.That(container.AsSpan(offset, 16).SequenceEqual(SimpleIndexObject), Is.True,
      $"expected Simple Index Object at 0x{offset:X}");
    var size = checked((int)ReadU64(container, offset + 16));
    Assert.That(size, Is.GreaterThanOrEqualTo(56));
    var fileId = container.AsSpan(offset + 24, 16).ToArray();
    var interval = ReadU64(container, offset + 40);
    var maximumPacketCount = ReadU32(container, offset + 48);
    var entryCount = checked((int)ReadU32(container, offset + 52));
    Assert.That(size, Is.EqualTo(checked(56 + entryCount * 6)));

    var entries = new IndexEntry[entryCount];
    var p = offset + 56;
    for (var i = 0; i < entries.Length; ++i) {
      entries[i] = new IndexEntry(ReadU32(container, p), ReadU16(container, p + 4));
      p += 6;
    }
    return new ParsedIndex(size, fileId, interval, maximumPacketCount, entries);
  }

  private static int FindHeaderChild(byte[] container, byte[] guid) {
    var childCount = ReadU32(container, 24);
    var position = 30;
    for (uint i = 0; i < childCount; ++i) {
      if (container.AsSpan(position, 16).SequenceEqual(guid))
        return position;
      position = checked(position + (int)ReadU64(container, position + 16));
    }
    throw new AssertionException($"Header child {Convert.ToHexString(guid)} not found.");
  }

  private static ArchiveInputInfo[] StreamArtifacts(int streamNumber, byte[] properties, byte[] payload, byte[] manifest) => [
    ArchiveInputInfo.InMemory($"streams/stream_{streamNumber:D2}.properties.bin", properties),
    ArchiveInputInfo.InMemory($"streams/stream_{streamNumber:D2}.bin", payload),
    ArchiveInputInfo.InMemory($"streams/stream_{streamNumber:D2}.objects.csv", manifest),
  ];

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, int packetSize) {
    using var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["packet-size"] = packetSize.ToString(CultureInfo.InvariantCulture),
      },
    };
    ((IArchiveCreatable)new AsfFormatDescriptor()).Create(output, inputs, options);
    return output.ToArray();
  }

  private static byte[] BuildAudioStreamProperties(int streamNumber) {
    using var typeSpecific = new MemoryStream();
    WriteU16(typeSpecific, 0x0055); // MP3; payload remains opaque to ASF
    WriteU16(typeSpecific, 2);
    WriteU32(typeSpecific, 48000);
    WriteU32(typeSpecific, 16000);
    WriteU16(typeSpecific, 1);
    WriteU16(typeSpecific, 0);
    WriteU16(typeSpecific, 0);
    return BuildStreamProperties(streamNumber, AudioStreamType, typeSpecific.ToArray());
  }

  private static byte[] BuildVideoStreamProperties(int streamNumber, int width, int height, string fourCc) {
    using var typeSpecific = new MemoryStream();
    WriteU32(typeSpecific, checked((uint)width));
    WriteU32(typeSpecific, checked((uint)height));
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
    return BuildStreamProperties(streamNumber, VideoStreamType, typeSpecific.ToArray());
  }

  private static byte[] BuildStreamProperties(int streamNumber, byte[] streamType, byte[] typeSpecific) {
    using var body = new MemoryStream();
    body.Write(streamType);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0);
    WriteU32(body, checked((uint)typeSpecific.Length));
    WriteU32(body, 0);
    WriteU16(body, checked((ushort)streamNumber));
    WriteU32(body, 0);
    body.Write(typeSpecific);
    return body.ToArray();
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
      result[i] = unchecked((byte)(seed + i * 17 + (i >> 1)));
    return result;
  }

  private static ushort ReadU16(byte[] bytes, int offset)
    => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

  private static uint ReadU32(byte[] bytes, int offset)
    => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

  private static ulong ReadU64(byte[] bytes, int offset)
    => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));

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
