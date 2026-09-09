#pragma warning disable CS1591
using Codec.Ac3;

namespace Compression.Tests.Codecs.Ac3;

[TestFixture]
public sealed class Ac3ExtendedEncoderTests {

  [Test]
  [Category("Regression")]
  public void LegacyDualMono_EncodesSecondProgramBsiAndDecodes() {
    var pcm = Signal(1536, 2, 48_000);
    var encoded = Ac3Codec.Encode(pcm, new Ac3EncoderOptions(
      SampleRate: 48_000,
      Bitrate: 192_000,
      Acmod: 0,
      DialNorm: -31,
      DialNorm2: -27,
      PadFinalFrame: false));

    var header = Ac3FrameHeader.TryParse(encoded, 0);
    Assert.That(header, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(header!.Value.IsEnhanced, Is.False);
      Assert.That(header.Value.Acmod, Is.EqualTo(0));
      Assert.That(header.Value.SampleRate, Is.EqualTo(48_000));
    });

    var r = new Ac3BitReader(encoded, 0, encoded.Length);
    r.SkipBits(16 + 16 + 2 + 6 + 5 + 3 + 3 + 1); // sync/crc/fscod/frmsizecod/bsid/bsmod/acmod/lfe
    Assert.That(r.ReadBits(5), Is.EqualTo(31u), "primary dialnorm");
    Assert.That(r.ReadFlag(), Is.False, "compre");
    Assert.That(r.ReadFlag(), Is.False, "langcode");
    Assert.That(r.ReadFlag(), Is.False, "audprodie");
    Assert.That(r.ReadBits(5), Is.EqualTo(27u), "dual-mono dialnorm2 must immediately follow program 1 BSI");
    Assert.That(r.ReadFlag(), Is.False, "compr2e");
    Assert.That(r.ReadFlag(), Is.False, "langcod2e");
    Assert.That(r.ReadFlag(), Is.False, "audprodi2e");

    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();
    Ac3Codec.Decompress(input, output);
    Assert.That(output.Length, Is.EqualTo(1536L * 2 * sizeof(short)));
  }

  [Test]
  [Category("Regression")]
  public void LegacyDualMono_ExternalSyntaxSilenceFrameStaysAligned() {
    var frame = BuildDualMonoSilenceFrame();
    using var input = new MemoryStream(frame, writable: false);
    using var output = new MemoryStream();

    Ac3Codec.Decompress(input, output);

    Assert.That(output.Length, Is.EqualTo(1536L * 2 * sizeof(short)));
    Assert.That(output.ToArray(), Is.All.EqualTo((byte)0));
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(6)]
  [Category("HappyPath")]
  public void EnhancedEncoder_WritesAndDecodesEveryFullRateBlockCount(int blocksPerFrame) {
    const int sampleRate = 48_000;
    var samplesPerChannel = blocksPerFrame * 256;
    var encoded = Ac3Codec.EncodeEnhanced(
      Signal(samplesPerChannel, 2, sampleRate),
      new Eac3EncoderOptions(
        SampleRate: sampleRate,
        Bitrate: 640_000,
        Acmod: 2,
        PadFinalFrame: false,
        BlocksPerFrame: blocksPerFrame));

    var header = Ac3FrameHeader.TryParse(encoded, 0);
    Assert.That(header, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(header!.Value.IsEnhanced, Is.True);
      Assert.That(header.Value.Bsid, Is.EqualTo(16));
      Assert.That(header.Value.StreamType, Is.EqualTo(0));
      Assert.That(header.Value.SubstreamId, Is.EqualTo(0));
      Assert.That(header.Value.NumBlocks, Is.EqualTo(blocksPerFrame));
      Assert.That(header.Value.FrameSize, Is.EqualTo(encoded.Length));
      Assert.That(header.Value.SampleRate, Is.EqualTo(sampleRate));
    });
    AssertEnhancedCrc(encoded);

    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();
    Ac3Codec.Decompress(input, output);
    Assert.That(output.Length, Is.EqualTo((long)samplesPerChannel * 2 * sizeof(short)));
  }

  [Test]
  [Category("HappyPath")]
  public void EnhancedEncoder_CoversEveryAcmodAndLfeCombination() {
    for (var acmod = 0; acmod <= 7; ++acmod) {
      for (var lfe = 0; lfe <= 1; ++lfe) {
        var channels = Ac3FrameHeader.AcmodChannelCount(acmod) + lfe;
        var encoded = Ac3Codec.EncodeEnhanced(
          new short[1536 * channels],
          new Eac3EncoderOptions(
            SampleRate: 48_000,
            Bitrate: 640_000,
            Acmod: acmod,
            LowFrequencyEffects: lfe != 0,
            PadFinalFrame: false,
            BlocksPerFrame: 6));

        var header = Ac3FrameHeader.TryParse(encoded, 0);
        Assert.That(header, Is.Not.Null, $"acmod={acmod}, lfe={lfe}");
        Assert.Multiple(() => {
          Assert.That(header!.Value.Acmod, Is.EqualTo(acmod), $"acmod={acmod}, lfe={lfe}");
          Assert.That(header.Value.LowFrequencyEffects, Is.EqualTo(lfe != 0), $"acmod={acmod}, lfe={lfe}");
          Assert.That(header.Value.IsEnhanced, Is.True, $"acmod={acmod}, lfe={lfe}");
        });

        using var input = new MemoryStream(encoded, writable: false);
        using var output = new MemoryStream();
        Ac3Codec.Decompress(input, output);
        Assert.That(output.Length, Is.EqualTo(1536L * channels * sizeof(short)), $"acmod={acmod}, lfe={lfe}");
      }
    }
  }

  [TestCase(1_024_000, 6)]
  [TestCase(1_100_000, 3)]
  [TestCase(2_100_000, 2)]
  [TestCase(3_100_000, 1)]
  [Category("EdgeCase")]
  public void EnhancedEncoder_AutoSelectsLargestRepresentableBlockCount(int bitrate, int expectedBlocks) {
    var samples = expectedBlocks * 256;
    var encoded = Ac3Codec.EncodeEnhanced(
      new short[samples],
      new Eac3EncoderOptions(
        SampleRate: 48_000,
        Bitrate: bitrate,
        Acmod: 1,
        PadFinalFrame: false));

    var header = Ac3FrameHeader.TryParse(encoded, 0);
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Value.NumBlocks, Is.EqualTo(expectedBlocks));
  }

  private static short[] Signal(int samplesPerChannel, int channels, int sampleRate) {
    var result = new short[samplesPerChannel * channels];
    for (var frame = 0; frame < samplesPerChannel; ++frame)
      for (var channel = 0; channel < channels; ++channel)
        result[frame * channels + channel] = (short)Math.Round(
          Math.Sin(2 * Math.PI * (170 + channel * 97) * frame / sampleRate) * 7_000);
    return result;
  }

  private static byte[] BuildDualMonoSilenceFrame() {
    const int frameBytes = 768; // 192 kbit/s @ 48 kHz, frmsizecod 20
    var w = new BitWriter();
    w.Put(0x0B77, 16);
    w.Put(0, 16);
    w.Put(0, 2);       // fscod 48 kHz
    w.Put(20, 6);      // 192 kbit/s
    w.Put(8, 5);       // bsid
    w.Put(0, 3);       // bsmod
    w.Put(0, 3);       // acmod 1+1
    w.Flag(false);     // lfeon
    w.Put(31, 5);      // dialnorm program 1
    w.Flag(false);     // compre
    w.Flag(false);     // langcode
    w.Flag(false);     // audprodie
    w.Put(27, 5);      // mandatory dialnorm2
    w.Flag(false);     // compr2e
    w.Flag(false);     // langcod2e
    w.Flag(false);     // audprodi2e
    w.Flag(false);     // copyrightb
    w.Flag(false);     // origbs
    w.Flag(false);     // timecod1e
    w.Flag(false);     // timecod2e
    w.Flag(false);     // addbsie

    for (var block = 0; block < 6; ++block)
      WriteDualMonoSilenceBlock(w, block);
    return w.ToBytes(frameBytes);
  }

  private static void WriteDualMonoSilenceBlock(BitWriter w, int block) {
    w.Flag(false); w.Flag(false); // blksw[2]
    w.Flag(false); w.Flag(false); // dithflag[2]
    w.Flag(false);                // dynrnge
    w.Flag(false);                // dynrng2e

    if (block == 0) {
      w.Flag(true);               // cplstre
      w.Flag(false);              // cplinu
      w.Put(1, 2); w.Put(1, 2);   // D15 exponent strategy
      w.Put(0, 6); w.Put(0, 6);   // chbwcod
      WriteFlatExponents(w);
      WriteFlatExponents(w);
      w.Flag(true);               // baie
      w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 3);
      w.Flag(true);               // snroffste
      w.Put(0, 6);                // csnroffst -> all bap zero
      w.Put(0, 4); w.Put(0, 3);
      w.Put(0, 4); w.Put(0, 3);
    } else {
      w.Flag(false);              // cplstre
      w.Put(0, 2); w.Put(0, 2);   // exponent reuse
      w.Flag(false);              // baie reuse
      w.Flag(false);              // snroffste reuse
    }
    w.Flag(false);                // deltbaie
    w.Flag(false);                // skiple
  }

  private static void WriteFlatExponents(BitWriter w) {
    w.Put(15, 4);
    for (var group = 0; group < 12; ++group) // GroupCount(37, D15)
      w.Put(62, 7);                         // delta codes 2,2,2 -> flat exponent
    w.Put(0, 2);                            // gainrng
  }

  // A/52 Annex E: crc2 covers the frame from just after the sync word up to the CRC field itself.
  private static void AssertEnhancedCrc(byte[] frame) {
    var expected = Crc16(frame.AsSpan(2, frame.Length - 4));
    var actual = (ushort)((frame[^2] << 8) | frame[^1]);
    Assert.That(actual, Is.EqualTo(expected));
  }

  // A/52 CRC-16: x^16 + x^15 + x^2 + 1, processed most-significant bit first, zero seed.
  private static ushort Crc16(ReadOnlySpan<byte> data) {
    var crc = 0;
    foreach (var value in data) {
      crc ^= value << 8;
      for (var bit = 0; bit < 8; ++bit)
        crc = ((crc & 0x8000) != 0) ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
    }
    return (ushort)crc;
  }

  private sealed class BitWriter {
    private readonly List<bool> _bits = [];

    public void Put(int value, int count) {
      for (var bit = count - 1; bit >= 0; --bit)
        this._bits.Add(((value >> bit) & 1) != 0);
    }

    public void Flag(bool value) => this._bits.Add(value);

    public byte[] ToBytes(int totalBytes) {
      if (this._bits.Count > totalBytes * 8)
        throw new InvalidOperationException("test frame does not fit selected syncframe size");
      var result = new byte[totalBytes];
      for (var bit = 0; bit < this._bits.Count; ++bit)
        if (this._bits[bit])
          result[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
      return result;
    }
  }
}