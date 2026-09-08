#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

[TestFixture]
public sealed class AsfMuxTests {
  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] HeaderExtensionObject =
    [0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11, 0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

  [Test]
  public void Mux_FragmentedWmaObjects_RoundTripCodecBytesAndPresentationTimes() {
    var first = Enumerable.Range(0, 180).Select(static i => (byte)i).ToArray();
    var second = Enumerable.Range(0, 20).Select(static i => (byte)(0xE0 + i)).ToArray();
    var codecPrivate = new byte[] { 0x10, 0x00, 0x00, 0x00, 0x1F, 0x00 };
    var encoded = BuildWmav2(first, second, codecPrivate);
    var options = new FormatCreateOptions();
    options.FormatSpecific["packet-size"] = "100";

    using var output = new MemoryStream();
    AsfAudioAdapter.Instance.Mux(output, encoded, options);
    var asf = output.ToArray();

    Assert.That(asf.AsSpan(0, 16).ToArray(), Is.EqualTo(HeaderObject));
    Assert.That(asf.AsSpan().IndexOf(HeaderExtensionObject), Is.GreaterThanOrEqualTo(0));

    using var metadata = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), "metadata.ini", metadata, null);
    var metadataText = Encoding.UTF8.GetString(metadata.ToArray());
    Assert.That(metadataText, Does.Contain("data_packets = 4"));
    Assert.That(metadataText, Does.Contain("min_packet_size = 100"));
    Assert.That(metadataText, Does.Contain("max_packet_size = 100"));

    Assert.That(AsfAudioAdapter.Instance.TryDemux(new MemoryStream(asf), out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(demuxed!.Format.CodecId, Is.EqualTo("wmav2"));
      Assert.That(demuxed.Format.SampleRate, Is.EqualTo(44_100));
      Assert.That(demuxed.Format.Channels, Is.EqualTo(2));
      Assert.That(demuxed.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(demuxed.CodecPrivateData, Is.EqualTo(codecPrivate));
      Assert.That(demuxed.Packets, Has.Count.EqualTo(2));
      Assert.That(demuxed.Packets[0].Data, Is.EqualTo(first));
      Assert.That(demuxed.Packets[1].Data, Is.EqualTo(second));
      Assert.That(demuxed.Packets[0].GranulePosition, Is.EqualTo(0));
      Assert.That(demuxed.Packets[1].GranulePosition, Is.EqualTo(4_410));
      Assert.That(demuxed.Packets[0].DurationSamples, Is.EqualTo(4_410));
      Assert.That(demuxed.Packets[1].DurationSamples, Is.EqualTo(4_410));
    });
  }

  [Test]
  public void AudioConversionOperation_ForcedAsfToAsf_RemuxesWithoutReencoding() {
    var first = Enumerable.Range(0, 180).Select(static i => (byte)(i * 13)).ToArray();
    var second = Enumerable.Range(0, 20).Select(static i => (byte)(0xA0 + i)).ToArray();
    var codecPrivate = new byte[] { 1, 2, 3, 4, 5, 6 };
    var encoded = BuildWmav2(first, second, codecPrivate);
    var muxOptions = new FormatCreateOptions();
    muxOptions.FormatSpecific["packet-size"] = "100";

    using var source = new MemoryStream();
    AsfAudioAdapter.Instance.Mux(source, encoded, muxOptions);

    source.Position = 0;
    using var remuxed = new MemoryStream();
    var remuxOptions = new FormatCreateOptions(Method: "wmav2");
    remuxOptions.FormatSpecific["packet-size"] = "128";
    AudioConversionOperation.Convert(
      source,
      new AsfFormatDescriptor(),
      remuxed,
      new AsfFormatDescriptor(),
      remuxOptions);

    remuxed.Position = 0;
    Assert.That(AsfAudioAdapter.Instance.TryDemux(remuxed, out var decoded), Is.True);
    Assert.That(decoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(decoded!.Packets, Has.Count.EqualTo(2));
      Assert.That(decoded.Packets[0].Data, Is.EqualTo(first));
      Assert.That(decoded.Packets[1].Data, Is.EqualTo(second));
      Assert.That(decoded.CodecPrivateData, Is.EqualTo(codecPrivate));
      Assert.That(decoded.Packets[1].GranulePosition, Is.EqualTo(4_410));
    });
  }

  [Test]
  public void CanMux_UnknownCodecRequiresExplicitWaveFormatTag() {
    var unknown = new AudioStreamFormat("private-codec", 48_000, 2, 16);
    Assert.That(AsfAudioAdapter.Instance.CanMux(unknown, new FormatCreateOptions(), out var reason), Is.False);
    Assert.That(reason, Does.Contain("format tag"));

    var tagged = unknown with {
      Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["format-tag"] = "0x1234",
      },
    };
    Assert.That(AsfAudioAdapter.Instance.CanMux(tagged, new FormatCreateOptions(), out reason), Is.True);
    Assert.That(reason, Is.Null);
  }

  [Test]
  public void Mux_RejectsEmptyMediaObject() {
    var format = new AudioStreamFormat(
      "wmav2",
      44_100,
      2,
      16,
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["format-tag"] = "0x0161",
        ["byte-rate"] = "16000",
        ["block-align"] = "1",
      });
    var encoded = new AudioEncodedStream(format, [new AudioPacket([])]);

    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => AsfAudioAdapter.Instance.Mux(output, encoded, new FormatCreateOptions()));
  }

  [Test]
  public void TryDemux_TruncatedInput_ReturnsFalse() {
    Assert.That(AsfAudioAdapter.Instance.TryDemux(new MemoryStream(HeaderObject[..12]), out var stream), Is.False);
    Assert.That(stream, Is.Null);
  }

  private static AudioEncodedStream BuildWmav2(byte[] first, byte[] second, byte[] codecPrivate) {
    var format = new AudioStreamFormat(
      "wmav2",
      44_100,
      2,
      16,
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["format-tag"] = "0x0161",
        ["byte-rate"] = "16000",
        ["block-align"] = "180",
      });

    return new AudioEncodedStream(
      format,
      [
        new AudioPacket(first, DurationSamples: 4_410, GranulePosition: 0),
        new AudioPacket(second, DurationSamples: 4_410, GranulePosition: 4_410),
      ],
      codecPrivate);
  }
}
