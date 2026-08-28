#pragma warning disable CS1591
using Codec.ImaAdpcm;
using Codec.MsAdpcm;

namespace Compression.Tests.Audio;

[TestFixture]
public class AdpcmEncodeTests {

  [Test]
  public void ImaAdpcm_EncodeDecode_MonoSine_StaysWithinTolerance() {
    const int blockAlign = 256;
    const int samplesPerBlock = 505;
    var pcm = new short[samplesPerBlock * 6];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 53) * 12000);

    var encoded = ImaAdpcmCodec.Encode([pcm], blockAlign);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign, channels: 1)[0];

    Assert.That(encoded.Length, Is.EqualTo(blockAlign * 6));
    Assert.That(MaxError(pcm, decoded), Is.LessThan(2500));
    Assert.That(ImaAdpcmCodec.Encode([pcm], blockAlign), Is.EqualTo(encoded), "encoder must be deterministic");
  }

  [Test]
  public void ImaAdpcm_EncodeDecode_Stereo_UsesMicrosoftGroupLayout() {
    const int blockAlign = 512;
    const int samplesPerBlock = 505;
    var left = new short[samplesPerBlock * 4];
    var right = new short[left.Length];
    for (var i = 0; i < left.Length; ++i) {
      left[i] = (short)(Math.Sin(i * 2 * Math.PI / 47) * 10000);
      right[i] = (short)(Math.Cos(i * 2 * Math.PI / 61) * 9000);
    }

    var encoded = ImaAdpcmCodec.Encode([left, right], blockAlign);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign, channels: 2);

    Assert.That(MaxError(left, decoded[0]), Is.LessThan(2500));
    Assert.That(MaxError(right, decoded[1]), Is.LessThan(2500));
  }

  [Test]
  public void ImaAdpcm_EncodeQuickTime_RoundTripsPacketsAndChannels() {
    const int samplesPerPacket = 64;
    var left = new short[samplesPerPacket * 5];
    var right = new short[left.Length];
    for (var i = 0; i < left.Length; ++i) {
      left[i] = (short)(Math.Sin(i * 2 * Math.PI / 37) * 7000);
      right[i] = (short)(Math.Cos(i * 2 * Math.PI / 43) * 6000);
    }

    var encoded = ImaAdpcmCodec.EncodeQuickTime([left, right]);
    var decoded = ImaAdpcmCodec.DecodeQuickTime(encoded, channels: 2);

    Assert.That(encoded.Length, Is.EqualTo(34 * 2 * 5));
    Assert.That(MaxError(left, decoded[0]), Is.LessThan(3000));
    Assert.That(MaxError(right, decoded[1]), Is.LessThan(3000));
  }

  [Test]
  public void ImaAdpcm_Encode_PadsOnlyTheTerminalBlock() {
    const int blockAlign = 256;
    var pcm = new short[506];
    for (var i = 0; i < pcm.Length; ++i) pcm[i] = (short)(i * 10 - 2000);

    var encoded = ImaAdpcmCodec.Encode([pcm], blockAlign);
    var decoded = ImaAdpcmCodec.Decode(encoded, blockAlign, channels: 1)[0];

    Assert.That(encoded.Length, Is.EqualTo(blockAlign * 2));
    Assert.That(decoded.Length, Is.EqualTo(505 * 2));
    Assert.That(MaxError(pcm, decoded), Is.LessThan(2500));
  }

  [Test]
  public void MsAdpcm_EncodeDecode_MonoSine_StaysWithinTolerance() {
    const int blockAlign = 256;
    const int samplesPerBlock = 500;
    var pcm = new short[samplesPerBlock * 5];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 59) * 11000);

    var encoded = MsAdpcmCodec.Encode([pcm], blockAlign);
    var decoded = MsAdpcmCodec.Decode(encoded, blockAlign, channels: 1)[0];

    Assert.That(encoded.Length, Is.EqualTo(blockAlign * 5));
    Assert.That(decoded[0], Is.EqualTo(pcm[0]));
    Assert.That(decoded[1], Is.EqualTo(pcm[1]));
    Assert.That(MaxError(pcm, decoded), Is.LessThan(3500));
    Assert.That(MsAdpcmCodec.Encode([pcm], blockAlign), Is.EqualTo(encoded), "encoder must be deterministic");
  }

  [Test]
  public void MsAdpcm_EncodeDecode_Stereo_InterleavesNibbles() {
    const int blockAlign = 512;
    const int samplesPerBlock = 500;
    var left = new short[samplesPerBlock * 4];
    var right = new short[left.Length];
    for (var i = 0; i < left.Length; ++i) {
      left[i] = (short)(Math.Sin(i * 2 * Math.PI / 41) * 9000);
      right[i] = (short)(Math.Cos(i * 2 * Math.PI / 67) * 8000);
    }

    var encoded = MsAdpcmCodec.Encode([left, right], blockAlign);
    var decoded = MsAdpcmCodec.Decode(encoded, blockAlign, channels: 2);

    Assert.That(decoded[0][0], Is.EqualTo(left[0]));
    Assert.That(decoded[0][1], Is.EqualTo(left[1]));
    Assert.That(decoded[1][0], Is.EqualTo(right[0]));
    Assert.That(decoded[1][1], Is.EqualTo(right[1]));
    Assert.That(MaxError(left, decoded[0]), Is.LessThan(3500));
    Assert.That(MaxError(right, decoded[1]), Is.LessThan(3500));
  }

  [Test]
  public void AdpcmEncoders_EmptyInput_ReturnsEmpty() {
    Assert.That(ImaAdpcmCodec.Encode([Array.Empty<short>()], 256), Is.Empty);
    Assert.That(ImaAdpcmCodec.EncodeQuickTime([Array.Empty<short>()]), Is.Empty);
    Assert.That(MsAdpcmCodec.Encode([Array.Empty<short>()], 256), Is.Empty);
  }

  private static int MaxError(ReadOnlySpan<short> expected, ReadOnlySpan<short> actual) {
    Assert.That(actual.Length, Is.GreaterThanOrEqualTo(expected.Length));
    var result = 0;
    for (var i = 0; i < expected.Length; ++i)
      result = Math.Max(result, Math.Abs(actual[i] - expected[i]));
    return result;
  }
}
