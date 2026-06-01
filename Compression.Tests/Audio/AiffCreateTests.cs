using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Aiff;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the assemble direction for AIFF: building a multi-channel AIFF from
/// per-channel mono WAV inputs (<see cref="IArchiveCreatable"/>) and the 80-bit
/// extended-float sample-rate encoder it relies on.
/// </summary>
[TestFixture]
public class AiffCreateTests {

  private static byte[] MonoWav(short[] samples, int sampleRate = 44100) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  [Test]
  public void Encode80BitFloat_IsInverseOfDecode() {
    foreach (var rate in new[] { 8000, 11025, 22050, 44100, 48000, 96000, 192000 }) {
      var encoded = AiffWriter.Encode80BitFloat(rate);
      Assert.That(AiffReader.Decode80BitFloatToInt(encoded), Is.EqualTo(rate), $"rate {rate}");
    }
  }

  [Test, Category("RoundTrip")]
  public void Create_FromPerChannelWavs_ProducesStereoAiff_ThatRoundTrips() {
    var left = MonoWav([100, 200, 300, -400]);
    var right = MonoWav([-100, -200, -300, 400]);

    using var output = new MemoryStream();
    ((IArchiveCreatable)new AiffFormatDescriptor()).Create(output, [
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    ], new FormatCreateOptions());

    var blob = output.ToArray();
    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo("FORM"u8.ToArray()));
    Assert.That(blob.AsSpan(8, 4).ToArray(), Is.EqualTo("AIFF"u8.ToArray()));

    var parsed = new AiffReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.SampleFrames, Is.EqualTo(4));

    // Re-list through the descriptor and confirm the channels survive the trip.
    using var ms = new MemoryStream(blob);
    var entries = new AiffFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void Create_WithFullAif_PassesThroughVerbatim() {
    var left = MonoWav([1, 2, 3, 4]);
    var right = MonoWav([5, 6, 7, 8]);
    using var built = new MemoryStream();
    ((IArchiveCreatable)new AiffFormatDescriptor()).Create(built, [
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    ], new FormatCreateOptions());
    var original = built.ToArray();

    using var output = new MemoryStream();
    ((IArchiveCreatable)new AiffFormatDescriptor()).Create(output,
      [ArchiveInputInfo.InMemory("FULL.aif", original)], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
