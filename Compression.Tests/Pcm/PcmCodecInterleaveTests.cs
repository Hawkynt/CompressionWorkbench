using System.Buffers.Binary;
using Codec.Pcm;

namespace Compression.Tests.Pcm;

/// <summary>
/// Pins <see cref="PcmCodec.Interleave"/> — the inverse of
/// <see cref="PcmCodec.SplitInterleavedPcm"/> — which lets audio-container
/// descriptors assemble a multi-channel file from per-channel mono inputs.
/// </summary>
[TestFixture]
public class PcmCodecInterleaveTests {

  [Test, Category("RoundTrip")]
  public void Interleave_TwoChannels16Bit_WeavesFramesInChannelOrder() {
    // left = [1, 2, 3], right = [-1, -2, -3] as 16-bit LE mono PCM.
    var left = new byte[6];
    var right = new byte[6];
    for (var i = 0; i < 3; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(i + 1));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)-(i + 1));
    }

    var interleaved = PcmCodec.Interleave([left, right], bitsPerSample: 16);

    Assert.That(interleaved.Length, Is.EqualTo(12));
    // Frame f = (left[f], right[f]).
    for (var f = 0; f < 3; ++f) {
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(interleaved.AsSpan(f * 4)), Is.EqualTo((short)(f + 1)));
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(interleaved.AsSpan(f * 4 + 2)), Is.EqualTo((short)-(f + 1)));
    }
  }

  [Test, Category("RoundTrip")]
  public void Interleave_IsInverseOfSplit_For6Channel24Bit() {
    // Build a known interleaved 6-channel 24-bit buffer, split it to mono WAVs,
    // strip the 44-byte headers, re-interleave, and demand byte-exact equality.
    const int channels = 6, bytesPerSample = 3, frames = 17;
    var original = new byte[frames * channels * bytesPerSample];
    for (var i = 0; i < original.Length; ++i) original[i] = (byte)((i * 7 + 3) & 0xFF);

    var monoWavs = PcmCodec.SplitInterleavedPcm(original, channels, sampleRate: 48000, bitsPerSample: 24);
    var monoPcm = monoWavs.Select(w => w.WavBlob[44..]).ToArray();

    var reInterleaved = PcmCodec.Interleave(monoPcm, bitsPerSample: 24);

    Assert.That(reInterleaved, Is.EqualTo(original));
  }

  [Test]
  public void Interleave_RejectsUnequalChannelLengths() {
    var ok = new byte[8];
    var shortChannel = new byte[6];
    Assert.That(() => PcmCodec.Interleave([ok, shortChannel], bitsPerSample: 16),
      Throws.ArgumentException);
  }

  [Test]
  public void Interleave_SingleChannel_ReturnsInputUnchanged() {
    var mono = new byte[] { 1, 2, 3, 4 };
    var result = PcmCodec.Interleave([mono], bitsPerSample: 16);
    Assert.That(result, Is.EqualTo(mono));
  }
}
