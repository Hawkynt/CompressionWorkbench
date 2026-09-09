using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Flv;

namespace Compression.Tests.Flv;

[TestFixture]
public sealed class FlvMuxerTests {

  private static readonly byte[] AacConfig = [0x12, 0x10]; // AAC-LC, 44.1 kHz, stereo
  private static readonly byte[] AacFrame1 = [0x21, 0x10, 0x05, 0x00];
  private static readonly byte[] AacFrame2 = [0x21, 0x10, 0x06, 0x00, 0x7F];
  private static readonly byte[] Mp3Frame1 = [0xFF, 0xFB, 0x90, 0x00, 0xAA, 0xBB];
  private static readonly byte[] Mp3Frame2 = [0xFF, 0xFB, 0x90, 0x00, 0xCC, 0xDD];

  [Test, Category("HappyPath")]
  public void RawMux_WritesHeaderTagFieldsAndPreviousSizes() {
    var tags = new[] {
      new FlvRawTag(FlvReader.TagScript, 0x12345678, 0, [0x02, 0x00, 0x01, (byte)'x']),
      new FlvRawTag(FlvReader.TagAudio, 0x00000021, 0, [0x2F, 0xAA, 0xBB]),
      new FlvRawTag(FlvReader.TagVideo, 0x0000002A, 0, [0x12, 0xDE, 0xAD]),
    };

    using var output = new MemoryStream();
    FlvMuxer.Mux(output, tags);
    var data = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(data.AsSpan(0, 3).ToArray(), Is.EqualTo("FLV"u8.ToArray()));
      Assert.That(data[3], Is.EqualTo(1));
      Assert.That(data[4], Is.EqualTo(0x05), "audio + video header flags");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(5, 4)), Is.EqualTo(9));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(9, 4)), Is.Zero);
    });

    const int firstTag = 13;
    Assert.Multiple(() => {
      Assert.That(data[firstTag], Is.EqualTo(FlvReader.TagScript));
      Assert.That(ReadUInt24(data.AsSpan(firstTag + 1, 3)), Is.EqualTo(4u));
      Assert.That(ReadUInt24(data.AsSpan(firstTag + 4, 3)), Is.EqualTo(0x345678u));
      Assert.That(data[firstTag + 7], Is.EqualTo(0x12));
      Assert.That(ReadUInt24(data.AsSpan(firstTag + 8, 3)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(firstTag + 11 + 4, 4)), Is.EqualTo(15u));
    });

    var parsed = FlvReader.Read(data);
    Assert.Multiple(() => {
      Assert.That(parsed.TagCount, Is.EqualTo(3));
      Assert.That(parsed.LastTimestampMs, Is.EqualTo(0x12345678u));
      Assert.That(parsed.HasAudioFlag, Is.True);
      Assert.That(parsed.HasVideoFlag, Is.True);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remux_PreservesCanonicalNativeTagsByteForByte() {
    var original = BuildIndependentFlv();

    using var input = new MemoryStream(original, writable: false);
    using var output = new MemoryStream();
    FlvMuxer.Remux(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_AacMuxAndDemux_PreservesAccessUnitsWithoutDecode() {
    var descriptor = new FlvFormatDescriptor();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 44100, 2),
      [new AudioPacket(AacFrame1, 1024), new AudioPacket(AacFrame2, 1024)],
      AacConfig);

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, new FormatCreateOptions());
    var flv = FlvReader.Read(output.ToArray());

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(flv.HasAudioFlag, Is.True);
      Assert.That(flv.HasVideoFlag, Is.False);
      Assert.That(flv.TagCount, Is.EqualTo(3), "AAC sequence header + two access units");
      Assert.That(flv.Streams.Single().Codec, Is.EqualTo("aac"));
      Assert.That(flv.Streams.Single().TagCount, Is.EqualTo(2));
      Assert.That(flv.Streams.Single().LastTimestampMs, Is.EqualTo(23u));
    });

    output.Position = 0;
    Assert.That(descriptor.TryDemux(output, out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(demuxed!.Format.CodecId, Is.EqualTo("aac"));
      Assert.That(demuxed.Format.SampleRate, Is.EqualTo(44100));
      Assert.That(demuxed.Format.Channels, Is.EqualTo(2));
      Assert.That(demuxed.CodecPrivateData, Is.EqualTo(AacConfig));
      Assert.That(demuxed.Packets.Select(static packet => packet.Data), Is.EqualTo(new[] { AacFrame1, AacFrame2 }));
      Assert.That(demuxed.Packets.All(static packet => packet.DurationSamples == 1024), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Mp3Mux_UsesPacketDurationsForTimestamps() {
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("mp3", 44100, 2),
      [new AudioPacket(Mp3Frame1, 1152), new AudioPacket(Mp3Frame2, 1152)]);

    using var output = new MemoryStream();
    FlvMuxer.MuxAudio(output, encoded);
    var flv = FlvReader.Read(output.ToArray());
    var audio = flv.Streams.Single();

    Assert.Multiple(() => {
      Assert.That(flv.TagCount, Is.EqualTo(2));
      Assert.That(audio.Codec, Is.EqualTo("mp3"));
      Assert.That(audio.TagCount, Is.EqualTo(2));
      Assert.That(audio.FirstTimestampMs, Is.Zero);
      Assert.That(audio.LastTimestampMs, Is.EqualTo(26u));
      Assert.That(audio.Payload, Is.EqualTo(Mp3Frame1.Concat(Mp3Frame2).ToArray()));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Mp3Mux_WithoutPacketDuration_RefusesToInventTiming() {
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("mp3", 44100, 2),
      [new AudioPacket(Mp3Frame1)]);

    using var output = new MemoryStream();
    var ex = Assert.Throws<InvalidDataException>(() => FlvMuxer.MuxAudio(output, encoded));
    Assert.That(ex!.Message, Does.Contain("DurationSamples"));
  }

  [Test, Category("ErrorHandling")]
  public void RawMux_StreamIdBeyond24Bits_IsRejected() {
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() =>
      FlvMuxer.Mux(output, [new FlvRawTag(FlvReader.TagScript, 0, 0x01000000, [])]));
  }

  [Test, Category("ErrorHandling")]
  public void Remux_BadPreviousTagSize_IsRejected() {
    var broken = BuildIndependentFlv();
    broken[^1] ^= 0x01;

    using var input = new MemoryStream(broken, writable: false);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => FlvMuxer.Remux(input, output));
  }

  private static byte[] BuildIndependentFlv() {
    using var output = new MemoryStream();
    output.Write([
      (byte)'F', (byte)'L', (byte)'V', 0x01, 0x05,
      0x00, 0x00, 0x00, 0x09,
      0x00, 0x00, 0x00, 0x00,
    ]);
    WriteIndependentTag(output, FlvReader.TagScript, 0, [0x02, 0x00, 0x01, (byte)'x']);
    WriteIndependentTag(output, FlvReader.TagAudio, 23, [0xAF, 0x00, .. AacConfig]);
    WriteIndependentTag(output, FlvReader.TagAudio, 46, [0xAF, 0x01, .. AacFrame1]);
    WriteIndependentTag(output, FlvReader.TagVideo, 0x01020304, [0x12, 0xDE, 0xAD, 0xBE, 0xEF]);
    return output.ToArray();
  }

  private static void WriteIndependentTag(Stream output, byte tagType, uint timestamp, byte[] body) {
    Span<byte> header = stackalloc byte[11];
    header[0] = tagType;
    WriteUInt24(header[1..4], (uint)body.Length);
    WriteUInt24(header[4..7], timestamp & 0xFFFFFF);
    header[7] = (byte)(timestamp >> 24);
    output.Write(header);
    output.Write(body);
    Span<byte> previous = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(previous, (uint)(11 + body.Length));
    output.Write(previous);
  }

  private static uint ReadUInt24(ReadOnlySpan<byte> source)
    => ((uint)source[0] << 16) | ((uint)source[1] << 8) | source[2];

  private static void WriteUInt24(Span<byte> destination, uint value) {
    destination[0] = (byte)(value >> 16);
    destination[1] = (byte)(value >> 8);
    destination[2] = (byte)value;
  }
}
