#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.InterplayAcm;

namespace Compression.Tests.InterplayAcm;

/// <summary>
/// Structure/determinism tests for the Interplay ACM decoder. Cross-validation
/// against a reference decoder is not possible here (no published vectors), so these
/// pin the documented invariants of the FFmpeg port: header parsing, exact sample
/// counts per the header geometry, the zero-bitstream "silence" property (an all-zero
/// stream selects the zero filler for every column and decodes to exact silence), and
/// tolerance of truncated input.
/// </summary>
[TestFixture]
public class InterplayAcmTests {

  // Builds a minimal ACM file: 14-byte header + bitstream payload.
  private static byte[] BuildAcm(uint totalSamples, int channels, int sampleRate, int level, int rows, byte[] bitstream) {
    var header = new byte[14];
    BinaryPrimitives.WriteUInt32LittleEndian(header, InterplayAcmCodec.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), totalSamples);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)channels);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)sampleRate);
    var word = (ushort)((level & 0xF) | (rows << 4));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), word);

    var blob = new byte[header.Length + bitstream.Length];
    header.CopyTo(blob.AsSpan());
    bitstream.CopyTo(blob.AsSpan(header.Length));
    return blob;
  }

  [Test]
  public void ParseHeader_ReadsFieldsAndSplitsLevelRows() {
    var blob = BuildAcm(totalSamples: 1000, channels: 1, sampleRate: 22050, level: 4, rows: 0x123, bitstream: []);
    var h = InterplayAcmCodec.ParseHeader(blob);

    Assert.That(h.Magic, Is.EqualTo(InterplayAcmCodec.Magic));
    Assert.That(h.TotalSamples, Is.EqualTo(1000u));
    Assert.That(h.Channels, Is.EqualTo(1));
    Assert.That(h.SampleRate, Is.EqualTo(22050));
    Assert.That(h.Level, Is.EqualTo(4));
    Assert.That(h.Rows, Is.EqualTo(0x123));
  }

  [Test]
  public void ParseHeader_RejectsBadMagic() {
    var blob = new byte[14];
    Assert.Throws<InvalidDataException>(() => InterplayAcmCodec.ParseHeader(blob));
  }

  [Test]
  public void ParseHeader_RejectsTooShort() {
    Assert.Throws<InvalidDataException>(() => InterplayAcmCodec.ParseHeader(new byte[8]));
  }

  [Test]
  public void Decode_ZeroBitstream_DecodesToSilence() {
    // level=2 → cols=4, rows=8 → block_len=32 samples. A zero bitstream makes every
    // block: pwr=0 (count=1), val=0 (midbuf all 0), and every 5-bit column index = 0
    // (the zero filler), so the whole block is zeros — exact silence.
    const int level = 2, rows = 8;
    const uint total = 64; // two blocks of 32 samples
    var blob = BuildAcm(total, channels: 1, sampleRate: 22050, level, rows, bitstream: new byte[64]);

    var (samples, channels, rate) = InterplayAcmCodec.Decode(blob);

    Assert.That(channels, Is.EqualTo(1));
    Assert.That(rate, Is.EqualTo(22050));
    Assert.That(samples.Length, Is.EqualTo((int)total));
    Assert.That(samples, Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_StopsAtHeaderTotalSampleCount() {
    // block_len = cols(2) * rows(4) = 8; request only 5 samples.
    var blob = BuildAcm(totalSamples: 5, channels: 1, sampleRate: 11025, level: 1, rows: 4, bitstream: new byte[64]);
    var (samples, _, _) = InterplayAcmCodec.Decode(blob);
    Assert.That(samples.Length, Is.EqualTo(5));
  }

  [Test]
  public void Decode_TruncatedBitstream_IsTolerated() {
    // Ask for many samples but only supply a couple of bitstream bytes; the EOF-tolerant
    // reader yields zeros and the decode loop terminates once it runs past the end.
    var blob = BuildAcm(totalSamples: 10_000, channels: 1, sampleRate: 22050, level: 2, rows: 8, bitstream: new byte[3]);
    Assert.DoesNotThrow(() => InterplayAcmCodec.Decode(blob));
    var (samples, _, _) = InterplayAcmCodec.Decode(blob);
    Assert.That(samples.Length, Is.LessThan(10_000));
  }

  [Test]
  public void Decode_SurfacesRawChannelCountVerbatim() {
    // Even a "2" here is surfaced as-is; Interplay assets are quirky about this field.
    var blob = BuildAcm(totalSamples: 16, channels: 2, sampleRate: 22050, level: 1, rows: 8, bitstream: new byte[64]);
    var (_, channels, _) = InterplayAcmCodec.Decode(blob);
    Assert.That(channels, Is.EqualTo(2));
  }

  [Test]
  public void Decode_DefaultsSampleRateWhenZero() {
    var blob = BuildAcm(totalSamples: 8, channels: 1, sampleRate: 0, level: 1, rows: 4, bitstream: new byte[32]);
    var (_, _, rate) = InterplayAcmCodec.Decode(blob);
    Assert.That(rate, Is.EqualTo(22050));
  }

  [Test]
  public void Decode_IsDeterministic_AcrossRuns() {
    var blob = BuildAcm(totalSamples: 64, channels: 1, sampleRate: 22050, level: 3, rows: 8,
      bitstream: Enumerable.Range(0, 128).Select(i => (byte)(i * 37 + 11)).ToArray());
    var first = InterplayAcmCodec.Decode(blob).Samples;
    var second = InterplayAcmCodec.Decode(blob).Samples;
    Assert.That(second, Is.EqualTo(first));
  }
}
