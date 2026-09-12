#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Atrac1;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aea;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AeaWriteRemuxTests {

  private const int HeaderSize = 2048;
  private const int SoundUnitSize = Atrac1Codec.SoundUnitSize;

  [TestCase(1)]
  [TestCase(2)]
  public void DemuxThenMux_ReproducesCanonicalAeaByteForByte(int channels) {
    var input = BuildAea(channels, 3, "Remux title", seed: 0x31);
    var descriptor = new AeaFormatDescriptor();
    Assert.That(descriptor.TryDemux(new MemoryStream(input, writable: false), out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);

    using var output = new MemoryStream();
    descriptor.Mux(output, stream!, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(input));
  }

  [Test]
  public void Mux_WritesFfmpegCompatibleHeaderAndCountsSoundUnits() {
    const int channels = 2;
    var frameSize = channels * SoundUnitSize;
    var packet = new byte[frameSize * 2];
    for (var i = 0; i < packet.Length; ++i)
      packet[i] = (byte)(i * 13 + 7);

    var stream = new AudioEncodedStream(
      new AudioStreamFormat("atrac1", 44100, channels, Properties: new Dictionary<string, string> {
        ["title"] = "Container title",
      }),
      [new AudioPacket(packet, 2 * Atrac1Codec.SamplesPerFrame)]);

    using var output = new MemoryStream();
    new AeaFormatDescriptor().Mux(output, stream, new FormatCreateOptions());
    var aea = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(aea), Is.EqualTo(0x800u));
      Assert.That(Encoding.Latin1.GetString(aea, 4, "Container title".Length), Is.EqualTo("Container title"));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(aea.AsSpan(260, 4)), Is.EqualTo(4u),
        "the header field counts per-channel ATRAC1 sound units");
      Assert.That(aea[264], Is.EqualTo(2));
      Assert.That(aea.Length, Is.EqualTo(HeaderSize + packet.Length));
      Assert.That(aea.AsSpan(HeaderSize).ToArray(), Is.EqualTo(packet));
    });
  }

  [TestCase("aac", 44100, 2, "ATRAC1")]
  [TestCase("atrac1", 48000, 2, "44100")]
  [TestCase("atrac1", 44100, 3, "mono or stereo")]
  public void CanMux_ExplainsUnsupportedProfiles(string codec, int sampleRate, int channels, string expected) {
    var allowed = new AeaFormatDescriptor().CanMux(
      new AudioStreamFormat(codec, sampleRate, channels), new FormatCreateOptions(), out var reason);

    Assert.Multiple(() => {
      Assert.That(allowed, Is.False);
      Assert.That(reason, Does.Contain(expected));
    });
  }

  [Test]
  public void Demux_AcceptsExtendedReadOnlyChannelCountsButMuxDoesNotClaimThem() {
    var input = BuildAea(8, 1, string.Empty, seed: 0);
    var descriptor = new AeaFormatDescriptor();

    Assert.That(descriptor.TryDemux(new MemoryStream(input, writable: false), out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(stream!.Format.Channels, Is.EqualTo(8));
      Assert.That(stream.Packets, Has.Count.EqualTo(1));
      Assert.That(descriptor.CanMux(stream.Format, new FormatCreateOptions(), out _), Is.False);
      Assert.That(AeaFormatDescriptor.LooksLikeAea(input), Is.False,
        "extension-only read compatibility must not broaden weak magic-based AEA detection beyond the interoperable profile");
    });
  }

  [TestCase(1)]
  [TestCase(2)]
  public void EncodeAndDecodePcm_RoundTripsTheContainerGeometry(int channels) {
    var descriptor = new AeaFormatDescriptor();
    var pcmBytes = new byte[Atrac1Codec.SamplesPerFrame * channels * 2];
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(44100, channels, 16), pcmBytes);

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, pcm, "atrac1", new FormatCreateOptions {
      FormatSpecific = { ["title"] = "Encoded" },
    });

    var aea = encoded.ToArray();
    using var source = new MemoryStream(aea, writable: false);
    var decoded = descriptor.DecodePcm(source);

    Assert.Multiple(() => {
      Assert.That(AeaFormatDescriptor.LooksLikeAea(aea), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(aea.AsSpan(260, 4)), Is.EqualTo((uint)channels));
      Assert.That(decoded.Format, Is.EqualTo(new AudioPcmFormat(44100, channels, 16)));
      Assert.That(decoded.InterleavedData.Length, Is.EqualTo(pcmBytes.Length));
      Assert.That(decoded.InterleavedData.All(static value => value == 0), Is.True);
    });
  }

  [Test]
  public void Create_AcceptsMonoWavAndProducesAea() {
    var wav = PcmCodec.ToWavBlob(new byte[Atrac1Codec.SamplesPerFrame * 2], 1, 44100, 16, formatCode: 1);
    using var output = new MemoryStream();

    new AeaFormatDescriptor().Create(
      output,
      [ArchiveInputInfo.InMemory("MONO.wav", wav)],
      new FormatCreateOptions(Method: "atrac1"));

    Assert.That(AeaFormatDescriptor.LooksLikeAea(output.ToArray()), Is.True);
  }

  [Test]
  public void EncoderOptions_CoverEveryRepresentableWindowMaskAndBfuSelector() {
    var descriptor = new AeaFormatDescriptor();
    foreach (var mask in Enumerable.Range(0, 8))
      foreach (var bfuCount in new[] { 20, 28, 32, 36, 40, 44, 48, 52 }) {
        var options = new FormatCreateOptions {
          FormatSpecific = {
            ["window-mask"] = mask.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["bfu-count"] = bfuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
          },
        };
        Assert.That(descriptor.CanEncode(new AudioPcmFormat(44100, 2, 16), "atrac1", options, out var reason),
          Is.True, $"mask {mask}, BFUs {bfuCount}: {reason}");
      }
  }

  [Test]
  public void Inventory_ReportsDecodeEncodeDemuxAndMux() {
    var capability = AudioConversionInventory.Describe(new AeaFormatDescriptor());

    Assert.Multiple(() => {
      Assert.That(capability.CanDecodePcm, Is.True);
      Assert.That(capability.CanEncodePcm, Is.True);
      Assert.That(capability.CanDemuxEncoded, Is.True);
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.EncodeCodecs, Does.Contain("atrac1"));
      Assert.That(capability.MuxCodecs, Does.Contain("atrac1"));
    });
  }

  private static byte[] BuildAea(int channels, int frames, string title, byte seed) {
    var frameSize = channels * SoundUnitSize;
    var result = new byte[HeaderSize + frameSize * frames];
    BinaryPrimitives.WriteUInt32LittleEndian(result, 0x800u);
    var titleBytes = Encoding.Latin1.GetBytes(title);
    titleBytes.AsSpan(0, Math.Min(titleBytes.Length, 256)).CopyTo(result.AsSpan(4, 256));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(260, 4), checked((uint)(frames * channels)));
    result[264] = checked((byte)channels);
    for (var i = HeaderSize; i < result.Length; ++i)
      result[i] = (byte)(seed == 0 ? 0 : seed + i);
    return result;
  }
}
