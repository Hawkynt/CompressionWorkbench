#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

/// <summary>
/// Pins the general ASF container contract with heterogeneous tracks. A normal WMV file is
/// not "one video stream": it commonly combines WMV video with WMA audio and may carry
/// additional audio tracks. Canonical extraction must retain every encoded stream even when
/// an audio decoder also exposes convenience channel WAVs, and those artifacts must be
/// sufficient to rebuild the same stream set without decoding or re-encoding anything.
/// </summary>
[TestFixture]
public sealed class AsfMultiTrackMuxTests {
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  [Test]
  public void Create_MixedVideoAndMultipleAudioTracks_RoundTripsThroughCanonicalArtifacts() {
    var videoProperties = BuildVideoStreamProperties(1, 640, 360, "WMV3");
    var wmaProperties = BuildAudioStreamProperties(2, 0x0161, channels: 2, sampleRate: 8000,
      byteRate: 16000, blockAlign: 256, bitsPerSample: 16);
    var mp3Properties = BuildAudioStreamProperties(3, 0x0055, channels: 1, sampleRate: 48000,
      byteRate: 8000, blockAlign: 1, bitsPerSample: 0);

    var video = Pattern(1700, 0x21);
    var wma = new byte[512]; // two valid all-zero WMA v2 synthetic superframes used by the decoder tests
    var mp3 = Pattern(333, 0xB0); // opaque here: ASF muxing never parses the codec payload

    var original = Create(new[] {
      StreamArtifacts(1, videoProperties, video, Manifest((900, 0u, true), (800, 40u, false))),
      StreamArtifacts(2, wmaProperties, wma, Manifest((256, 0u, false), (256, 32u, false))),
      StreamArtifacts(3, mp3Properties, mp3, Manifest((111, 10u, false), (222, 30u, false))),
    }.SelectMany(static set => set).ToArray(), packetSize: 512);

    var descriptor = new AsfFormatDescriptor();
    var entries = descriptor.List(new MemoryStream(original), null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "streams/stream_01.bin"), Is.True, "video elementary stream");
      Assert.That(entries.Any(e => e.Name == "streams/stream_02.bin"), Is.True,
        "decoded WMA must still expose its encoded stream for remux");
      Assert.That(entries.Any(e => e.Name == "streams/stream_03.bin"), Is.True, "second audio elementary stream");
      Assert.That(entries.Any(e => e.Name == "streams/stream_02.properties.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "streams/stream_02.objects.csv"), Is.True);
      Assert.That(entries.Any(e => e.Kind == "Channel" && e.Name.StartsWith("streams/stream_02/", StringComparison.Ordinal)), Is.True,
        "WMA channel decode is an additive convenience view, not a replacement for canonical artifacts");
    });

    var recreatedInputs = new List<ArchiveInputInfo>();
    foreach (var streamNumber in new[] { 1, 2, 3 }) {
      foreach (var suffix in new[] { ".properties.bin", ".bin", ".objects.csv" }) {
        var path = $"streams/stream_{streamNumber:D2}{suffix}";
        recreatedInputs.Add(ArchiveInputInfo.InMemory(path, Extract(original, path)));
      }
    }

    var recreated = Create(recreatedInputs, packetSize: 512);

    Assert.Multiple(() => {
      Assert.That(Extract(recreated, "streams/stream_01.bin"), Is.EqualTo(video));
      Assert.That(Extract(recreated, "streams/stream_02.bin"), Is.EqualTo(wma));
      Assert.That(Extract(recreated, "streams/stream_03.bin"), Is.EqualTo(mp3));
      Assert.That(Extract(recreated, "streams/stream_01.properties.bin"), Is.EqualTo(videoProperties));
      Assert.That(Extract(recreated, "streams/stream_02.properties.bin"), Is.EqualTo(wmaProperties));
      Assert.That(Extract(recreated, "streams/stream_03.properties.bin"), Is.EqualTo(mp3Properties));
      Assert.That(Encoding.UTF8.GetString(Extract(recreated, "streams/stream_01.objects.csv")),
        Does.Contain("900,800,40,false"));
      Assert.That(Encoding.UTF8.GetString(Extract(recreated, "streams/stream_02.objects.csv")),
        Does.Contain("256,256,32,false"));
      Assert.That(Encoding.UTF8.GetString(Extract(recreated, "streams/stream_03.objects.csv")),
        Does.Contain("111,222,30,false"));
    });

    var infos = new[] { 1, 2, 3 }
      .Select(number => Encoding.UTF8.GetString(Extract(recreated, $"streams/stream_{number:D2}.info.txt")))
      .ToArray();
    Assert.Multiple(() => {
      Assert.That(infos[0], Does.Contain("type = video"));
      Assert.That(infos[0], Does.Contain("codec = wmv3"));
      Assert.That(infos[1], Does.Contain("type = audio"));
      Assert.That(infos[1], Does.Contain("codec = wmav2"));
      Assert.That(infos[2], Does.Contain("type = audio"));
      Assert.That(infos[2], Does.Contain("codec = mp3"));
    });
  }

  [Test]
  public void Remove_AudioTrack_FromMixedContainer_PreservesOtherTracksByteExactly() {
    var videoProperties = BuildVideoStreamProperties(1, 320, 240, "WMV3");
    var wmaProperties = BuildAudioStreamProperties(2, 0x0161, 2, 8000, 16000, 256, 16);
    var mp3Properties = BuildAudioStreamProperties(3, 0x0055, 1, 44100, 12000, 1, 0);
    var video = Pattern(700, 0x10);
    var wma = new byte[256];
    var mp3 = Pattern(400, 0xD2);

    using var archive = new MemoryStream(Create(new[] {
      StreamArtifacts(1, videoProperties, video, Manifest((700, 0u, true))),
      StreamArtifacts(2, wmaProperties, wma, Manifest((256, 0u, false))),
      StreamArtifacts(3, mp3Properties, mp3, Manifest((400, 20u, false))),
    }.SelectMany(static set => set).ToArray(), packetSize: 512), writable: true);

    ((IArchiveModifiable)new AsfFormatDescriptor()).Remove(archive, ["streams/stream_02.bin"]);
    var changed = archive.ToArray();
    var entries = new AsfFormatDescriptor().List(new MemoryStream(changed), null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name.Contains("stream_02", StringComparison.OrdinalIgnoreCase)), Is.False);
      Assert.That(Extract(changed, "streams/stream_01.bin"), Is.EqualTo(video));
      Assert.That(Extract(changed, "streams/stream_03.bin"), Is.EqualTo(mp3));
      Assert.That(Extract(changed, "streams/stream_01.properties.bin"), Is.EqualTo(videoProperties));
      Assert.That(Extract(changed, "streams/stream_03.properties.bin"), Is.EqualTo(mp3Properties));
    });
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
        ["packet-size"] = packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

  private static byte[] BuildAudioStreamProperties(
    int streamNumber,
    int formatTag,
    int channels,
    int sampleRate,
    int byteRate,
    int blockAlign,
    int bitsPerSample
  ) {
    using var typeSpecific = new MemoryStream();
    WriteU16(typeSpecific, checked((ushort)formatTag));
    WriteU16(typeSpecific, checked((ushort)channels));
    WriteU32(typeSpecific, checked((uint)sampleRate));
    WriteU32(typeSpecific, checked((uint)byteRate));
    WriteU16(typeSpecific, checked((ushort)blockAlign));
    WriteU16(typeSpecific, checked((ushort)bitsPerSample));
    WriteU16(typeSpecific, 0); // WAVEFORMATEX.cbSize
    return BuildStreamProperties(streamNumber, AudioStreamType, typeSpecific.ToArray());
  }

  private static byte[] BuildVideoStreamProperties(int streamNumber, int width, int height, string fourCc) {
    using var typeSpecific = new MemoryStream();
    WriteU32(typeSpecific, checked((uint)width));
    WriteU32(typeSpecific, checked((uint)height));
    typeSpecific.WriteByte(0); // reserved flags
    WriteU16(typeSpecific, 40); // format data size
    WriteU32(typeSpecific, 40); // BITMAPINFOHEADER.biSize
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
      result[i] = unchecked((byte)(seed + i * 29 + (i >> 2)));
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