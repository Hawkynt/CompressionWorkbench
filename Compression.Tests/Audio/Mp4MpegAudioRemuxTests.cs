using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Mp3;
using FileFormat.Mp4;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Mp4MpegAudioRemuxTests {

  [TestCase(1, 3, 0x6B, 1_152)]
  [TestCase(1, 2, 0x6B, 1_152)]
  [TestCase(2, 3, 0x69, 576)]
  [TestCase(2, 2, 0x69, 1_152)]
  public void MpegAudioToMp4_PreservesFramesAndUsesRegisteredObjectType(
    int mpegVersion,
    int layer,
    int expectedObjectType,
    int expectedDuration
  ) {
    var first = BuildMpegAudioFrame(mpegVersion, layer, payloadSeed: 0x17);
    var second = BuildMpegAudioFrame(mpegVersion, layer, payloadSeed: 0x53);
    byte[] inputBytes = [.. first, .. second];

    using var input = new MemoryStream(inputBytes, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new Mp3FormatDescriptor(),
      output,
      new Mp4FormatDescriptor());

    var mp4 = output.ToArray();
    var audio = new Mp4Demuxer().Demux(mp4).Single(static track => track.HandlerType == "soun");
    var esds = ReadEsds(mp4);
    var stts = ReadStts(mp4);

    Assert.Multiple(() => {
      Assert.That(audio.CodecFourCc, Is.EqualTo("mp4a"));
      Assert.That(audio.Samples.Count, Is.EqualTo(2));
      Assert.That(audio.Samples[0].Data, Is.EqualTo(first));
      Assert.That(audio.Samples[1].Data, Is.EqualTo(second));
      Assert.That(esds.ObjectType, Is.EqualTo(expectedObjectType));
      Assert.That(esds.HasDecoderSpecificInfo, Is.False,
        "MPEG-1/2 Layer II/III in mp4a is identified by objectTypeIndication and must not carry AAC AudioSpecificConfig.");
      Assert.That(stts.EntryCount, Is.EqualTo(1));
      Assert.That(stts.SampleCount, Is.EqualTo(2));
      Assert.That(stts.SampleDelta, Is.EqualTo(expectedDuration));
    });
  }

  [Test]
  public void Mpeg25ToMp4_IsRejectedRatherThanMislabelled() {
    var frame = BuildMpegAudioFrame(25, layer: 3);
    using var input = new MemoryStream(frame, writable: false);
    Assert.That(Mp3AudioPacketAdapter.Instance.TryDemux(input, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);

    var target = new Mp4FormatDescriptor();
    var options = new FormatCreateOptions();
    Assert.That(target.CanMux(encoded!.Format, options, out var reason), Is.False);
    Assert.That(reason, Does.Contain("no registered MP4 objectTypeIndication"));

    using var output = new MemoryStream();
    var exception = Assert.Throws<NotSupportedException>(() => target.Mux(output, encoded, options));
    Assert.That(exception!.Message, Does.Contain("no registered MP4 objectTypeIndication"));
    Assert.That(output.Length, Is.Zero);
  }

  [Test]
  public void AacToMp4_KeepsMpeg4AudioObjectTypeAndDecoderSpecificInfo() {
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 44_100, 2),
      [new AudioPacket([0x11, 0x22, 0x33], 1_024)],
      [0x12, 0x10]);
    using var output = new MemoryStream();

    new Mp4FormatDescriptor().Mux(output, encoded, new FormatCreateOptions());

    var esds = ReadEsds(output.ToArray());
    Assert.Multiple(() => {
      Assert.That(esds.ObjectType, Is.EqualTo(0x40));
      Assert.That(esds.HasDecoderSpecificInfo, Is.True);
    });
  }

  [Test]
  public void Mp4Inventory_ReportsAacAndMpegPacketMuxing() {
    var capability = AudioConversionInventory.Describe(new Mp4FormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.MuxCodecs, Is.EquivalentTo(new[] { "aac", "mp3", "mp2" }));
    });
  }

  private static byte[] BuildMpegAudioFrame(int version, int layer, int sampleRateIndex = 0, byte payloadSeed = 0x5A) {
    int[] mpeg1Layer2Bitrates = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    int[] mpeg1Layer3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    int[] mpeg2Layer23Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    int[] mpeg1SampleRates = [44_100, 48_000, 32_000];
    const int bitrateIndex = 9;

    var versionBits = version switch {
      1 => 3,
      2 => 2,
      25 => 0,
      _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };
    if (layer is not (2 or 3)) throw new ArgumentOutOfRangeException(nameof(layer));
    if (sampleRateIndex is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(sampleRateIndex));

    var bitrateKbps = version == 1
      ? (layer == 2 ? mpeg1Layer2Bitrates : mpeg1Layer3Bitrates)[bitrateIndex]
      : mpeg2Layer23Bitrates[bitrateIndex];
    var sampleRate = mpeg1SampleRates[sampleRateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
    var coefficient = layer == 2 || version == 1 ? 144 : 72;
    var frameSize = coefficient * bitrateKbps * 1_000 / sampleRate;
    var frame = new byte[frameSize];
    var layerBits = 4 - layer;
    var header = 0xFFE0_0000u |
                 (uint)versionBits << 19 |
                 (uint)layerBits << 17 |
                 1u << 16 |
                 (uint)bitrateIndex << 12 |
                 (uint)sampleRateIndex << 10;
    BinaryPrimitives.WriteUInt32BigEndian(frame, header);
    for (var i = 4; i < frame.Length; ++i)
      frame[i] = (byte)(payloadSeed + i * 17);
    return frame;
  }

  private static (byte ObjectType, bool HasDecoderSpecificInfo) ReadEsds(byte[] mp4) {
    var body = FindBoxBodyInMoov(mp4, "esds");
    if (body.Length < 5) throw new InvalidDataException("esds box is truncated.");

    var offset = 4; // version + flags
    var es = ReadDescriptor(body, ref offset, 0x03);
    var esOffset = 3; // ES_ID + flags (writer emits no optional fields)
    var decoderConfig = ReadDescriptor(es, ref esOffset, 0x04);
    if (decoderConfig.Length < 13) throw new InvalidDataException("DecoderConfigDescriptor is truncated.");

    return (decoderConfig[0], decoderConfig.Length > 13 && decoderConfig[13] == 0x05);
  }

  private static (uint EntryCount, uint SampleCount, uint SampleDelta) ReadStts(byte[] mp4) {
    var body = FindBoxBodyInMoov(mp4, "stts");
    if (body.Length < 16) throw new InvalidDataException("stts box is truncated.");
    return (
      BinaryPrimitives.ReadUInt32BigEndian(body[4..]),
      BinaryPrimitives.ReadUInt32BigEndian(body[8..]),
      BinaryPrimitives.ReadUInt32BigEndian(body[12..]));
  }

  private static ReadOnlySpan<byte> ReadDescriptor(ReadOnlySpan<byte> bytes, ref int offset, byte expectedTag) {
    if ((uint)offset >= (uint)bytes.Length || bytes[offset++] != expectedTag)
      throw new InvalidDataException($"Expected descriptor tag 0x{expectedTag:X2}.");

    var length = 0;
    var terminated = false;
    for (var i = 0; i < 4; ++i) {
      if ((uint)offset >= (uint)bytes.Length) throw new InvalidDataException("Descriptor length is truncated.");
      var value = bytes[offset++];
      length = checked((length << 7) | (value & 0x7F));
      if ((value & 0x80) == 0) {
        terminated = true;
        break;
      }
    }
    if (!terminated || length > bytes.Length - offset)
      throw new InvalidDataException("Descriptor length is invalid.");

    var body = bytes.Slice(offset, length);
    offset += length;
    return body;
  }

  private static ReadOnlySpan<byte> FindBoxBodyInMoov(byte[] file, string type) {
    var moov = FindTopLevelBox(file, "moov");
    var typeBytes = Encoding.ASCII.GetBytes(type);
    for (var typeOffset = 4; typeOffset <= moov.Length - 4; ++typeOffset) {
      if (!moov.Slice(typeOffset, 4).SequenceEqual(typeBytes)) continue;
      var boxOffset = typeOffset - 4;
      var size = BinaryPrimitives.ReadUInt32BigEndian(moov[boxOffset..]);
      if (size < 8 || size > moov.Length - boxOffset) continue;
      return moov.Slice(typeOffset + 4, checked((int)size - 8));
    }
    throw new InvalidDataException($"MP4 box '{type}' was not found inside moov.");
  }

  private static ReadOnlySpan<byte> FindTopLevelBox(byte[] file, string type) {
    var offset = 0;
    while (offset + 8 <= file.Length) {
      var size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
      if (size < 8 || size > file.Length - offset) break;
      if (Encoding.ASCII.GetString(file, offset + 4, 4) == type)
        return file.AsSpan(offset + 8, checked((int)size - 8));
      offset = checked(offset + (int)size);
    }
    throw new InvalidDataException($"Top-level MP4 box '{type}' was not found.");
  }
}
