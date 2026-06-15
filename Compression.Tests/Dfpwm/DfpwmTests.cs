#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Dfpwm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Dfpwm;

namespace Compression.Tests.Dfpwm;

[TestFixture]
public class DfpwmTests {

  // ── Hand-walked decode (exact charge sequence) ──────────────────────────────

  // Input byte 0x00 → 8 zero bits → target -128 every step. Walking ffmpeg's
  // au_decompress from the init state (fq=0,q=0,s=0,lt=-128):
  //   bit0: nq=0; since nq==q and nq!=t, nq-=1 → q=-1; s→8; ov=nq=-1;
  //         fq += (140*(-1)+128)>>8 = (-12)>>8 = -1 → fq=-1; out = -1+128 = 127
  //   bit1: nq=-1+((8*(-127)+512)>>10)=-1+((-504)>>10)=-1+(-1)=-2; q=-2; s→9;
  //         ov=-2; fq += (140*(-1)+128)>>8 = -1 → fq=-2; out = -2+128 = 126
  [Test]
  public void Decode_ZeroByte_MatchesHandWalk() {
    var pcm = DfpwmCodec.Decompress(new byte[] { 0x00 });
    Assert.That(pcm.Length, Is.EqualTo(8));
    Assert.That(pcm[0], Is.EqualTo((byte)127), "sample 0");
    Assert.That(pcm[1], Is.EqualTo((byte)126), "sample 1");
  }

  [Test]
  public void Decode_SixteenBits_ProducesSixteenSamples() {
    var pcm = DfpwmCodec.Decompress(new byte[] { 0xAA, 0x55 });
    Assert.That(pcm.Length, Is.EqualTo(16));
    // The decoder is bounded to the unsigned-8 range.
    foreach (var s in pcm)
      Assert.That(s, Is.InRange((byte)0, (byte)255));
  }

  [Test]
  public void Encode_Decode_RoundTrip_IsStable() {
    // A ramp/triangle exercises the adaptive strength in both directions.
    var src = new byte[256];
    for (var i = 0; i < src.Length; ++i)
      src[i] = (byte)(128 + (int)(80 * Math.Sin(i * 0.2)));

    var encoded = DfpwmCodec.Compress(src);
    Assert.That(encoded.Length, Is.EqualTo((src.Length + 7) / 8));

    var decoded = DfpwmCodec.Decompress(encoded);
    Assert.That(decoded.Length, Is.EqualTo(src.Length));

    // DFPWM is deterministic (and lossy): encoding the same PCM twice yields the
    // same bytes, and decoding those bytes is stable across runs.
    Assert.That(DfpwmCodec.Compress(src), Is.EqualTo(encoded));
    Assert.That(DfpwmCodec.Decompress(encoded), Is.EqualTo(decoded));
  }

  // ── Descriptor ──────────────────────────────────────────────────────────────

  private static byte[] MakeDfpwm(int bytes = 64) {
    var data = new byte[bytes];
    for (var i = 0; i < bytes; ++i)
      data[i] = (byte)(i * 37 + 11);
    return data;
  }

  [Test]
  public void Descriptor_ListsFullAndMono() {
    var df = MakeDfpwm();
    using var ms = new MemoryStream(df);
    var entries = new DfpwmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.dfpwm" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_MonoWavIs8BitAt48k() {
    var df = MakeDfpwm();
    using var ms = new MemoryStream(df);
    using var output = new MemoryStream();
    new DfpwmFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(48000u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
  }

  [Test]
  public void Descriptor_FullDfpwmIsByteExact() {
    var df = MakeDfpwm();
    using var ms = new MemoryStream(df);
    using var output = new MemoryStream();
    new DfpwmFormatDescriptor().ExtractEntry(ms, "FULL.dfpwm", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(df));
  }

  [Test]
  public void Descriptor_Create_PassesThroughFull() {
    var df = MakeDfpwm();
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.dfpwm", df) };
    using var output = new MemoryStream();
    new DfpwmFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(df));
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_MatchesDirectEncode() {
    var src = new byte[64];
    for (var i = 0; i < src.Length; ++i)
      src[i] = (byte)(128 + (int)(60 * Math.Sin(i * 0.3)));
    var wav = PcmCodec.ToWavBlob(src, 1, 48000, 8, formatCode: 1);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var output = new MemoryStream();
    new DfpwmFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(DfpwmCodec.Compress(src)));
  }
}
