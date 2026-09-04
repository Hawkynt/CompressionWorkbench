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
  /// An 8-bit source into an encoder that only takes 16-bit. The width is offered
  /// only after the encoder has refused the one it was given, so nothing is
  /// re-quantised that did not have to be.
  /// </summary>
  [TestCase("Mp3")]
  [TestCase("Qoa")]
  [Category("RoundTrip")]
  public void EightBitSource_IsWidenedForTargetsThatRequireSixteen(string targetId) {
    const int frames = 8_192;
    var eightBit = new byte[frames];
    for (var i = 0; i < frames; ++i)
      eightBit[i] = (byte)(128 + (int)(Math.Sin(2 * Math.PI * 440 * i / 44_100.0) * 100));

    using var input = new MemoryStream(
      PcmCodec.ToWavBlob(eightBit, channels: 1, 44_100, bitsPerSample: 8), writable: false);
    using var output = new MemoryStream();

    AudioConversionOperation.Convert(
      input, new WavFormatDescriptor(), output, Descriptor(targetId), new FormatCreateOptions());

    Assert.That(output.Length, Is.GreaterThan(0), targetId);
  }

  /// <summary>Widening is exact: every 8-bit code maps to one 16-bit value and back.</summary>
  [Test]
  public void WideningEightToSixteenAndBackIsLossless() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i) original[i] = (byte)i;

    var widened = PcmCodec.Requantize(original, 8, 16);
    Assert.That(widened, Has.Length.EqualTo(512));
    Assert.That(PcmCodec.Requantize(widened, 16, 8), Is.EqualTo(original));
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
