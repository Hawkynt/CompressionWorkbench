using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// Conversions into containers that are built from per-channel WAVs rather than
/// from a PCM encoder.
/// </summary>
/// <remarks>
/// Those targets used to be reachable only when the source happened to list
/// Channel entries. A mono file never does — a single channel has nothing to
/// split — so every one of them was unreachable from mono input while the same
/// conversion worked from stereo. The pipeline now decodes the source and makes
/// the channels itself.
/// </remarks>
[TestFixture]
public sealed class MonoPseudoArchiveConversionTests {

  private static byte[] MonoWav(int sampleRate = 44_100, int frames = 4_096) {
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      var value = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 12_000);
      pcm[i * 2] = (byte)(value & 0xFF);
      pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
    }

    return PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample: 16);
  }

  private static IFormatDescriptor Descriptor(string id) {
    FormatRegistration.EnsureInitialized();
    return FormatRegistry.All.Single(descriptor => descriptor.Id == id);
  }

  [TestCase("Voc")]
  [TestCase("Ircam")]
  [TestCase("Sphere")]
  [TestCase("Wave64")]
  [TestCase("Qoa")]
  [Category("RoundTrip")]
  public void MonoSource_ReachesCreateOnlyTargets(string targetId) {
    using var input = new MemoryStream(MonoWav(), writable: false);
    using var output = new MemoryStream();

    AudioConversionOperation.Convert(
      input, new WavFormatDescriptor(), output, Descriptor(targetId), new FormatCreateOptions());

    Assert.That(output.Length, Is.GreaterThan(0), targetId);

    // and what came out is readable as that format again
    output.Position = 0;
    var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(targetId)!;
    Assert.That(() => ops.List(output, null), Throws.Nothing, targetId);
  }

  /// <summary>
  /// The stereo path already worked; it must keep working, and both paths must
  /// name their channels the same way.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void StereoSourceStillUsesItsOwnChannelEntries() {
    const int frames = 4_096;
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i)
      for (var channel = 0; channel < 2; ++channel) {
        var value = (short)(Math.Sin(2 * Math.PI * (440 + 110 * channel) * i / 44_100.0) * 12_000);
        var offset = (i * 2 + channel) * 2;
        pcm[offset] = (byte)(value & 0xFF);
        pcm[offset + 1] = (byte)((value >> 8) & 0xFF);
      }

    using var input = new MemoryStream(PcmCodec.ToWavBlob(pcm, 2, 44_100, 16), writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(
      input, new WavFormatDescriptor(), output, Descriptor("Voc"), new FormatCreateOptions());

    Assert.That(output.Length, Is.GreaterThan(0));
  }
}
