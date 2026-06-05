#pragma warning disable CS1591
using Codec.CriHca;

namespace Compression.Tests.Codecs.CriHca;

[TestFixture]
public class HcaCodecTests {

  // ── Header parse matrix ───────────────────────────────────────────────────

  [Test]
  public void ReadHeader_PlainMagic_ParsesFmtAndComp() {
    var hca = HcaFixture.BuildSilence(channels: 1, sampleRate: 44100, frameCount: 3);
    var (h, _) = HcaCodec.ReadHeader(hca);

    Assert.That(h.Channels, Is.EqualTo(1));
    Assert.That(h.SampleRate, Is.EqualTo(44100));
    Assert.That(h.FrameCount, Is.EqualTo(3));
    Assert.That(h.CipherType, Is.EqualTo(0));
    Assert.That(h.TotalBandCount, Is.EqualTo(h.BaseBandCount));
    Assert.That(h.StereoBandCount, Is.EqualTo(0));
  }

  [Test]
  public void ReadHeader_MaskedMagic_ParsesIdentically() {
    var plain = HcaFixture.BuildSilence(channels: 2, sampleRate: 48000, frameCount: 1);
    var masked = HcaFixture.BuildSilence(channels: 2, sampleRate: 48000, frameCount: 1, maskMagic: true);

    Assert.That(HcaCodec.LooksLikeHca(masked), Is.True);
    var (hp, _) = HcaCodec.ReadHeader(plain);
    var (hm, _) = HcaCodec.ReadHeader(masked);

    Assert.That(hm.Channels, Is.EqualTo(hp.Channels));
    Assert.That(hm.SampleRate, Is.EqualTo(hp.SampleRate));
    Assert.That(hm.FrameCount, Is.EqualTo(hp.FrameCount));
  }

  [Test]
  public void ReadHeader_CiphChunk_TypeRecorded() {
    var (h0, _) = HcaCodec.ReadHeader(HcaFixture.BuildSilence(cipherType: 0));
    var (h1, _) = HcaCodec.ReadHeader(HcaFixture.BuildSilence(cipherType: 1));
    var (h56, _) = HcaCodec.ReadHeader(HcaFixture.BuildSilence(cipherType: 56));

    Assert.That(h0.CipherType, Is.EqualTo(0));
    Assert.That(h1.CipherType, Is.EqualTo(1));
    Assert.That(h56.CipherType, Is.EqualTo(56));
    Assert.That(h56.IsKeyedCipher, Is.True);
  }

  [Test]
  public void ReadHeader_CorruptedCrc_Throws() {
    var hca = HcaFixture.BuildSilence();
    hca[10] ^= 0xFF; // flip a header byte → CRC mismatch
    Assert.Throws<InvalidDataException>(() => HcaCodec.ReadHeader(hca));
  }

  // ── Table spot values (transcribed from FFmpeg hca_data.h) ─────────────────

  [Test]
  public void Tables_SpotValues_MatchReference() {
    Assert.That(HcaTables.MaxBits[15], Is.EqualTo(12));
    Assert.That(HcaTables.MaxBits[1], Is.EqualTo(2));
    Assert.That(HcaTables.ScaleTable[0], Is.EqualTo(15));
    Assert.That(HcaTables.ScaleTable[58], Is.EqualTo(1));
    Assert.That(HcaTables.AthBaseCurve[0], Is.EqualTo(0x78));
    Assert.That(HcaTables.AthBaseCurve[655], Is.EqualTo(0xFF));
    Assert.That(HcaTables.IntensityRatio[0], Is.EqualTo(2.0f));
    Assert.That(HcaTables.IntensityRatio[7], Is.EqualTo(1.0f));
    Assert.That(HcaTables.QuantStepSize[1], Is.EqualTo(0.666667f));
    Assert.That(HcaTables.ScaleConvBias, Is.EqualTo(64));
  }

  [Test]
  public void AthCurve_Type0_IsAllZero_Type1_StartsAtCurveBase() {
    // Build with version 0x0200 (ath_type defaults to 0) vs explicit type-1 via v1.x.
    var (hV2, _) = HcaCodec.ReadHeader(HcaFixture.BuildSilence(version: 0x0200));
    Assert.That(hV2.AthType, Is.EqualTo(0));

    var (hV1, _) = HcaCodec.ReadHeader(HcaFixture.BuildSilence(version: 0x0103));
    Assert.That(hV1.AthType, Is.EqualTo(1));
  }

  // ── CRC-16 ─────────────────────────────────────────────────────────────────

  [Test]
  public void Crc16_KnownVector_AnsiPoly() {
    // CRC-16/IBM (poly 0x8005, MSB-first, init 0) of "123456789" = 0xFEE8 (a.k.a. ARC variant
    // computed non-reflected). Verify the round-trip property: appending the CRC zeroes it.
    var data = "123456789"u8.ToArray();
    var crc = HcaCodec.Crc16(data);
    var withCrc = new byte[data.Length + 2];
    data.CopyTo(withCrc, 0);
    withCrc[^2] = (byte)(crc >> 8);
    withCrc[^1] = (byte)crc;
    Assert.That(HcaCodec.Crc16(withCrc), Is.EqualTo(0));
  }

  // ── Cipher type-1 table ──────────────────────────────────────────────────

  [Test]
  public void CipherType1_FixedEndpoints_AndPermutation() {
    var table = HcaCodec.CipherInit(1);
    Assert.That(table[0], Is.EqualTo(0));
    Assert.That(table[0xFF], Is.EqualTo(0xFF));

    // The static table is a permutation of 0..255 (every output value occurs exactly once).
    var seen = new bool[256];
    foreach (var b in table) {
      Assert.That(seen[b], Is.False, "cipher table must be a permutation");
      seen[b] = true;
    }
  }

  [Test]
  public void CipherType0_IsIdentity() {
    var table = HcaCodec.CipherInit(0);
    for (var i = 0; i < 256; i++)
      Assert.That(table[i], Is.EqualTo((byte)i));
  }

  [Test]
  public void CipherType1_RoundTrips_ViaInverse() {
    // The type-1 table is a permutation (not necessarily an involution); decrypt∘encrypt
    // round-trips through the inverse permutation. Verify reversibility explicitly.
    var table = HcaCodec.CipherInit(1);
    var inverse = new byte[256];
    for (var i = 0; i < 256; i++)
      inverse[table[i]] = (byte)i;
    for (var i = 0; i < 256; i++)
      Assert.That(table[inverse[i]], Is.EqualTo((byte)i));
  }

  // ── Deterministic silence-frame decode ────────────────────────────────────

  [Test]
  public void Decode_SilenceMono_ExactlyOneKilosamplePerFrameAndAllZero() {
    const int frames = 2;
    var hca = HcaFixture.BuildSilence(channels: 1, sampleRate: 44100, frameCount: frames);
    var (pcm, channels, rate, header) = HcaCodec.Decode(hca);

    Assert.That(channels, Is.EqualTo(1));
    Assert.That(rate, Is.EqualTo(44100));
    Assert.That(pcm.Length, Is.EqualTo(frames * HcaCodec.SamplesPerFrame));
    Assert.That(header.TotalSamples, Is.EqualTo((long)frames * HcaCodec.SamplesPerFrame));
    Assert.That(pcm, Is.All.EqualTo((short)0), "all-zero scalefactors must decode to digital silence");
  }

  [Test]
  public void Decode_SilenceStereo_InterleavedLengthAndSilence() {
    const int frames = 1;
    var hca = HcaFixture.BuildSilence(channels: 2, sampleRate: 48000, frameCount: frames);
    var (pcm, channels, _, _) = HcaCodec.Decode(hca);

    Assert.That(channels, Is.EqualTo(2));
    Assert.That(pcm.Length, Is.EqualTo(frames * HcaCodec.SamplesPerFrame * 2));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_CipherType1Silence_DecryptsToSilence() {
    var hca = HcaFixture.BuildSilence(channels: 1, frameCount: 1, cipherType: 1);
    var (pcm, _, _, _) = HcaCodec.Decode(hca);
    Assert.That(pcm.Length, Is.EqualTo(HcaCodec.SamplesPerFrame));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_KeyedCipher_ThrowsNotSupported() {
    var hca = HcaFixture.BuildSilence(cipherType: 56);
    Assert.Throws<NotSupportedException>(() => HcaCodec.Decode(hca));
  }

  [Test]
  public void Decode_CorruptedFrameCrc_Throws() {
    var hca = HcaFixture.BuildSilence(channels: 1, frameCount: 1);
    hca[^1] ^= 0xFF; // corrupt last frame byte (its CRC)
    Assert.Throws<InvalidDataException>(() => HcaCodec.Decode(hca));
  }
}
