#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AmrNb;
using Codec.AmrWb;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Amr;

namespace Compression.Tests.Amr;

[TestFixture]
public class AmrWriteTests {
  private static readonly string[] NbModes = ["4.75", "5.15", "5.90", "6.70", "7.40", "7.95", "10.2", "12.2"];
  private static readonly string[] WbModes = ["6.60", "8.85", "12.65", "14.25", "15.85", "18.25", "19.85", "23.05", "23.85"];
  private static readonly int[] NbSpeechBits = [95, 103, 118, 134, 148, 159, 204, 244, 39, -1, -1, -1, -1, -1, -1, 0];
  private static readonly int[] WbSpeechBits = [132, 177, 253, 285, 317, 365, 397, 461, 477, 40, -1, -1, -1, -1, 0, 0];

  [TestCaseSource(nameof(NbModes))]
  public void Encode_Nb_AllModes_AllChannelCounts(string mode) {
    var descriptor = new AmrFormatDescriptor();
    for (var channels = 1; channels <= 6; ++channels) {
      var options = Options(("mode", mode));
      using var output = new MemoryStream();
      descriptor.EncodePcm(output, MakePcm(AmrNbCodec.SampleRate, channels, AmrNbCodec.SamplesPerFrame), "amr-nb", options);
      var file = output.ToArray();

      Assert.That(file.AsSpan().StartsWith(channels == 1 ? "#!AMR\n"u8 : "#!AMR_MC1.0\n"u8), Is.True);
      Assert.That(descriptor.TryDemux(new MemoryStream(file), out var stream), Is.True);
      Assert.Multiple(() => {
        Assert.That(stream!.Format.CodecId, Is.EqualTo("amr-nb"));
        Assert.That(stream.Format.SampleRate, Is.EqualTo(8000));
        Assert.That(stream.Format.Channels, Is.EqualTo(channels));
        Assert.That(stream.Packets, Has.Count.EqualTo(1));
      });
    }
  }

  [TestCaseSource(nameof(WbModes))]
  public void Encode_Wb_AllModes_AllChannelCounts(string mode) {
    var descriptor = new AmrFormatDescriptor();
    for (var channels = 1; channels <= 6; ++channels) {
      var options = Options(("mode", mode));
      using var output = new MemoryStream();
      descriptor.EncodePcm(output, MakePcm(AmrWbCodec.SampleRate, channels, AmrWbCodec.SamplesPerFrame), "amr-wb", options);
      var file = output.ToArray();

      Assert.That(file.AsSpan().StartsWith(channels == 1 ? "#!AMR-WB\n"u8 : "#!AMR-WB_MC1.0\n"u8), Is.True);
      Assert.That(descriptor.TryDemux(new MemoryStream(file), out var stream), Is.True);
      Assert.Multiple(() => {
        Assert.That(stream!.Format.CodecId, Is.EqualTo("amr-wb"));
        Assert.That(stream.Format.SampleRate, Is.EqualTo(16000));
        Assert.That(stream.Format.Channels, Is.EqualTo(channels));
        Assert.That(stream.Packets, Has.Count.EqualTo(1));
      });
    }
  }

  [TestCase("amr", 8000, false)]
  [TestCase("amr-nb", 8000, false)]
  [TestCase("amrnb", 8000, false)]
  [TestCase("amr", 16000, true)]
  [TestCase("amr-wb", 16000, true)]
  [TestCase("amrwb", 16000, true)]
  public void CodecAliases_SelectExpectedVariant(string codecId, int sampleRate, bool wideband) {
    var descriptor = new AmrFormatDescriptor();
    var format = new AudioPcmFormat(sampleRate, 1, 16);
    Assert.That(descriptor.CanEncode(format, codecId, new FormatCreateOptions(), out var reason), Is.True, reason);

    using var output = new MemoryStream();
    descriptor.EncodePcm(output, MakePcm(sampleRate, 1, wideband ? 320 : 160), codecId, new FormatCreateOptions());
    Assert.That(descriptor.TryDemux(new MemoryStream(output.ToArray()), out var stream), Is.True);
    Assert.That(stream!.Format.CodecId, Is.EqualTo(wideband ? "amr-wb" : "amr-nb"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Dtx_Silence_EncodesAndDemuxes(bool wideband) {
    var sampleRate = wideband ? 16000 : 8000;
    var samples = wideband ? 320 : 160;
    var descriptor = new AmrFormatDescriptor();
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(sampleRate, 6, 16), new byte[samples * 6 * 2]);
    using var output = new MemoryStream();
    descriptor.EncodePcm(output, pcm, wideband ? "amr-wb" : "amr-nb", Options(("dtx", "true")));
    Assert.That(descriptor.TryDemux(new MemoryStream(output.ToArray()), out var stream), Is.True);
    Assert.That(stream!.Packets, Has.Count.EqualTo(1));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void PadFinalFrame_CanBeDisabled(bool wideband) {
    var sampleRate = wideband ? 16000 : 8000;
    var samples = (wideband ? 320 : 160) - 1;
    var descriptor = new AmrFormatDescriptor();
    var pcm = MakePcm(sampleRate, 2, samples);
    Assert.Throws<ArgumentException>(() => descriptor.EncodePcm(
      Stream.Null, pcm, wideband ? "amr-wb" : "amr-nb", Options(("pad-final-frame", "false"))));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void SixChannel_EncodeDecode_PreservesPcmShape(bool wideband) {
    var sampleRate = wideband ? 16000 : 8000;
    var samples = wideband ? 320 : 160;
    var descriptor = new AmrFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.EncodePcm(output, MakePcm(sampleRate, 6, samples), wideband ? "amr-wb" : "amr-nb", new FormatCreateOptions());

    var decoded = descriptor.DecodePcm(new MemoryStream(output.ToArray()));
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(6));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.FrameCount, Is.EqualTo(samples));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void DemuxMux_RoundTrip_IsByteExactForCanonicalFiles(bool wideband) {
    var sampleRate = wideband ? 16000 : 8000;
    var samples = (wideband ? 320 : 160) * 2;
    var codec = wideband ? "amr-wb" : "amr-nb";
    var descriptor = new AmrFormatDescriptor();
    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, MakePcm(sampleRate, 6, samples), codec, new FormatCreateOptions());
    var original = encoded.ToArray();

    Assert.That(descriptor.TryDemux(new MemoryStream(original), out var stream), Is.True);
    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, stream!, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void MuxDemux_Nb_AllDefinedFrameTypes() {
    AssertDefinedFrameTypesRoundTrip(false, [0, 1, 2, 3, 4, 5, 6, 7, 8, 15]);
  }

  [Test]
  public void MuxDemux_Wb_AllDefinedFrameTypes() {
    AssertDefinedFrameTypesRoundTrip(true, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 14, 15]);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Mux_MixedFrameTypesInSixChannelBlock_RoundTrips(bool wideband) {
    var bits = wideband ? WbSpeechBits : NbSpeechBits;
    using var block = new MemoryStream();
    for (var frameType = 0; frameType < 6; ++frameType)
      block.Write(Frame(frameType, bits));

    var stream = new AudioEncodedStream(
      new AudioStreamFormat(wideband ? "amr-wb" : "amr-nb", wideband ? 16000 : 8000, 6),
      [new AudioPacket(block.ToArray(), wideband ? 320 : 160)]);
    var descriptor = new AmrFormatDescriptor();
    using var file = new MemoryStream();
    descriptor.Mux(file, stream, new FormatCreateOptions());

    Assert.That(descriptor.TryDemux(new MemoryStream(file.ToArray()), out var demuxed), Is.True);
    Assert.That(demuxed!.Packets.Single().Data, Is.EqualTo(block.ToArray()));
  }

  [Test]
  public void McReader_IgnoresReservedChannelDescriptionBits() {
    using var file = new MemoryStream();
    file.Write("#!AMR_MC1.0\n"u8);
    Span<byte> description = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(description, 0xA5A5A502);
    file.Write(description);
    file.WriteByte(0x7C);
    file.WriteByte(0x7C);

    var descriptor = new AmrFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(file.ToArray()), out var stream), Is.True);
    Assert.That(stream!.Format.Channels, Is.EqualTo(2));
  }

  [Test]
  public void Mux_ZerosReservedChannelDescriptionAndFramePaddingBits() {
    using var source = new MemoryStream();
    source.Write("#!AMR_MC1.0\n"u8);
    Span<byte> description = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(description, 0xA5A5A502);
    source.Write(description);
    source.WriteByte(0xFF);
    source.WriteByte(0xFF);

    var descriptor = new AmrFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(source.ToArray()), out var stream), Is.True);
    using var output = new MemoryStream();
    descriptor.Mux(output, stream!, new FormatCreateOptions());
    var normalized = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(12, 4)), Is.EqualTo(2));
      Assert.That(normalized[16], Is.EqualTo(0x7C));
      Assert.That(normalized[17], Is.EqualTo(0x7C));
    });
  }

  [Test]
  public void Demux_RejectsTruncatedMultichannelBlockAndReservedFrameType() {
    var descriptor = new AmrFormatDescriptor();

    using var truncated = new MemoryStream();
    truncated.Write("#!AMR_MC1.0\n"u8);
    Span<byte> description = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(description, 2);
    truncated.Write(description);
    truncated.WriteByte(0x7C);
    Assert.That(descriptor.TryDemux(new MemoryStream(truncated.ToArray()), out _), Is.False);

    var reserved = "#!AMR\n"u8.ToArray().Concat([(byte)((9 << 3) | 0x04)]).ToArray();
    Assert.That(descriptor.TryDemux(new MemoryStream(reserved), out _), Is.False);
  }

  [Test]
  public void Create_FromTwoMonoWavs_ProducesMcAmr() {
    var descriptor = new AmrFormatDescriptor();
    var left = MakePcm(8000, 1, 160).InterleavedData;
    var right = MakePcm(8000, 1, 160).InterleavedData;
    var inputs = new[] {
      ArchiveInputInfo.InMemory("FL.wav", PcmCodec.ToWavBlob(left, 1, 8000, 16, 1)),
      ArchiveInputInfo.InMemory("FR.wav", PcmCodec.ToWavBlob(right, 1, 8000, 16, 1)),
    };
    using var output = new MemoryStream();
    descriptor.Create(output, inputs, Options(("mode", "4.75")));

    Assert.That(output.ToArray().AsSpan().StartsWith("#!AMR_MC1.0\n"u8), Is.True);
    Assert.That(descriptor.TryDemux(new MemoryStream(output.ToArray()), out var stream), Is.True);
    Assert.That(stream!.Format.Channels, Is.EqualTo(2));
  }

  [Test]
  public void Create_FullFile_IsByteExact() {
    var original = "#!AMR\n"u8.ToArray().Concat([(byte)0x7C]).ToArray();
    var descriptor = new AmrFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(output, [ArchiveInputInfo.InMemory("FULL.amr", original)], new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void CanEncode_RejectsImpossibleParameterCombinations() {
    var descriptor = new AmrFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 0, 16), "amr-nb", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 7, 16), "amr-nb", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(16000, 1, 16), "amr-nb", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 1, 8), "amr-nb", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 1, 16), "amr-wb", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(44100, 1, 16), "amr", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 1, 16), "aac", new(), out _), Is.False);
      Assert.That(descriptor.CanEncode(new AudioPcmFormat(8000, 1, 16), "amr-nb", Options(("mode", "99")), out _), Is.False);
    });
  }

  private static void AssertDefinedFrameTypesRoundTrip(bool wideband, int[] frameTypes) {
    var descriptor = new AmrFormatDescriptor();
    var bits = wideband ? WbSpeechBits : NbSpeechBits;
    foreach (var frameType in frameTypes) {
      var frame = Frame(frameType, bits);
      var stream = new AudioEncodedStream(
        new AudioStreamFormat(wideband ? "amr-wb" : "amr-nb", wideband ? 16000 : 8000, 1),
        [new AudioPacket(frame, wideband ? 320 : 160)]);
      using var file = new MemoryStream();
      descriptor.Mux(file, stream, new FormatCreateOptions());
      Assert.That(descriptor.TryDemux(new MemoryStream(file.ToArray()), out var parsed), Is.True, $"FT={frameType}");
      Assert.That(parsed!.Packets.Single().Data, Is.EqualTo(frame), $"FT={frameType}");
    }
  }

  private static byte[] Frame(int frameType, int[] speechBits) {
    var bits = speechBits[frameType];
    if (bits < 0)
      throw new ArgumentOutOfRangeException(nameof(frameType));
    var frame = new byte[1 + ((bits + 7) / 8)];
    frame[0] = (byte)((frameType << 3) | 0x04);
    return frame;
  }

  private static AudioPcmBuffer MakePcm(int sampleRate, int channels, int samplesPerChannel) {
    var data = new byte[samplesPerChannel * channels * 2];
    var offset = 0;
    for (var sample = 0; sample < samplesPerChannel; ++sample)
      for (var channel = 0; channel < channels; ++channel) {
        var value = (short)(((sample * 97 + channel * 701) % 12000) - 6000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), value);
        offset += 2;
      }
    return new AudioPcmBuffer(new AudioPcmFormat(sampleRate, channels, 16), data);
  }

  private static FormatCreateOptions Options(params (string Key, string Value)[] values) {
    var options = new FormatCreateOptions();
    foreach (var (key, value) in values)
      options.FormatSpecific[key] = value;
    return options;
  }
}
