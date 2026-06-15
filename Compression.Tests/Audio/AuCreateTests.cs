using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Au;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the assemble direction for Sun/NeXT .au: building a multi-channel .au
/// from per-channel mono WAV inputs (<see cref="IArchiveCreatable"/>).
/// </summary>
[TestFixture]
public class AuCreateTests {

  private static byte[] MonoWav(short[] samples, int sampleRate = 22050) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  [Test, Category("RoundTrip")]
  public void Create_FromPerChannelWavs_ProducesStereoAu_ThatRoundTrips() {
    var left = MonoWav([10, 20, 30, -40]);
    var right = MonoWav([-10, -20, -30, 40]);

    using var output = new MemoryStream();
    ((IArchiveCreatable)new AuFormatDescriptor()).Create(output, [
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    ], new FormatCreateOptions());

    var blob = output.ToArray();
    Assert.That(blob.AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0x2E, 0x73, 0x6E, 0x64 })); // .snd

    var parsed = new AuReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(22050));
    Assert.That(parsed.Encoding, Is.EqualTo(3u)); // 16-bit BE linear PCM

    using var ms = new MemoryStream(blob);
    var entries = new AuFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void Create_WithFullAu_PassesThroughVerbatim() {
    using var built = new MemoryStream();
    ((IArchiveCreatable)new AuFormatDescriptor()).Create(built, [
      ArchiveInputInfo.InMemory("MONO.wav", MonoWav([1, 2, 3, 4])),
    ], new FormatCreateOptions());
    var original = built.ToArray();

    using var output = new MemoryStream();
    ((IArchiveCreatable)new AuFormatDescriptor()).Create(output,
      [ArchiveInputInfo.InMemory("FULL.au", original)], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
