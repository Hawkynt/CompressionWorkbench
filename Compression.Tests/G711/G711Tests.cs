#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.G711;

namespace Compression.Tests.G711;

[TestFixture]
public class G711Tests {

  // Known μ-law / A-law bytes; expectations come straight from the G.711 codecs
  // (deterministic, lossless decode) so the container can't drift from the codec.
  private static readonly byte[] UlawBytes = [0x00, 0x7F, 0x80, 0xFF, 0x55, 0xAA];
  private static readonly byte[] AlawBytes = [0x00, 0x7F, 0x80, 0xFF, 0x55, 0xD5];

  [Test]
  public void Ulaw_Mono_DecodesToExpectedSamples() {
    using var ms = new MemoryStream(UlawBytes);
    var entries = new G711UlawFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ul" && e.Kind == "Container"), Is.True);
    var mono = entries.First(e => e.Name == "MONO.wav");
    Assert.That(mono.Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);

    var pcm = ExtractMonoPcm(new G711UlawFormatDescriptor(), UlawBytes, ".ul");
    for (var i = 0; i < UlawBytes.Length; ++i) {
      var expected = Codec.MuLaw.MuLawCodec.DecodeSample(UlawBytes[i]);
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(expected));
    }
  }

  [Test]
  public void Alaw_Mono_DecodesToExpectedSamples() {
    using var ms = new MemoryStream(AlawBytes);
    var entries = new G711AlawFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.al" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);

    var pcm = ExtractMonoPcm(new G711AlawFormatDescriptor(), AlawBytes, ".al");
    for (var i = 0; i < AlawBytes.Length; ++i) {
      var expected = Codec.ALaw.ALawCodec.DecodeSample(AlawBytes[i]);
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(expected));
    }
  }

  [Test]
  public void Mono_Wav_IsValidRiffAt8000HzMono() {
    var pcm = ExtractMonoPcmBlob(new G711UlawFormatDescriptor(), UlawBytes, "MONO.wav");
    Assert.That(pcm.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(pcm.AsSpan(8, 4).ToArray(), Is.EqualTo("WAVE"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(pcm.AsSpan(20)), Is.EqualTo(1));     // PCM
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(pcm.AsSpan(22)), Is.EqualTo(1));     // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(pcm.AsSpan(24)), Is.EqualTo(8000u)); // 8 kHz
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(pcm.AsSpan(34)), Is.EqualTo(16));    // 16-bit
  }

  [Test]
  public void Ulaw_Create_FullPassthrough_RoundTrips() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("FULL.ul", UlawBytes),
    };
    using var output = new MemoryStream();
    new G711UlawFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(UlawBytes));
  }

  [Test]
  public void Ulaw_Create_FromMonoWav_RoundTripsLosslessly() {
    // Decode known bytes → 16-bit WAV → re-encode. G.711 companding is lossless on
    // its own quantisation grid: decode∘encode reproduces the same linear samples, so
    // the re-decoded stream is bit-identical to the original decode (encode is the
    // exact inverse here because the PCM came from a prior decode).
    var pcm = ExtractMonoPcm(new G711UlawFormatDescriptor(), UlawBytes, ".ul");
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 8000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var output = new MemoryStream();
    new G711UlawFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var encoded = output.ToArray();

    Assert.That(encoded.Length, Is.EqualTo(UlawBytes.Length));
    var reDecoded = Codec.MuLaw.MuLawCodec.Decode(encoded);
    var original = Codec.MuLaw.MuLawCodec.Decode(UlawBytes);
    Assert.That(reDecoded, Is.EqualTo(original));
  }

  [Test]
  public void Alaw_Create_FromMonoWav_RoundTripsLosslessly() {
    var pcm = ExtractMonoPcm(new G711AlawFormatDescriptor(), AlawBytes, ".al");
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 8000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var output = new MemoryStream();
    new G711AlawFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var encoded = output.ToArray();

    Assert.That(encoded.Length, Is.EqualTo(AlawBytes.Length));
    var reDecoded = Codec.ALaw.ALawCodec.Decode(encoded);
    var original = Codec.ALaw.ALawCodec.Decode(AlawBytes);
    Assert.That(reDecoded, Is.EqualTo(original));
  }

  private static byte[] ExtractMonoPcmBlob(G711FormatDescriptorBase d, byte[] raw, string entry) {
    using var input = new MemoryStream(raw);
    using var output = new MemoryStream();
    d.ExtractEntry(input, entry, output, null);
    return output.ToArray();
  }

  // Returns the raw 16-bit LE PCM payload (data chunk body) of MONO.wav.
  private static byte[] ExtractMonoPcm(G711FormatDescriptorBase d, byte[] raw, string _) {
    var blob = ExtractMonoPcmBlob(d, raw, "MONO.wav");
    return blob.AsSpan(44).ToArray();
  }
}
