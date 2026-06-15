#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.WavArc;
using FileFormat.WavArc;

namespace Compression.Tests.WavArc;

[TestFixture]
public class WavArcTests {

  // Builds a minimal valid .wa file: length-prefixed filename, NUL, 4-char codec
  // tag, a 36-byte data block carrying the embedded RIFF/WAVE/fmt markers + fmt
  // length, the fmt body (channels@+2, rate@+4, bits@+14), then a 'data' chunk
  // whose 4-byte size is skipped, followed by the coded bitstream.
  private static byte[] BuildFile(string method, int channels, int sampleRate, int bits, byte[] coded) {
    using var ms = new MemoryStream();
    var name = "a"u8.ToArray();
    ms.WriteByte((byte)name.Length);
    ms.Write(name);
    ms.WriteByte(0);
    ms.Write(System.Text.Encoding.ASCII.GetBytes(method));

    const int fmtLen = 16;
    var data36 = new byte[36];
    "RIFF"u8.CopyTo(data36.AsSpan(16));
    "WAVE"u8.CopyTo(data36.AsSpan(24));
    "fmt "u8.CopyTo(data36.AsSpan(28));
    BinaryPrimitives.WriteUInt32LittleEndian(data36.AsSpan(32), fmtLen);
    ms.Write(data36);

    var fmt = new byte[fmtLen];
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), 1);                 // format = PCM
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);  // → extradata+38
    BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);  // → extradata+40
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);     // → extradata+50
    ms.Write(fmt);

    ms.Write("data"u8);
    var size = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)coded.Length);
    ms.Write(size);
    ms.Write(coded);
    return ms.ToArray();
  }

  // ── MSB-first bit packing helper (matches WavArcBitReader) ──────────────────

  private sealed class MsbWriter {
    private readonly List<byte> _bytes = [];
    private int _cur, _n;
    public void PutBits(int count, uint value) {
      for (var i = count - 1; i >= 0; --i) {
        _cur = (_cur << 1) | (int)((value >> i) & 1);
        if (++_n == 8) { _bytes.Add((byte)_cur); _cur = 0; _n = 0; }
      }
    }
    public void PutUnaryThenK(uint x, int k, uint low) {
      for (uint i = 0; i < x; ++i) PutBits(1, 1);
      PutBits(1, 0);           // stop bit
      if (k > 0) PutBits(k, low);
    }
    public byte[] ToArray() {
      var r = new List<byte>(_bytes);
      if (_n > 0) r.Add((byte)(_cur << (8 - _n)));
      return r.ToArray();
    }
  }

  // ── 0CPY: byte-exact raw copy ───────────────────────────────────────────────

  [Test]
  public void Decode_0Cpy_16Bit_IsByteExact() {
    short[] samples = [0, 1, -1, 1000, -1000, 32767, -32768, 12345];
    var coded = new byte[samples.Length * 2];
    // 0CPY reads each 16-bit sample as get_bits(16) (consuming the on-disk bytes in
    // order) then byte-swaps the result, so on-disk samples are little-endian.
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(coded.AsSpan(i * 2), samples[i]);

    var file = BuildFile("0CPY", channels: 1, sampleRate: 22050, bits: 16, coded);
    var pcm = WavArcCodec.Decompress(file);

    Assert.That(pcm.Length, Is.EqualTo(samples.Length * 2));
    for (var i = 0; i < samples.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(samples[i]), $"s{i}");
  }

  [Test]
  public void Decode_0Cpy_8Bit_IsByteExact() {
    // 8-bit: stored as get_bits(8) - 0x80; we store (value + 0x80) so it round-trips.
    int[] values = [0, 1, -1, 50, -50, 127, -128];
    var coded = new byte[values.Length];
    for (var i = 0; i < values.Length; ++i)
      coded[i] = (byte)(values[i] + 0x80);

    var file = BuildFile("0CPY", channels: 1, sampleRate: 8000, bits: 8, coded);
    var pcm = WavArcCodec.Decompress(file);

    Assert.That(pcm.Length, Is.EqualTo(values.Length));
    for (var i = 0; i < values.Length; ++i)
      Assert.That((sbyte)(pcm[i] - 0x80), Is.EqualTo((sbyte)values[i]), $"s{i}");
  }

  // ── 1DIF: fixed-difference decode (hand-built bitstream) ────────────────────

  // Builds a 1DIF mono stream: block-type 7 sets nb_samples, then block-type 0
  // (raw Rice residuals) emits them directly. get_urice(k) = unary high bits then
  // k low bits; block_type read via get_urice(1); k read via get_urice(2 for 16-bit)+1.
  [Test]
  public void Decode_1Dif_Type0RawResiduals_IsByteExact() {
    short[] residuals = [3, -4, 0, 7, -1];
    var mw = new MsbWriter();

    // block_type 7 (set nb_samples): get_urice(1) == 7  → unary 3, low bit 1 → (3<<1)|1 = 7
    mw.PutUnaryThenK(3, 1, 1);
    mw.PutBits(8, (uint)residuals.Length);   // nb_samples

    // block_type 0: get_urice(1) == 0 → unary 0, low bit 0
    mw.PutUnaryThenK(0, 1, 0);
    // k for 16-bit: get_urice(2) + 1; pick k=2 → get_urice(2) must be 1 → unary 0, low bits 01
    mw.PutUnaryThenK(0, 2, 1);
    var kLow = 2;

    // residuals via get_srice(k): zig-zag z = (val<0) ? ~(2*val) ... encode srice.
    foreach (var r in residuals) {
      var z = r >= 0 ? (uint)(r << 1) : (uint)(~r << 1 | 1);
      var high = z >> kLow;
      var low = z & ((1u << kLow) - 1);
      mw.PutUnaryThenK(high, kLow, low);
    }

    var file = BuildFile("1DIF", channels: 1, sampleRate: 16000, bits: 16, mw.ToArray());
    var pcm = WavArcCodec.Decompress(file);

    Assert.That(pcm.Length, Is.GreaterThanOrEqualTo(residuals.Length * 2));
    for (var i = 0; i < residuals.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(residuals[i]), $"s{i}");
  }

  // ── Header parse + descriptor ───────────────────────────────────────────────

  [Test]
  public void ReadStreamInfo_ParsesGeometry() {
    var file = BuildFile("0CPY", channels: 2, sampleRate: 44100, bits: 16, new byte[8]);
    var info = WavArcCodec.ReadStreamInfo(file, out _);
    Assert.That(info.Method, Is.EqualTo("0CPY"));
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.BitsPerSample, Is.EqualTo(16));
  }

  [Test]
  public void Descriptor_ListsFullAndChannel_0Cpy() {
    short[] samples = [10, -10, 20, -20];
    var coded = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(coded.AsSpan(i * 2), samples[i]);
    var file = BuildFile("0CPY", 1, 22050, 16, coded);

    using var ms = new MemoryStream(file);
    var entries = new WavArcFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.wa" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_FullWaIsByteExact() {
    var file = BuildFile("0CPY", 1, 22050, 16, new byte[4]);
    using var ms = new MemoryStream(file);
    using var output = new MemoryStream();
    new WavArcFormatDescriptor().ExtractEntry(ms, "FULL.wa", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(file));
  }

  [Test]
  public void Descriptor_GracefulFallback_OnAdaptiveMethod() {
    // 5ELP is not verified → decode throws → FULL-only listing.
    var file = BuildFile("5ELP", 1, 44100, 16, new byte[16]);
    using var ms = new MemoryStream(file);
    var entries = new WavArcFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.wa"));
  }

  [Test]
  public void Descriptor_GracefulFallback_OnGarbage() {
    using var ms = new MemoryStream(new byte[] { 9, 9, 9, 9 });
    var entries = new WavArcFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.wa"));
  }
}
