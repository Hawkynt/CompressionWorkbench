#pragma warning disable CS1591
using Codec.S302M;

namespace Compression.Tests.Codecs.S302M;

/// <summary>
/// Pins the SMPTE 302M (AES3-over-MPEG-TS) PCM decoder. The reference packs samples in bit-reversed
/// AES3 subframe order; correctness is verified by encoding known PCM into an AES3 payload by hand
/// (the codec's <see cref="S302MCodec.Encode"/>, the exact inverse of the decoder) and decoding it
/// back byte/sample-exact for 16-, 20- and 24-bit depths, plus the header parsing and bit-reversal
/// table.
/// </summary>
[TestFixture]
public class S302MCodecTests {

  [Test]
  public void Reverse_IsByteBitReversalAndInvolution() {
    Assert.That(S302MCodec.Reverse[0x01], Is.EqualTo(0x80));
    Assert.That(S302MCodec.Reverse[0x80], Is.EqualTo(0x01));
    Assert.That(S302MCodec.Reverse[0x0F], Is.EqualTo(0xF0));
    Assert.That(S302MCodec.Reverse[0xAA], Is.EqualTo(0x55));
    for (var i = 0; i < 256; ++i)
      Assert.That(S302MCodec.Reverse[S302MCodec.Reverse[i]], Is.EqualTo(i), $"involution at {i}");
  }

  [Test]
  public void ReadHeader_DecodesChannelsAndBits() {
    // 16-bit stereo, frame_size to match payload of one sample pair (5 bytes).
    var samples = new[] { 1234, -5678 };
    var packet = S302MCodec.Encode(samples, channels: 2, bitsPerSample: 16);
    var header = S302MCodec.ReadHeader(packet);
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Value.Channels, Is.EqualTo(2));
    Assert.That(header.Value.BitsPerSample, Is.EqualTo(16));
    Assert.That(header.Value.FrameSizeBytes, Is.EqualTo(packet.Length - S302MCodec.Aes3HeaderLength));
  }

  [Test]
  public void ReadHeader_RejectsTooShortAndOver24Bit() {
    Assert.That(S302MCodec.ReadHeader(new byte[3]), Is.Null, "shorter than header + 1");
    // bits field 0b11 → 28 bits per sample (> 24): invalid.
    var bad = new byte[] { 0x00, 0x05, 0x00, 0x30 };
    Assert.That(S302MCodec.ReadHeader(bad), Is.Null);
  }

  [TestCase(16)]
  [TestCase(20)]
  [TestCase(24)]
  public void RoundTrip_16_20_24Bit_Stereo_IsSampleExact(int bits) {
    var max = (1 << (bits - 1)) - 1;
    var min = -(1 << (bits - 1));
    int[] samples = [0, 0, 1, -1, max, min, 12345 & max, -(12345 & max), 0x5A5A & max, -(0x5A5A & max)];

    var packet = S302MCodec.Encode(samples, channels: 2, bitsPerSample: bits);
    var decoded = S302MCodec.DecodeInterleaved(packet);
    Assert.That(decoded, Is.EqualTo(samples), $"{bits}-bit interleaved round-trip");
  }

  [Test]
  public void RoundTrip_FourChannels_SplitsToChannels() {
    int[] interleaved = [10, 20, 30, 40, -10, -20, -30, -40];
    var packet = S302MCodec.Encode(interleaved, channels: 4, bitsPerSample: 16);
    var channels = S302MCodec.DecodeToChannels(packet, out var rate, out var ch, out var bps);
    Assert.That(rate, Is.EqualTo(48000));
    Assert.That(ch, Is.EqualTo(4));
    Assert.That(bps, Is.EqualTo(16));
    Assert.That(channels[0], Is.EqualTo(new[] { 10, -10 }));
    Assert.That(channels[1], Is.EqualTo(new[] { 20, -20 }));
    Assert.That(channels[2], Is.EqualTo(new[] { 30, -30 }));
    Assert.That(channels[3], Is.EqualTo(new[] { 40, -40 }));
  }

  [Test]
  public void DecodeInterleaved_OnInvalidHeader_ReturnsEmpty() {
    Assert.That(S302MCodec.DecodeInterleaved(new byte[2]), Is.Empty);
  }

  [Test]
  public void Encode_RejectsBadChannelOrBitWidth() {
    Assert.That(() => S302MCodec.Encode([0, 0], channels: 3, bitsPerSample: 16), Throws.ArgumentException);
    Assert.That(() => S302MCodec.Encode([0, 0], channels: 2, bitsPerSample: 18), Throws.ArgumentException);
  }
}
