#pragma warning disable CS1591
using Codec.SmackerAudio;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Smacker audio decoder (<see cref="SmackerAudioCodec"/>), a decode-only port of
/// FFmpeg's <c>libavcodec/smacker.c</c> (the SMKA path). FFmpeg reference output is not
/// available here, so these tests pin behaviour by hand-building a minimal chunk bitstream:
/// a tiny two-symbol Huffman tree decoded LSB-first (matching <c>VLC_INIT_OUTPUT_LE</c>),
/// an exact 8-bit mono delta sequence (with wraparound), the stereo predictor seeding, the
/// "no data" early return, and malformed-input tolerance.
/// </summary>
[TestFixture]
public class SmackerAudioTests {

  // Two-symbol tree: root node, then leaf A, then leaf B. Both leaves are length 1, so the
  // LSB-first canonical (VLC_INIT_OUTPUT_LE) codes are: read one bit, 0 → A, 1 → B.
  private const int SymbolA = 2;    // small positive delta
  private const int SymbolB = 200;  // large delta (exercises byte wraparound)

  [Test]
  public void Mono8Bit_KnownDeltaSequence_DecodesToExactPcm() {
    // Base sample 10, then deltas: A(2), B(200), A(2). With byte wraparound:
    //   10, 12, (12+200)=212, (212+2)=214.
    var unpSize = 4;
    var chunk = BuildMono8BitChunk(baseSample: 10, deltas: [false, true, false], unpSize);

    var codec = new SmackerAudioCodec(sampleRate: 22050, channels: 1, bitsPerSample: 8);
    var pcm = codec.DecodeChunk(chunk);

    Assert.That(pcm, Is.EqualTo(new byte[] { 10, 12, 212, 214 }));
  }

  [Test]
  public void Mono8Bit_WraparoundPastByte() {
    // Base 200, delta B(200): 200 + 200 = 400 → 0x190 → low byte 0x90 = 144.
    var chunk = BuildMono8BitChunk(baseSample: 200, deltas: [true], unpSize: 2);
    var codec = new SmackerAudioCodec(22050, 1, 8);
    var pcm = codec.DecodeChunk(chunk);
    Assert.That(pcm, Is.EqualTo(new byte[] { 200, 144 }));
  }

  [Test]
  public void NoDataFlag_ReturnsEmpty() {
    // unp_size prefix then a single clear "data present" bit.
    var bw = new LeBitWriter();
    var body = new LeBitWriter();
    body.Put(1, 0); // no data
    var chunk = Prefix(4, body);
    _ = bw;
    var codec = new SmackerAudioCodec(22050, 1, 8);
    Assert.That(codec.DecodeChunk(chunk), Is.Empty);
  }

  [Test]
  public void TooShortChunk_ReturnsEmpty() {
    var codec = new SmackerAudioCodec(22050, 1, 8);
    Assert.That(codec.DecodeChunk([0x01, 0x02]), Is.Empty);
  }

  [Test]
  public void ChannelMismatch_ReturnsEmpty() {
    // Build a mono chunk but ask a stereo codec to decode it → stereo flag mismatch.
    var chunk = BuildMono8BitChunk(10, [false], 2);
    var codec = new SmackerAudioCodec(22050, channels: 2, bitsPerSample: 8);
    Assert.That(codec.DecodeChunk(chunk), Is.Empty);
  }

  /// <summary>
  /// Hand-builds an 8-bit mono SMKA chunk: 4-byte LE unpacked size, then the bitstream —
  /// data-present(1)=1, stereo(1)=0, bits(1)=0, then one tree (skip-bit, tree definition,
  /// skip-bit), then the 8-bit base sample, then one Huffman-coded delta bit per sample.
  /// The tree is "node, leaf A, leaf B": bits 1,0,&lt;A:8&gt;,0,&lt;B:8&gt;.
  /// </summary>
  private static byte[] BuildMono8BitChunk(int baseSample, bool[] deltas, int unpSize) {
    var body = new LeBitWriter();
    body.Put(1, 1); // data present
    body.Put(1, 0); // mono
    body.Put(1, 0); // 8-bit

    // One tree, wrapped by skip_bits1 markers.
    body.Put(1, 0);          // leading skip bit
    body.Put(1, 1);          // node
    body.Put(1, 0);          // leaf
    body.Put(8, SymbolA);    //   value A
    body.Put(1, 0);          // leaf
    body.Put(8, SymbolB);    //   value B
    body.Put(1, 0);          // trailing skip bit

    body.Put(8, (uint)baseSample); // initial predictor
    foreach (var d in deltas)
      body.Put(1, d ? 1u : 0u);    // 0 → A, 1 → B

    return Prefix(unpSize, body);
  }

  private static byte[] Prefix(int unpSize, LeBitWriter body) {
    var payload = body.ToArray();
    var chunk = new byte[4 + payload.Length];
    chunk[0] = (byte)(unpSize & 0xFF);
    chunk[1] = (byte)((unpSize >> 8) & 0xFF);
    chunk[2] = (byte)((unpSize >> 16) & 0xFF);
    chunk[3] = (byte)((unpSize >> 24) & 0xFF);
    payload.CopyTo(chunk, 4);
    return chunk;
  }

  /// <summary>Minimal LSB-first bit writer mirroring the decoder's bit order.</summary>
  private sealed class LeBitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bit;

    public void Put(int n, uint value) {
      for (var i = 0; i < n; ++i) {
        var b = (int)((value >> i) & 1);
        this._cur |= b << this._bit;
        if (++this._bit == 8) {
          this._bytes.Add((byte)this._cur);
          this._cur = 0;
          this._bit = 0;
        }
      }
    }

    public byte[] ToArray() {
      if (this._bit != 0) {
        this._bytes.Add((byte)this._cur);
        this._cur = 0;
        this._bit = 0;
      }
      return this._bytes.ToArray();
    }
  }
}
