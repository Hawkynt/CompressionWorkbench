#pragma warning disable CS1591
using Codec.Aac;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aac;

namespace Compression.Tests.Audio;

/// <summary>
/// ADTS remux: the AAC descriptor could take an access-unit stream apart but had nowhere to put
/// one back, so AAC was the only audio format in the registry that could demux and not mux. These
/// tests hold the two halves to being each other's inverse.
/// </summary>
[TestFixture]
public sealed class AacAdtsRemuxTests {

  private const int SampleRateIndex = 4;   // 44 100 Hz
  private const int Profile = 1;           // AAC-LC (object type 2)

  private static byte[] AdtsFrame(int payloadLength, byte seed, int channels = 2, bool mpeg2 = false) {
    var payload = new byte[payloadLength];
    for (var i = 0; i < payload.Length; ++i)
      payload[i] = (byte)(seed + i);
    var header = AacAdtsReader.BuildHeader(
      Profile, SampleRateIndex, channels,
      AacAdtsReader.ShortHeaderLength + payloadLength, mpeg2: mpeg2);
    return [.. header, .. payload];
  }

  [Test]
  public void DemuxThenMux_ReproducesTheStreamByteForByte() {
    byte[] input = [.. AdtsFrame(64, 0x10), .. AdtsFrame(97, 0x40), .. AdtsFrame(23, 0x90)];

    using var source = new MemoryStream(input, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      source, new AacFormatDescriptor(), output, new AacFormatDescriptor(),
      new FormatCreateOptions(Method: "aac"));

    Assert.That(output.ToArray(), Is.EqualTo(input));
  }

  [Test]
  public void Mux_KeepsTheMpeg2IdBit() {
    byte[] input = [.. AdtsFrame(48, 0x21, mpeg2: true), .. AdtsFrame(48, 0x55, mpeg2: true)];

    using var source = new MemoryStream(input, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      source, new AacFormatDescriptor(), output, new AacFormatDescriptor(),
      new FormatCreateOptions(Method: "aac"));

    Assert.That(output.ToArray(), Is.EqualTo(input));
  }

  [Test]
  public void Descriptor_ReportsMuxCapability() {
    var descriptor = new AacFormatDescriptor();
    var capability = AudioConversionInventory.Describe(descriptor);

    Assert.Multiple(() => {
      Assert.That(capability.CanDemuxEncoded, Is.True);
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.MuxCodecs, Does.Contain("aac"));
    });
  }

  [TestCase("mp3", "not codec")]
  [TestCase("aac", "sample-rate index")]
  public void CanMux_ExplainsWhatItRefuses(string codec, string expected) {
    var descriptor = new AacFormatDescriptor();
    var rate = codec == "aac" ? 44_101 : 44_100;

    var allowed = descriptor.CanMux(
      new AudioStreamFormat(codec, rate, 2), new FormatCreateOptions(), out var reason);

    Assert.Multiple(() => {
      Assert.That(allowed, Is.False);
      Assert.That(reason, Does.Contain(expected));
    });
  }

  [Test]
  public void Mux_RejectsAnAccessUnitTooLargeForTheFrameLengthField() {
    var descriptor = new AacFormatDescriptor();
    var stream = new AudioEncodedStream(
      new AudioStreamFormat("aac", 44_100, 2),
      [new AudioPacket(new byte[0x2000], 1024)]);

    using var output = new MemoryStream();
    var exception = Assert.Throws<InvalidDataException>(() =>
      descriptor.Mux(output, stream, new FormatCreateOptions()));

    Assert.That(exception!.Message, Does.Contain("13-bit ADTS frame length"));
  }
}
