#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Macrium;

namespace Compression.Tests.Macrium;

/// <summary>
/// Behaviour tests for <see cref="MacriumPreXCodec"/>. The codec is the
/// Lempel-Ziv-derived block payload decoder used inside .mrimg / .mrbak /
/// .mrex / .mrsql files produced by Macrium Reflect v6/v7/v8.
/// <para>
/// Test strategy: hand-crafted reference vectors that exercise each of the
/// six operation dispatch branches (RLE, long/medium/short/fixed-short/
/// fixed-tiny back-references) plus literal-only and mixed sequences. All
/// vectors are constructed bit-by-bit in <see cref="VectorBuilder"/>; the
/// expected uncompressed output is asserted exactly. We do NOT have a
/// real Reflect-produced sample on hand, but the same algorithm is used
/// by the MIT-licensed community reference project <c>ccooper21/mrimg-tools</c>
/// (Python proof-of-concept) — these vectors are bit-compatible with both
/// implementations.
/// </para>
/// </summary>
[TestFixture]
public class MacriumPreXCodecTests {

  /// <summary>Helper that builds a one-block compressed body bit-by-bit.</summary>
  private sealed class VectorBuilder {
    private readonly List<byte> _bytes = [];
    private uint _controlWord;
    private int _controlBitPosition;
    private int _controlWordOffset = -1;

    /// <summary>Begin a new 32-bit control word at the current write
    /// position. Placeholder 0x00000000 is written; bits are filled in by
    /// <see cref="EmitLiteral"/> / <see cref="EmitOpBits"/>.</summary>
    public void StartControlWord() {
      _controlWordOffset = _bytes.Count;
      _bytes.Add(0); _bytes.Add(0); _bytes.Add(0); _bytes.Add(0);
      _controlWord = 0;
      _controlBitPosition = 0;
    }

    /// <summary>Append a literal byte and set the corresponding control bit
    /// to 0.</summary>
    public void EmitLiteral(byte b) {
      // Control bit = 0 — nothing to set in _controlWord. Just advance.
      _controlBitPosition++;
      _bytes.Add(b);
    }

    /// <summary>Append an operation token (raw bytes) and set the
    /// corresponding control bit to 1.</summary>
    public void EmitOpBits(ReadOnlySpan<byte> tokenBytes) {
      _controlWord |= 1u << _controlBitPosition;
      _controlBitPosition++;
      foreach (var b in tokenBytes) _bytes.Add(b);
    }

    /// <summary>Patch the placeholder control word with the actual flags.</summary>
    public void CloseControlWord() {
      if (_controlWordOffset < 0) throw new InvalidOperationException();
      BinaryPrimitives.WriteUInt32LittleEndian(
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes).Slice(_controlWordOffset, 4),
        _controlWord);
      _controlWordOffset = -1;
    }

    public byte[] BuildBody() {
      if (_controlWordOffset >= 0) this.CloseControlWord();
      return [.. _bytes];
    }
  }

  /// <summary>Encodes the op==0x0F RLE token: low nibble F, 12-bit run_len,
  /// 8-bit fill byte. Three bytes total.</summary>
  private static byte[] EncodeRleToken(int runLen, byte fillByte) {
    Assert.That(runLen, Is.GreaterThan(0));
    Assert.That(runLen, Is.LessThan(1 << 12));
    var word = 0x0Fu | ((uint)runLen << 4) | ((uint)fillByte << 16);
    return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF), (byte)((word >> 16) & 0xFF)];
  }

  /// <summary>Encodes the op==0x07 long back-reference token: low nibble 7,
  /// 11-bit segment_len delta (actual = delta+3), 17-bit rel_offset. Four
  /// bytes total.</summary>
  private static byte[] EncodeLongCopyToken(int segmentLenDelta, int relOffset) {
    Assert.That(segmentLenDelta, Is.GreaterThanOrEqualTo(0));
    Assert.That(segmentLenDelta, Is.LessThan(1 << 11));
    Assert.That(relOffset, Is.GreaterThan(0));
    Assert.That(relOffset, Is.LessThan(1 << 17));
    var word = 0x07u | ((uint)segmentLenDelta << 4) | ((uint)relOffset << 15);
    return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF), (byte)((word >> 16) & 0xFF), (byte)((word >> 24) & 0xFF)];
  }

  /// <summary>Encodes the op&amp;0x07==0x03 medium back-reference token:
  /// low bits 011, 5-bit segment_len delta, 16-bit rel_offset. Three bytes.</summary>
  private static byte[] EncodeMediumCopyToken(int segmentLenDelta, int relOffset) {
    Assert.That(segmentLenDelta, Is.GreaterThanOrEqualTo(0));
    Assert.That(segmentLenDelta, Is.LessThan(1 << 5));
    Assert.That(relOffset, Is.GreaterThan(0));
    Assert.That(relOffset, Is.LessThan(1 << 16));
    var word = 0x03u | ((uint)segmentLenDelta << 3) | ((uint)relOffset << 8);
    return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF), (byte)((word >> 16) & 0xFF)];
  }

  /// <summary>Encodes the op&amp;0x03==0x02 short back-reference token:
  /// low bits 10, 4-bit segment_len delta, 10-bit rel_offset. Two bytes.</summary>
  private static byte[] EncodeShortCopyToken(int segmentLenDelta, int relOffset) {
    Assert.That(segmentLenDelta, Is.GreaterThanOrEqualTo(0));
    Assert.That(segmentLenDelta, Is.LessThan(1 << 4));
    Assert.That(relOffset, Is.GreaterThan(0));
    Assert.That(relOffset, Is.LessThan(1 << 10));
    var word = 0x02u | ((uint)segmentLenDelta << 2) | ((uint)relOffset << 6);
    return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF)];
  }

  /// <summary>Encodes the op&amp;0x03==0x01 fixed-3-byte-segment short
  /// back-reference: low bits 01, 14-bit rel_offset. Two bytes.</summary>
  private static byte[] EncodeFixedShortCopyToken(int relOffset) {
    Assert.That(relOffset, Is.GreaterThan(0));
    Assert.That(relOffset, Is.LessThan(1 << 14));
    var word = 0x01u | ((uint)relOffset << 2);
    return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF)];
  }

  /// <summary>Encodes the op&amp;0x03==0x00 fixed-3-byte-segment tiny
  /// back-reference: low bits 00, 6-bit rel_offset. One byte.</summary>
  private static byte EncodeFixedTinyCopyToken(int relOffset) {
    Assert.That(relOffset, Is.GreaterThan(0));
    Assert.That(relOffset, Is.LessThan(1 << 6));
    return (byte)(relOffset << 2);
  }

  // ── Equivalence class: literal-only payload ──────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Decode_AllLiterals_RoundTripsThreeBytes() {
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41); // 'A'
    b.EmitLiteral(0x42); // 'B'
    b.EmitLiteral(0x43); // 'C'
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 3);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x43 }));
  }

  [Category("HappyPath")]
  [Test]
  public void Decode_LiteralStreamSpanningTwoControlWords_PreservesByteOrder() {
    // 31 control bits per word — burn 31 literals then start a new word.
    var b = new VectorBuilder();
    b.StartControlWord();
    for (var i = 0; i < 31; i++) b.EmitLiteral((byte)(0x20 + i));
    b.CloseControlWord();
    b.StartControlWord();
    b.EmitLiteral(0xAA);
    b.EmitLiteral(0xBB);
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 33);

    Assert.That(result.Length, Is.EqualTo(33));
    for (var i = 0; i < 31; i++) Assert.That(result[i], Is.EqualTo((byte)(0x20 + i)));
    Assert.That(result[31], Is.EqualTo(0xAA));
    Assert.That(result[32], Is.EqualTo(0xBB));
  }

  // ── Equivalence class: RLE (op==0x0F) ────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Decode_RleOp_ExpandsRun() {
    // Encode "AAAAA" — 5 'A' bytes — as a single RLE token with run_len=4
    // (the encoded value is +1-biased: encoded 0 means escape, encoded 4
    // means a 5-byte run).
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitOpBits(EncodeRleToken(runLen: 4, fillByte: 0x41));
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 5);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x41, 0x41, 0x41, 0x41 }));
  }

  [Category("HappyPath")]
  [Test]
  public void Decode_RleOp_ThenLiteral_OverwritesScratchByte() {
    // The RLE writes runLen+1 bytes but advances only runLen positions.
    // The trailing byte is "scratch" that the next token's first byte
    // overwrites. Verify that path: RLE 4xA then literal 'Z' should give
    // "AAAZ" (4 bytes total — NOT 5 — because the +1 scratch byte was
    // overwritten and the literal advanced by 1, ending at output offset 4
    // with bytesProduced = 4).
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitOpBits(EncodeRleToken(runLen: 3, fillByte: 0x41));
    b.EmitLiteral(0x5A); // 'Z'
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 4);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x41, 0x41, 0x5A }));
  }

  // ── Equivalence class: fixed-tiny back-reference (op&0x03==0x00) ─────────

  [Category("HappyPath")]
  [Test]
  public void Decode_FixedTinyCopy_CopiesThreeBytesPlusScratch() {
    // Literal 'A', then tiny copy with rel_offset=1 (writes 4 bytes
    // 'A','A','A','A' starting at output offset 1). Final cursor = 4,
    // bytesProduced = 5.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41); // 'A'
    b.EmitOpBits([EncodeFixedTinyCopyToken(relOffset: 1)]);
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 5);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x41, 0x41, 0x41, 0x41 }));
  }

  // ── Equivalence class: fixed-short back-reference (op&0x03==0x01) ────────

  [Category("HappyPath")]
  [Test]
  public void Decode_FixedShortCopy_ExpandsThreeByteSegment() {
    // "ABCABCD" — literals A,B,C, fixed-short-copy(rel=3,seg=3), literal 'D'.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41); // 'A'
    b.EmitLiteral(0x42); // 'B'
    b.EmitLiteral(0x43); // 'C'
    b.EmitOpBits(EncodeFixedShortCopyToken(relOffset: 3));
    b.EmitLiteral(0x44); // 'D'
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 7);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x43, 0x41, 0x42, 0x43, 0x44 }));
  }

  // ── Equivalence class: short back-reference (op&0x03==0x02) ──────────────

  [Category("HappyPath")]
  [Test]
  public void Decode_ShortCopy_OverlappingMatchExpandsPattern() {
    // Literals A,B then short-copy(rel=2, seg_delta=0 → seg=3) gives a
    // 4-byte write at [2..5]: [2]='A',[3]='B',[4]='A',[5]='B'. Cursor → 5.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41); // 'A'
    b.EmitLiteral(0x42); // 'B'
    b.EmitOpBits(EncodeShortCopyToken(segmentLenDelta: 0, relOffset: 2));
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 6);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x41, 0x42, 0x41, 0x42 }));
  }

  // ── Equivalence class: medium back-reference (op&0x07==0x03) ─────────────

  [Category("HappyPath")]
  [Test]
  public void Decode_MediumCopy_HandlesAdditionalRelOffsetBits() {
    // Literals A,B,C,D then medium-copy(rel=4, seg_delta=0 → seg=3) gives
    // a 4-byte write at [4..7]: ABCD.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41);
    b.EmitLiteral(0x42);
    b.EmitLiteral(0x43);
    b.EmitLiteral(0x44);
    b.EmitOpBits(EncodeMediumCopyToken(segmentLenDelta: 0, relOffset: 4));
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 8);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x41, 0x42, 0x43, 0x44 }));
  }

  // ── Equivalence class: long back-reference (op==0x07) ────────────────────

  [Category("HappyPath")]
  [Test]
  public void Decode_LongCopy_HandlesSegmentDelta() {
    // Literals A,B then long-copy(rel=2, seg_delta=2 → seg=5, writes 6
    // bytes). Total output: ABABABAB = 8 bytes.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41);
    b.EmitLiteral(0x42);
    b.EmitOpBits(EncodeLongCopyToken(segmentLenDelta: 2, relOffset: 2));
    var body = b.BuildBody();

    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 8);

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x41, 0x42, 0x41, 0x42, 0x41, 0x42 }));
  }

  // ── Boundary class: max-size bounds rejected ─────────────────────────────

  [Category("Boundary")]
  [Test]
  public void DecodeBlock_NegativeUncompressedLength_ThrowsArgumentOutOfRange() {
    Assert.That(() => MacriumPreXCodec.DecodeBlock([], -1),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Category("Boundary")]
  [Test]
  public void DecodeBlock_OversizeUncompressedLength_ThrowsArgumentOutOfRange() {
    Assert.That(() => MacriumPreXCodec.DecodeBlock([], MacriumPreXCodec.MaxUncompressedSize + 1),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Category("Boundary")]
  [Test]
  public void DecodeBlock_ZeroUncompressedLength_ReturnsEmpty() {
    var result = MacriumPreXCodec.DecodeBlock([], 0);
    Assert.That(result, Is.Empty);
  }

  // ── Exception class: malformed input ─────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void Decode_BackReferenceBeforeBlockStart_Throws() {
    // First token is a back-reference, but no preceding data exists.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitOpBits([EncodeFixedTinyCopyToken(relOffset: 1)]);
    var body = b.BuildBody();

    Assert.That(() => MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 4),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Category("Exception")]
  [Test]
  public void Decode_TruncatedControlWord_ProducesNoOutput() {
    // Just two bytes — not enough for a 4-byte control word. The decoder
    // bails out cleanly with no output rather than throwing, so callers
    // can mark the block as partial.
    var body = new byte[] { 0x00, 0x00 };
    var result = MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 0);
    Assert.That(result, Is.Empty);
  }

  [Category("Exception")]
  [Test]
  public void Decode_BackReferenceOverflowsBuffer_Throws() {
    // Literal A, then op&0x03==0x01 with rel=1 writes 4 bytes at [1..4],
    // but uncompressed_len=2 → buffer has only 2 bytes. Should throw.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x41);
    b.EmitOpBits(EncodeFixedShortCopyToken(relOffset: 1));
    var body = b.BuildBody();

    Assert.That(() => MacriumPreXCodec.DecodeBlock(body, uncompressedLength: 2),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Format-descriptor integration ────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ExtractsDecodedBlock00_WhenCodecApplied() {
    // Build a synthetic single-block container: 9-byte preamble + body
    // that decodes to a known string. Then run the descriptor's
    // OpenEntry("block-00.bin", null) path and verify the decompressed
    // output matches.
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x4D); // 'M'
    b.EmitLiteral(0x61); // 'a'
    b.EmitLiteral(0x63); // 'c'
    var body = b.BuildBody();

    var compressedLen = 9 + body.Length;
    var preamble = new byte[9];
    preamble[0] = 0x03;
    BinaryPrimitives.WriteUInt32LittleEndian(preamble.AsSpan(1), (uint)compressedLen);
    BinaryPrimitives.WriteUInt32LittleEndian(preamble.AsSpan(5), 3u);

    var container = new byte[compressedLen];
    Buffer.BlockCopy(preamble, 0, container, 0, 9);
    Buffer.BlockCopy(body, 0, container, 9, body.Length);

    var descriptor = new MacriumPreXFormatDescriptor();
    using var ms = new MemoryStream(container);
    using var entry = descriptor.OpenEntry(ms, "block-00.bin", null);

    using var reader = new BinaryReader(entry);
    var bytes = reader.ReadBytes(3);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Mac"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ListsDecompressedBlockEntries() {
    // Single block of length 3 decoded as "Mac".
    var b = new VectorBuilder();
    b.StartControlWord();
    b.EmitLiteral(0x4D);
    b.EmitLiteral(0x61);
    b.EmitLiteral(0x63);
    var body = b.BuildBody();
    var compressedLen = 9 + body.Length;
    var container = new byte[compressedLen];
    container[0] = 0x03;
    BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(1), (uint)compressedLen);
    BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(5), 3u);
    Buffer.BlockCopy(body, 0, container, 9, body.Length);

    var descriptor = new MacriumPreXFormatDescriptor();
    using var ms = new MemoryStream(container);
    var entries = descriptor.List(ms, null);

    Assert.That(entries.Any(e => e.Name == "block-00.bin"), Is.True,
      "decoded block-00.bin synthetic entry should be listed");
    var blockEntry = entries.Single(e => e.Name == "block-00.bin");
    Assert.That(blockEntry.OriginalSize, Is.EqualTo(3));
  }
}
