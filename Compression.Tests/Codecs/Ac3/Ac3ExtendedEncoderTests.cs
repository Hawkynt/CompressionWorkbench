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

  [TestCase(24_000, 0)]
  [TestCase(22_050, 1)]
  [TestCase(16_000, 2)]
  [Category("Regression")]
  public void EnhancedEncoder_ReducedRate_WritesFscod2SixBlocksAndDecodes(int sampleRate, int expectedFsCod2) {
    const int samplesPerChannel = 1536;
    var encoded = Ac3Codec.EncodeEnhanced(
      Signal(samplesPerChannel, 1, sampleRate),
      new Eac3EncoderOptions(
        SampleRate: sampleRate,
        Bitrate: 96_000,
        Acmod: 1,
        PadFinalFrame: false));

    var header = Ac3FrameHeader.TryParse(encoded, 0);
    Assert.That(header, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(header!.Value.IsEnhanced, Is.True);
      Assert.That(header.Value.FsCod, Is.EqualTo(3), "reduced rate must use fscod=3");
      Assert.That(header.Value.FsCod2, Is.EqualTo(expectedFsCod2));
      Assert.That(header.Value.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(header.Value.NumBlocks, Is.EqualTo(6), "fscod=3 implies six blocks");
      Assert.That(header.Value.FrameSize, Is.EqualTo(encoded.Length));
    });
    AssertEnhancedCrc(encoded);

    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();
    Ac3Codec.Decompress(input, output);
    Assert.That(output.Length, Is.EqualTo(samplesPerChannel * sizeof(short)));
  }

  [Test]
  [Category("EdgeCase")]
  public void EnhancedEncoder_ReducedRate_RejectsNonSixBlockSyncframe() {
    Assert.That(
      () => Ac3Codec.EncodeEnhanced(
        new short[256],
        new Eac3EncoderOptions(
          SampleRate: 24_000,
          Bitrate: 96_000,
          Acmod: 1,
          PadFinalFrame: false,
          BlocksPerFrame: 1)),
      Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains("six audio blocks"));
  }

  [TestCase(4, 30)] // 24 kHz: band 47 >> 1 = 23, hth[23,48k] = 0x350 -> address 30
  [TestCase(5, 29)] // 22.05 kHz: hth[23,44.1k] = 0x360 -> address 29
  [TestCase(6, 28)] // 16 kHz: hth[23,32k] = 0x390 -> address 28
  [Category("KnownAnswer")]
  public void BitAllocation_ReducedRate_UsesBaseFamilyWithShiftedCriticalBand(int reducedRateSelector, int expectedAddress) {
    // Clean-room KAT from A/52 Table 7.15 plus Annex E sr_shift=1 behavior. Coupling mode with very
    // large decay/gain values makes the hearing threshold dominate the mask. An identity bap table
    // then exposes the exact post-mask address rather than hiding it behind the normal bap LUT.
    var exp = Enumerable.Repeat((byte)10, 256).ToArray(); // psd = 3072 - 10*128 = 1792
    var bap = new byte[256];
    var identity = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
    var p = new Ac3BitAllocation.AllocParams(4096, 4096, 4096, 0, 0);

    Ac3BitAllocation.ComputeBap(
      exp, bap, start: 181, end: 205, p,
      fgain: 4096, snrOffset: 0, fscod: reducedRateSelector,
      isCoupling: true, cplFastLeak: 0, cplSlowLeak: 0,
      deltas: null, bapTable: identity);

    Assert.That(bap[181], Is.EqualTo(expectedAddress));
  }

  [Test]
  [Category("Regression")]
  public void BitAllocation_ReducedRate_DoesNotFallBackToRawFscodThreeAs32Khz() {
    var exp = Enumerable.Repeat((byte)10, 256).ToArray();
    var reduced = new byte[256];
    var oldFallback = new byte[256];
    var identity = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
    var p = new Ac3BitAllocation.AllocParams(4096, 4096, 4096, 0, 0);

    Ac3BitAllocation.ComputeBap(exp, reduced, 181, 205, p, 4096, 0, 6, true, 0, 0, null, identity);
    Ac3BitAllocation.ComputeBap(exp, oldFallback, 181, 205, p, 4096, 0, 3, true, 0, 0, null, identity);

    Assert.Multiple(() => {
      Assert.That(reduced[181], Is.EqualTo(28));
      Assert.That(oldFallback[181], Is.EqualTo(22));
      Assert.That(reduced[181], Is.Not.EqualTo(oldFallback[181]));
    });
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
    const int frameBytes = 768;
    var w = new BitWriter();
    w.Put(0x0B77, 16);
    w.Put(0, 16);
    w.Put(0, 2);
    w.Put(20, 6);
    w.Put(8, 5);
    w.Put(0, 3);
    w.Put(0, 3);
    w.Flag(false);
    w.Put(31, 5);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Put(27, 5);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);
    w.Flag(false);

    for (var block = 0; block < 6; ++block)
      WriteDualMonoSilenceBlock(w, block);
    return w.ToBytes(frameBytes);
  }

  private static void WriteDualMonoSilenceBlock(BitWriter w, int block) {
    w.Flag(false); w.Flag(false);
    w.Flag(false); w.Flag(false);
    w.Flag(false);
    w.Flag(false);

    if (block == 0) {
      w.Flag(true);
      w.Flag(false);
      w.Put(1, 2); w.Put(1, 2);
      w.Put(0, 6); w.Put(0, 6);
      WriteFlatExponents(w);
      WriteFlatExponents(w);
      w.Flag(true);
      w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 2); w.Put(0, 3);
      w.Flag(true);
      w.Put(0, 6);
      w.Put(0, 4); w.Put(0, 3);
      w.Put(0, 4); w.Put(0, 3);
    } else {
      w.Flag(false);
      w.Put(0, 2); w.Put(0, 2);
      w.Flag(false);
      w.Flag(false);
    }
    w.Flag(false);
    w.Flag(false);
  }

  private static void WriteFlatExponents(BitWriter w) {
    w.Put(15, 4);
    for (var group = 0; group < 12; ++group)
      w.Put(62, 7);
    w.Put(0, 2);
  }

  private static void AssertEnhancedCrc(byte[] frame) {
    var expected = Crc16(frame.AsSpan(2, frame.Length - 4));
    var actual = (ushort)((frame[^2] << 8) | frame[^1]);
    Assert.That(actual, Is.EqualTo(expected));
  }

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
