using Codec.Ac3;
using Codec.Pcm;
using Compression.Registry;
using Compression.Tests.Codecs.Ac3;
using FileFormat.Ac3;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Ac3FormatDescriptorTests {

  [Test]
  public void LegacyDemuxMux_RoundTripsEverySyncframeByteExact() {
    const int sampleRate = 48_000;
    var pcm = Signal(1536 * 2, channels: 2, sampleRate);
    var encoded = Ac3Codec.Encode(pcm, new Ac3EncoderOptions(sampleRate, 192_000, Acmod: 2));
    var descriptor = new Ac3FormatDescriptor();

    using var input = new MemoryStream(encoded, writable: false);
    Assert.That(descriptor.TryDemux(input, out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(stream!.Format.CodecId, Is.EqualTo("ac3"));
      Assert.That(stream.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(stream.Format.Channels, Is.EqualTo(2));
      Assert.That(stream.Packets, Has.Count.EqualTo(2));
      Assert.That(stream.Packets, Has.All.Matches<AudioPacket>(packet => packet.DurationSamples == 1536));
    });

    using var output = new MemoryStream();
    descriptor.Mux(output, stream!, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(encoded));
  }

  [Test]
  public void EnhancedDemuxMux_PreservesVariableBlockSyncframesByteExact() {
    var oneBlock = Eac3CodecTests.BuildSilenceFrame(numblkscod: 0);
    var sixBlocks = Eac3CodecTests.BuildSilenceFrame(numblkscod: 3);
    var encoded = oneBlock.Concat(sixBlocks).ToArray();
    var descriptor = new Ac3FormatDescriptor();

    using var input = new MemoryStream(encoded, writable: false);
    Assert.That(descriptor.TryDemux(input, out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(stream!.Format.CodecId, Is.EqualTo("eac3"));
      Assert.That(stream.Format.SampleRate, Is.EqualTo(48_000));
      Assert.That(stream.Format.Channels, Is.EqualTo(1));
      Assert.That(stream.Packets.Select(static packet => packet.DurationSamples), Is.EqualTo(new long[] { 256, 1536 }));
    });

    using var output = new MemoryStream();
    descriptor.Mux(output, stream!, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(encoded));
  }

  [Test]
  public void Demux_RejectsTruncatedFinalSyncframe() {
    var encoded = Ac3Codec.Encode(Signal(1536 * 2, 2, 48_000));
    Array.Resize(ref encoded, encoded.Length - 1);

    using var input = new MemoryStream(encoded, writable: false);
    Assert.That(new Ac3FormatDescriptor().TryDemux(input, out var stream), Is.False);
    Assert.That(stream, Is.Null);
  }

  [Test]
  public void Create_FromCanonicalChannelWavs_EncodesRequestedFivePointOneLayout() {
    const int sampleRate = 48_000;
    const int frames = 1536;
    var names = new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "SIDE_LEFT", "SIDE_RIGHT" };
    var inputs = names.Select((name, channel) => ArchiveInputInfo.InMemory(
      $"{name}.wav",
      PcmCodec.ToWavBlob(MonoPcm(frames, sampleRate, channel), 1, sampleRate, 16))).ToArray();
    var options = new FormatCreateOptions(Method: "ac3") {
      FormatSpecific = {
        ["acmod"] = "7",
        ["lfe"] = "true",
        ["bitrate"] = "448",
        ["dialnorm"] = "-27",
        ["cutoff"] = "18000",
        ["pad-final-frame"] = "false",
      },
    };

    using var output = new MemoryStream();
    var descriptor = new Ac3FormatDescriptor();
    descriptor.Create(output, inputs, options);

    var written = output.ToArray();
    output.Position = 0;
    var info = Ac3Codec.ReadStreamInfo(output);
    var header = Ac3FrameHeader.TryParse(written, 0);
    Assert.That(header, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(info.IsEnhanced, Is.False);
      Assert.That(info.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Bitrate, Is.EqualTo(448_000));
      Assert.That(info.Acmod, Is.EqualTo(7));
      Assert.That(info.Lfe, Is.True);
      Assert.That(info.Channels, Is.EqualTo(6));
      Assert.That(header!.Value.DialNorm, Is.EqualTo(27));
      Assert.That(info.DurationSamples, Is.EqualTo(frames));
    });
  }

  [Test]
  public void EncodeCapability_CoversEveryImplementedLegacyAcmodAndLfeCombination() {
    var descriptor = new Ac3FormatDescriptor();
    for (var acmod = 1; acmod <= 7; ++acmod) {
      for (var lfe = 0; lfe <= 1; ++lfe) {
        var channels = Ac3FrameHeader.AcmodChannelCount(acmod) + lfe;
        var options = new FormatCreateOptions(Method: "ac3") {
          FormatSpecific = {
            ["acmod"] = acmod.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["lfe"] = lfe == 0 ? "false" : "true",
            ["bitrate"] = "640",
          },
        };
        Assert.That(
          descriptor.CanEncode(new AudioPcmFormat(48_000, channels, 16), "ac3", options, out var reason),
          Is.True,
          $"acmod={acmod}, lfe={lfe}: {reason}");
      }
    }
  }

  [TestCase(32_000)]
  [TestCase(44_100)]
  [TestCase(48_000)]
  public void EncodeCapability_AcceptsEveryLegacySampleRate(int sampleRate) {
    var descriptor = new Ac3FormatDescriptor();
    foreach (var bitrate in new[] {
      32, 40, 48, 56, 64, 80, 96, 112, 128, 160,
      192, 224, 256, 320, 384, 448, 512, 576, 640,
    }) {
      var options = new FormatCreateOptions(Method: "ac3") {
        FormatSpecific = { ["bitrate"] = bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture) },
      };
      Assert.That(
        descriptor.CanEncode(new AudioPcmFormat(sampleRate, 2, 16), "ac3", options, out var reason),
        Is.True,
        $"{sampleRate} Hz, {bitrate} kbit/s: {reason}");
    }
  }

  [Test]
  public void EncodeCapability_EmitsRealEac3SyncframesInsteadOfRelabelingLegacyFrames() {
    var descriptor = new Ac3FormatDescriptor();
    var format = new AudioPcmFormat(48_000, 2, 16);
    Assert.That(descriptor.CanEncode(format, "eac3", new FormatCreateOptions(), out var reason), Is.True, reason);

    var pcm = Signal(1536, 2, 48_000);
    var payload = new byte[pcm.Length * sizeof(short)];
    Buffer.BlockCopy(pcm, 0, payload, 0, payload.Length);

    using var output = new MemoryStream();
    descriptor.EncodePcm(output, new AudioPcmBuffer(format, payload), "eac3", new FormatCreateOptions());

    // A relabelled legacy frame would carry bsid ≤ 10 and the AC-3 syncinfo layout.
    var header = Ac3FrameHeader.TryParse(output.ToArray(), 0);
    Assert.That(header, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(header!.Value.IsEnhanced, Is.True);
      Assert.That(header.Value.Bsid, Is.EqualTo(16));
      Assert.That(header.Value.SampleRate, Is.EqualTo(48_000));
      Assert.That(header.Value.Acmod, Is.EqualTo(2));
    });
  }

  [Test]
  public void ExtractedFivePointOneChannels_FollowCanonicalWaveInterleaveOrder() {
    var encoded = Ac3Codec.Encode(
      Signal(1536, 6, 48_000),
      new Ac3EncoderOptions(48_000, 448_000, Acmod: 7, LowFrequencyEffects: true));

    using var input = new MemoryStream(encoded, writable: false);
    var entries = new Ac3FormatDescriptor().List(input, null);
    var channels = entries
      .Where(static entry => entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .Select(static entry => Path.GetFileNameWithoutExtension(entry.Name))
      .ToArray();

    Assert.That(channels, Is.EqualTo(new[] {
      "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "SIDE_LEFT", "SIDE_RIGHT",
    }));
  }

  private static short[] Signal(int samplesPerChannel, int channels, int sampleRate) {
    var result = new short[samplesPerChannel * channels];
    for (var frame = 0; frame < samplesPerChannel; ++frame)
      for (var channel = 0; channel < channels; ++channel)
        result[frame * channels + channel] = (short)Math.Round(
          Math.Sin(2 * Math.PI * (180 + channel * 71) * frame / sampleRate) * 8_000);
    return result;
  }

  private static byte[] MonoPcm(int frames, int sampleRate, int channel) {
    var result = new byte[frames * sizeof(short)];
    for (var frame = 0; frame < frames; ++frame) {
      var sample = (short)Math.Round(
        Math.Sin(2 * Math.PI * (210 + channel * 83) * frame / sampleRate) * 7_000);
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(frame * 2, 2), sample);
    }
    return result;
  }
}
