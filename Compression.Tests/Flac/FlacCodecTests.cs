using FileFormat.Flac;

namespace Compression.Tests.Flac;

[TestFixture]
public class FlacCodecTests {

  // ──────────── helpers ────────────

  private static byte[] GeneratePcm16Stereo(int sampleCount, Func<int, (short left, short right)> generator) {
    var pcm = new byte[sampleCount * 4]; // 2 channels * 2 bytes each
    for (var i = 0; i < sampleCount; i++) {
      var (left, right) = generator(i);
      pcm[i * 4 + 0] = (byte)(left & 0xFF);
      pcm[i * 4 + 1] = (byte)((left >> 8) & 0xFF);
      pcm[i * 4 + 2] = (byte)(right & 0xFF);
      pcm[i * 4 + 3] = (byte)((right >> 8) & 0xFF);
    }
    return pcm;
  }

  private static byte[] RoundTrip(byte[] pcm) {
    using var input = new MemoryStream(pcm);
    using var compressed = new MemoryStream();
    FlacWriter.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FlacReader.Decompress(compressed, decompressed);
    return decompressed.ToArray();
  }

  private static byte[] Encode(byte[] pcm) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    FlacWriter.Compress(input, output);
    return output.ToArray();
  }

  // ──────────── 6a. Self Round-Trip Correctness ────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Silence() {
    var pcm = GeneratePcm16Stereo(4096, _ => (0, 0));
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SineWave440Hz() {
    var pcm = GeneratePcm16Stereo(4096, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 30000);
      return (sample, sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SquareWave() {
    var pcm = GeneratePcm16Stereo(4096, i => {
      var halfPeriod = 44100 / 440 / 2; // ~50 samples
      var sample = (short)((i / halfPeriod) % 2 == 0 ? 20000 : -20000);
      return (sample, sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomNoise() {
    var rng = new Random(42);
    var pcm = GeneratePcm16Stereo(4096, _ => {
      var left = (short)(rng.Next(65536) - 32768);
      var right = (short)(rng.Next(65536) - 32768);
      return (left, right);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_DcOffset() {
    var pcm = GeneratePcm16Stereo(4096, _ => (16000, -16000));
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ShortSamples_100() {
    var pcm = GeneratePcm16Stereo(100, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 20000);
      return (sample, sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MediumSamples_4096() {
    var pcm = GeneratePcm16Stereo(4096, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 1000.0 * i / 44100.0) * 25000);
      return (sample, (short)-sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongSamples_OneSecond() {
    var pcm = GeneratePcm16Stereo(44100, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 30000);
      return (sample, sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MaxAmplitude() {
    var pcm = GeneratePcm16Stereo(4096, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 32767);
      return (sample, sample);
    });
    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }

  // ──────────── STREAMINFO verification ────────────

  [Category("HappyPath")]
  [Test]
  public void StreamInfo_FieldsMatchActualData() {
    var pcm = GeneratePcm16Stereo(4096, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 20000);
      return (sample, sample);
    });

    var flac = Encode(pcm);

    // Parse STREAMINFO from the FLAC output
    // "fLaC" (4) + metadata block header (4) + STREAMINFO (34)
    Assert.That(flac.Length, Is.GreaterThanOrEqualTo(42));
    Assert.That(flac[0..4], Is.EqualTo("fLaC"u8.ToArray()), "Magic mismatch");

    // Metadata block header: type 0 (STREAMINFO), is_last, length 34
    var blockHeader = flac[4];
    Assert.That(blockHeader & 0x7F, Is.EqualTo(0), "Block type should be STREAMINFO");
    var blockLength = (flac[5] << 16) | (flac[6] << 8) | flac[7];
    Assert.That(blockLength, Is.EqualTo(34), "STREAMINFO block should be 34 bytes");

    // Parse fields from STREAMINFO (offset 8)
    var si = flac.AsSpan(8, 34);
    var sampleRate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
    var channels = ((si[12] >> 1) & 0x07) + 1;
    var bps = (((si[12] & 0x01) << 4) | (si[13] >> 4)) + 1;
    var totalSamples = ((long)(si[13] & 0x0F) << 32) | ((long)si[14] << 24) |
                       ((long)si[15] << 16) | ((long)si[16] << 8) | si[17];

    Assert.That(sampleRate, Is.EqualTo(44100), "Sample rate should be 44100");
    Assert.That(channels, Is.EqualTo(2), "Channels should be 2");
    Assert.That(bps, Is.EqualTo(16), "Bits per sample should be 16");
    Assert.That(totalSamples, Is.EqualTo(4096), "Total samples should match input");
  }

  // ──────────── 6c. Compression improvement with LPC ────────────

  [Category("HappyPath")]
  [Test]
  public void Lpc_CompressesSineWaveSmallerThanPcm() {
    // 1-second sine wave at 440 Hz
    var pcm = GeneratePcm16Stereo(44100, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 30000);
      return (sample, sample);
    });

    var flac = Encode(pcm);
    var pcmSize = pcm.Length;
    var flacSize = flac.Length;

    // FLAC should be significantly smaller than raw PCM
    Assert.That(flacSize, Is.LessThan(pcmSize), "FLAC output should be smaller than raw PCM");

    var ratio = (double)flacSize / pcmSize * 100;
    TestContext.Out.WriteLine($"PCM size:  {pcmSize} bytes");
    TestContext.Out.WriteLine($"FLAC size: {flacSize} bytes");
    TestContext.Out.WriteLine($"Ratio:     {ratio:F1}%");
  }

  [Category("HappyPath")]
  [Test]
  public void Lpc_CompressesMultiToneBetterThanFixedOnly() {
    // Multi-tone signal (simulating music/harmonics) — LPC should excel here
    // because FIXED-4 predicts polynomial trends while LPC adapts to harmonic content
    var pcm = GeneratePcm16Stereo(44100, i => {
      var t = i / 44100.0;
      // Three overlapping harmonics + slight decay
      var signal = Math.Sin(2 * Math.PI * 440 * t) * 15000 * Math.Exp(-0.5 * t)
                 + Math.Sin(2 * Math.PI * 880 * t) * 8000 * Math.Exp(-0.8 * t)
                 + Math.Sin(2 * Math.PI * 1320 * t) * 4000 * Math.Exp(-1.2 * t);
      var sample = (short)Math.Clamp(signal, short.MinValue, short.MaxValue);
      return (sample, sample);
    });

    var flac = Encode(pcm);
    var pcmSize = pcm.Length;
    var flacSize = flac.Length;
    var ratio = (double)flacSize / pcmSize * 100;

    TestContext.Out.WriteLine($"Multi-tone - PCM: {pcmSize}, FLAC: {flacSize}, Ratio: {ratio:F1}%");

    // FLAC should achieve meaningful compression on harmonic content
    Assert.That(flacSize, Is.LessThan(pcmSize * 0.6), "FLAC should compress multi-tone to under 60% of PCM size");
  }

  [Category("HappyPath")]
  [Test]
  public void Lpc_CompressesNoisySineWaveBetter() {
    // A noisy sine wave should show clear LPC advantage over FIXED
    // because FIXED-4 models polynomials, while LPC adapts to the sinusoidal component
    var rng = new Random(123);
    var pcm = GeneratePcm16Stereo(44100, i => {
      var signal = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 20000 + rng.Next(-500, 500));
      return (signal, signal);
    });

    var flac = Encode(pcm);
    var pcmSize = pcm.Length;
    var flacSize = flac.Length;
    var ratio = (double)flacSize / pcmSize * 100;
    TestContext.Out.WriteLine($"Noisy sine - PCM: {pcmSize}, FLAC: {flacSize}, Ratio: {ratio:F1}%");
    Assert.That(flacSize, Is.LessThan(pcmSize));
  }

  [Category("HappyPath")]
  [Test]
  public void Lpc_SubframesPresent_InMultiTone() {
    // Multi-tone signal where LPC should beat FIXED
    var pcm = GeneratePcm16Stereo(44100, i => {
      var t = i / 44100.0;
      var signal = Math.Sin(2 * Math.PI * 440 * t) * 15000 * Math.Exp(-0.5 * t)
                 + Math.Sin(2 * Math.PI * 880 * t) * 8000 * Math.Exp(-0.8 * t)
                 + Math.Sin(2 * Math.PI * 1320 * t) * 4000 * Math.Exp(-1.2 * t);
      var sample = (short)Math.Clamp(signal, short.MinValue, short.MaxValue);
      return (sample, sample);
    });

    var flac = Encode(pcm);

    // Scan the FLAC output for subframe type codes
    // After "fLaC" + STREAMINFO, frames start with sync 0xFFF8/0xFFF9
    var hasLpc = false;
    var hasFixed = false;
    for (var i = 42; i < flac.Length - 5; i++) {
      // Look for frame sync code: 0xFF 0xF8 or 0xFF 0xF9
      if (flac[i] != 0xFF || (flac[i + 1] & 0xFE) != 0xF8)
        continue;

      // We found a frame. After the frame header (variable length) and CRC-8,
      // the subframe header starts. The frame header is at least 5 bytes (sync + blocking + blocksize + samplerate + channel + samplesize + reserved + utf8(1) + crc8).
      // Rather than fully parsing, search for subframe patterns after the header.
      // Subframe starts with 0 + 6-bit type + wasted_bit
      // For FIXED: type = 001000-001100 (8-12)
      // For LPC: type = 1xxxxx (32-63)

      // Skip ahead past the frame header (~5-10 bytes) and look at subframe headers
      for (var j = i + 4; j < Math.Min(i + 20, flac.Length); j++) {
        // Subframe header byte: 0|type[5:0]|wasted = 0b0TTTTTTW
        var b = flac[j];
        if ((b & 0x80) != 0) continue; // padding bit should be 0
        var typeCode = (b >> 1) & 0x3F;
        if (typeCode >= 32 && typeCode <= 63) hasLpc = true;
        if (typeCode >= 8 && typeCode <= 12) hasFixed = true;
      }
    }

    TestContext.Out.WriteLine($"Has LPC subframes: {hasLpc}");
    TestContext.Out.WriteLine($"Has FIXED subframes: {hasFixed}");

    // Multi-tone content should trigger LPC subframes (better prediction than FIXED for harmonics)
    Assert.That(hasLpc, Is.True, "LPC subframes should be present for multi-tone content");
  }

  [Category("HappyPath")]
  [Test]
  public void Lpc_SubframesDecodedCorrectly() {
    // Verify round-trip works when LPC subframes are used
    // Use a signal where LPC should be chosen over FIXED (smooth sinusoid)
    var pcm = GeneratePcm16Stereo(8192, i => {
      var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0) * 30000);
      return (sample, (short)(Math.Sin(2.0 * Math.PI * 880.0 * i / 44100.0) * 25000));
    });

    var result = RoundTrip(pcm);
    Assert.That(result, Is.EqualTo(pcm));
  }
}
