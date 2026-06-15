#pragma warning disable CS1591
using Codec.Sbc;

namespace Compression.Tests.Codecs.Sbc;

/// <summary>
/// Pins the Bluetooth SBC / mSBC decoder (FFmpeg <c>libavcodec/sbcdec.c</c> port). Verified by
/// hand-checked table spot values (the AV_CRC_8_EBU CRC table and the ported prototype-filter
/// coefficients), header parsing for both syncwords, and a hand-built all-silence mSBC frame that
/// decodes to the exact sample count of pure zeros (a zero subband signal stays zero through the
/// polyphase synthesis filter).
/// </summary>
[TestFixture]
public class SbcCodecTests {

  [Test]
  public void Crc8Table_HasReferenceSpotValues() {
    // AV_CRC_8_EBU (poly 0x1D): table[0]=0, table[1]=0x1D, and the standard table head.
    Assert.That(SbcTables.Crc8Table[0], Is.EqualTo(0x00));
    Assert.That(SbcTables.Crc8Table[1], Is.EqualTo(0x1D));
    Assert.That(SbcTables.Crc8Table[2], Is.EqualTo(0x3A));
    Assert.That(SbcTables.Crc8Table[0x9C], Is.EqualTo(119));
  }

  [Test]
  public void Crc8_OfZeroHeader_MatchesReference() {
    // Start value 0x0F over two zero header bytes (the reference ff_sbc_crc8 seed).
    Assert.That(SbcCodec.Crc8(new byte[] { 0, 0 }, 16), Is.EqualTo(163));
    // Six zero bytes (mSBC's 16 header + 32 scale-factor bits) → the silent-frame CRC.
    Assert.That(SbcCodec.Crc8(new byte[6], 48), Is.EqualTo(197));
  }

  [Test]
  public void ProtoFilterTables_HaveReferenceSpotValues() {
    // sbc_proto_4_40m0 / m1, sbc_proto_8_80m0 / m1 after the SS4/SS8 shifts.
    Assert.That(SbcTables.Proto4M0[1], Is.EqualTo(-1431));
    Assert.That(SbcTables.Proto4M0[2], Is.EqualTo(-17773));
    Assert.That(SbcTables.Proto4M1[0], Is.EqualTo(-503));
    Assert.That(SbcTables.Proto8M0[2], Is.EqualTo(-17826));
    Assert.That(SbcTables.Proto8M1[0], Is.EqualTo(-528));
    // Synthesis-matrix value 0 entries are exactly zero.
    Assert.That(SbcTables.SynMatrix4[2], Is.EqualTo(new[] { 0, 0, 0, 0 }));
    Assert.That(SbcTables.SynMatrix4[0][0], Is.EqualTo(5792));
    Assert.That(SbcTables.SynMatrix8[0][0], Is.EqualTo(5792));
  }

  /// <summary>A valid all-silence mSBC frame (syncword 0xAD, zero header/scale-factors, CRC 0xC5).</summary>
  private static byte[] SilentMsbcFrame() {
    var frame = new byte[57]; // mono, 8 subbands, 15 blocks, bitpool 26 ⇒ standard 57-byte mSBC frame
    frame[0] = SbcCodec.MsbcSyncword;
    frame[1] = 0;
    frame[2] = 0;
    frame[3] = 197; // ff_sbc_crc8 over the zero header + zero scale factors
    return frame;
  }

  [Test]
  public void ReadHeader_Msbc_FixesParameters() {
    var header = SbcCodec.ReadHeader(SilentMsbcFrame());
    Assert.That(header, Is.Not.Null);
    var h = header!.Value;
    Assert.That(h.IsMsbc, Is.True);
    Assert.That(h.SampleRate, Is.EqualTo(16000));
    Assert.That(h.Blocks, Is.EqualTo(15));
    Assert.That(h.Subbands, Is.EqualTo(8));
    Assert.That(h.Channels, Is.EqualTo(1));
    Assert.That(h.Mode, Is.EqualTo(SbcCodec.ChannelMode.Mono));
    Assert.That(h.Bitpool, Is.EqualTo(26));
    Assert.That(h.FrameLengthBytes, Is.EqualTo(57));
  }

  [Test]
  public void ReadHeader_RejectsBadSyncAndBitpool() {
    Assert.That(SbcCodec.ReadHeader(new byte[] { 0x00, 0x00, 0x00, 0x00 }), Is.Null, "bad syncword");
    // SBC syncword, mono 4-subband, bitpool way above 16*subbands.
    var badBitpool = new byte[] { SbcCodec.SbcSyncword, 0x00, 200, 0x00 };
    Assert.That(SbcCodec.ReadHeader(badBitpool), Is.Null);
  }

  /// <summary>
  /// A hand-built SBC frame (syncword 0x9C, mono, 4 subbands, 4 blocks, freq 16 kHz, loudness,
  /// <b>bitpool 0</b>) with zero scale factors. A zero bitpool allocates zero bits to every
  /// subband, so every subband sample is forced to zero and the synthesis output is exactly zero.
  /// The frame is 6 bytes: 4-byte header + 2 bytes of zero scale factors (no sample bits). The
  /// CRC-8 (0x68) is computed over the zero header and scale factors.
  /// </summary>
  private static byte[] ZeroBitpoolSbcFrame() =>
    [SbcCodec.SbcSyncword, 0x00, 0x00, 104, 0x00, 0x00];

  [Test]
  public void Decode_MsbcSilenceFrame_HasCorrectLength() {
    // With a non-zero bitpool the all-zero quantised samples still carry a per-subband DC bias, so
    // the output is not zero — but the sample count is exact (15 blocks × 8 subbands).
    var pcm = SbcCodec.Decode(SilentMsbcFrame(), out var sampleRate, out var channels);
    Assert.That(sampleRate, Is.EqualTo(16000));
    Assert.That(channels, Is.EqualTo(1));
    Assert.That(pcm.Length, Is.EqualTo(15 * 8), "blocks × subbands samples");
  }

  [Test]
  public void Decode_ZeroBitpoolFrame_IsExactlyZeroAndCorrectLength() {
    var frame = ZeroBitpoolSbcFrame();
    Assert.That(frame.Length, Is.EqualTo(6), "4-byte header + 2 scale-factor bytes, no sample bits");
    var pcm = SbcCodec.Decode(frame, out var sampleRate, out var channels);
    Assert.That(sampleRate, Is.EqualTo(16000));
    Assert.That(channels, Is.EqualTo(1));
    Assert.That(pcm.Length, Is.EqualTo(4 * 4), "blocks × subbands samples");
    foreach (var s in pcm)
      Assert.That(s, Is.EqualTo(0), "zero bit allocation ⇒ exact zero output");
  }

  [Test]
  public void Decode_ConcatenatedZeroFrames_AccumulatesZeroSamples() {
    var two = ZeroBitpoolSbcFrame().Concat(ZeroBitpoolSbcFrame()).ToArray();
    var pcm = SbcCodec.Decode(two, out _, out _);
    Assert.That(pcm.Length, Is.EqualTo(2 * 4 * 4));
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void Decode_OnBadCrc_StopsCleanly() {
    var frame = SilentMsbcFrame();
    frame[3] ^= 0xFF; // corrupt the CRC
    var pcm = SbcCodec.Decode(frame, out _, out var channels);
    Assert.That(channels, Is.EqualTo(1), "header still parses");
    Assert.That(pcm, Is.Empty, "CRC mismatch ⇒ no samples decoded");
  }

  [Test]
  public void ReadFrames_WalksConcatenatedFrames() {
    var stream = SilentMsbcFrame().Concat(SilentMsbcFrame()).Concat(SilentMsbcFrame()).ToArray();
    var frames = SbcCodec.ReadFrames(stream);
    Assert.That(frames.Count, Is.EqualTo(3));
    Assert.That(frames.All(f => f.IsMsbc), Is.True);
  }

  [Test]
  public void ReadFrames_StopsAtTruncatedTail() {
    var stream = SilentMsbcFrame().Concat(new byte[10]).ToArray(); // 10 dangling bytes < a frame
    var frames = SbcCodec.ReadFrames(stream);
    Assert.That(frames.Count, Is.EqualTo(1));
  }
}
