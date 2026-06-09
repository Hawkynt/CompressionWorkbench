using System.Buffers.Binary;
using System.Text;
using FileFormat.EaseUs;

namespace Compression.Tests.EaseUs;

/// <summary>
/// Acceptance gate for the reverse-engineered EaseUS PBD on-disk
/// structure. Pins the four reverse-engineering deliverables that
/// promoted the format past R/O chunk-stream:
/// </summary>
/// <list type="bullet">
///   <item><description>
///     Header block is <c>0x4E8</c> (1256) bytes — not 12 — and the
///     <c>{magic, header_size, version} == {"IMGF", 0x000004E8,
///     0x00010001}</c> writer-side init must round-trip.
///   </description></item>
///   <item><description>
///     Trailer block is <c>0xC0</c> (192) bytes ending with a second
///     <c>"IMGF"</c> magic at trailer offset <c>0xBC</c>, preceded by
///     a <c>0xC0</c> size word at <c>+0xB8</c> and a <c>0x00010001</c>
///     version word at <c>+0xB4</c>.
///   </description></item>
///   <item><description>
///     The strict-form validators surface as <see cref="EaseUsReader.HeaderBlockFullyValidated"/>
///     and <see cref="EaseUsReader.TrailerBlockFullyValidated"/> so
///     downstream consumers can distinguish a real .pbd from a fuzz
///     hit that only happens to carry the magic.
///   </description></item>
///   <item><description>
///     The synthetic <c>metadata.ini</c> embeds the
///     <see cref="EaseUsContainerIndex.DescribeStructure"/> block so
///     forensic tools can diff the wire-protocol constants across
///     reader edits without re-reading the C# source.
///   </description></item>
/// </list>
/// <para>
/// Each fixture below builds the relevant block from hex-literal
/// bytes so the test will fail loudly if a future edit drifts the
/// offsets — the EaseUS engine itself enforces the exact same shape
/// in <c>CImgFile::CheckHeader</c>.
/// </para>
[TestFixture]
public class EaseUsContainerIndexTests {

  // ---------------------------------------------------------------------
  // Header structure constants — pinned to the writer-side init in
  // TBImageExplorer.exe (binary offsets 0x000CE913..0x000CE933).
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void HeaderBlockSize_Is_1256_Bytes_PerBinaryRe() {
    // CImgFile::CheckHeader (file_off 0x000CE170) issues a single
    // ReadFile(buf, 0x4E8) from file offset 0 then verifies
    // buf[0..4] == "IMGF". The header block is therefore 1256 bytes
    // — not the 12-byte slice the older reader assumed.
    Assert.That(EaseUsContainerIndex.HeaderBlockSize, Is.EqualTo(0x4E8));
    Assert.That(EaseUsContainerIndex.HeaderBlockSize, Is.EqualTo(1256));
  }

  [Test, Category("HappyPath")]
  public void HeaderFieldOffsets_PinFirstThreeWords() {
    Assert.Multiple(() => {
      Assert.That(EaseUsContainerIndex.HeaderMagicFieldOffset, Is.EqualTo(0));
      Assert.That(EaseUsContainerIndex.HeaderSizeFieldOffset, Is.EqualTo(4));
      Assert.That(EaseUsContainerIndex.HeaderVersionFieldOffset, Is.EqualTo(8));
    });
  }

  [Test, Category("HappyPath")]
  public void HeaderExpectedValues_MatchWriterSideConstants() {
    // Pinned by the c7 80 ... mov-imm32 sequence at binary offsets
    // 0x000CE913 (magic via push imm32), 0x000CE923 (version),
    // 0x000CE933 (header_size).
    Assert.Multiple(() => {
      Assert.That(EaseUsContainerIndex.HeaderSizeFieldExpectedValue, Is.EqualTo(0x000004E8u));
      Assert.That(EaseUsContainerIndex.HeaderVersionFieldExpectedValue, Is.EqualTo(0x00010001u));
    });
  }

  // ---------------------------------------------------------------------
  // Trailer structure constants — pinned to writer-side init at binary
  // offsets 0x000CE971 (magic), 0x000CE981 (version), 0x000CE991 (size).
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void TrailerBlockSize_Is_192_Bytes_PerBinaryRe() {
    // CImgFile::CheckHeader (file_off 0x000CE170) issues a
    // SetFilePointerEx(EOF-0xC0); ReadFile(buf, 0xC0) pair and then
    // verifies buf[0xBC..0xC0] == "IMGF".
    Assert.That(EaseUsContainerIndex.TrailerBlockSize, Is.EqualTo(0xC0));
    Assert.That(EaseUsContainerIndex.TrailerBlockSize, Is.EqualTo(192));
  }

  [Test, Category("HappyPath")]
  public void TrailerFieldOffsets_PinLastThreeWords() {
    Assert.Multiple(() => {
      Assert.That(EaseUsContainerIndex.TrailerVersionFieldOffset, Is.EqualTo(0xB4));
      Assert.That(EaseUsContainerIndex.TrailerSizeFieldOffset, Is.EqualTo(0xB8));
      Assert.That(EaseUsContainerIndex.TrailerMagicFieldOffset, Is.EqualTo(0xBC));
    });
  }

  [Test, Category("HappyPath")]
  public void TrailerExpectedValues_MatchWriterSideConstants() {
    Assert.Multiple(() => {
      Assert.That(EaseUsContainerIndex.TrailerSizeFieldExpectedValue, Is.EqualTo(0x000000C0u));
      Assert.That(EaseUsContainerIndex.TrailerVersionFieldExpectedValue, Is.EqualTo(0x00010001u));
    });
  }

  // ---------------------------------------------------------------------
  // INDX / VOLM / FDIR / RIND / FLTR sub-record magics — surfaced via
  // binary RE but not yet wired through the reader (they live behind
  // the header-bank zlib sub-streams at 0x98 and 0x10F).
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void SubRecordMagics_PinFourCharacterCodes() {
    Assert.Multiple(() => {
      Assert.That(EaseUsContainerIndex.IndxBlockMagic, Is.EqualTo("INDX"u8.ToArray()));
      Assert.That(EaseUsContainerIndex.VolmBlockMagic, Is.EqualTo("VOLM"u8.ToArray()));
      Assert.That(EaseUsContainerIndex.FdirBlockMagic, Is.EqualTo("FDIR"u8.ToArray()));
      Assert.That(EaseUsContainerIndex.RindBlockMagic, Is.EqualTo("RIND"u8.ToArray()));
      Assert.That(EaseUsContainerIndex.FltrRecordMagic, Is.EqualTo("FLTR"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void IndxEntrySize_Is_24_Bytes_PerBinaryRe() {
    // CImgFile::ReadIndx (file_off 0x000D1085) advances the iterator
    // by exactly 0x18 bytes per entry and tests the new offset against
    // [INDX_block + 8] (total-length cap).
    Assert.That(EaseUsContainerIndex.IndxEntrySize, Is.EqualTo(0x18));
    Assert.That(EaseUsContainerIndex.IndxEntrySize, Is.EqualTo(24));
    Assert.That(EaseUsContainerIndex.IndxBlockHeaderSize, Is.EqualTo(0x10));
    Assert.That(EaseUsContainerIndex.IndxEntryLengthMask, Is.EqualTo(0x3FFu));
  }

  // ---------------------------------------------------------------------
  // LooksLikeWellFormed* fail-soft validators — hex-literal fixtures.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void LooksLikeWellFormedHeader_AcceptsTheCanonicalWriterShape() {
    var header = new byte[EaseUsContainerIndex.HeaderBlockSize];
    // {"IMGF", 0x000004E8, 0x00010001} per binary offsets 0x000CE913..0x000CE933.
    Array.Copy("IMGF"u8.ToArray(), 0, header, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(
      header.AsSpan(EaseUsContainerIndex.HeaderSizeFieldOffset, 4),
      EaseUsContainerIndex.HeaderSizeFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      header.AsSpan(EaseUsContainerIndex.HeaderVersionFieldOffset, 4),
      EaseUsContainerIndex.HeaderVersionFieldExpectedValue);

    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedHeader(header), Is.True);
  }

  [Test, Category("HappyPath")]
  public void LooksLikeWellFormedHeader_AlsoAcceptsFimgVariant() {
    var header = new byte[EaseUsContainerIndex.HeaderBlockSize];
    Array.Copy("FIMG"u8.ToArray(), 0, header, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), EaseUsContainerIndex.HeaderSizeFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), EaseUsContainerIndex.HeaderVersionFieldExpectedValue);

    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedHeader(header), Is.True);
  }

  [Test, Category("ExceptionalPath")]
  public void LooksLikeWellFormedHeader_RejectsShortBuffer() {
    var tooShort = new byte[EaseUsContainerIndex.HeaderBlockSize - 1];
    Array.Copy("IMGF"u8.ToArray(), 0, tooShort, 0, 4);
    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedHeader(tooShort), Is.False);
  }

  [Test, Category("ExceptionalPath")]
  public void LooksLikeWellFormedHeader_RejectsWrongSizeField() {
    var header = new byte[EaseUsContainerIndex.HeaderBlockSize];
    Array.Copy("IMGF"u8.ToArray(), 0, header, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 0xDEADBEEFu);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), EaseUsContainerIndex.HeaderVersionFieldExpectedValue);
    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedHeader(header), Is.False);
  }

  [Test, Category("ExceptionalPath")]
  public void LooksLikeWellFormedHeader_RejectsWrongMagic() {
    var header = new byte[EaseUsContainerIndex.HeaderBlockSize];
    Array.Copy("XXXX"u8.ToArray(), 0, header, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), EaseUsContainerIndex.HeaderSizeFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), EaseUsContainerIndex.HeaderVersionFieldExpectedValue);
    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedHeader(header), Is.False);
  }

  [Test, Category("HappyPath")]
  public void LooksLikeWellFormedTrailer_AcceptsTheCanonicalWriterShape() {
    var trailer = new byte[EaseUsContainerIndex.TrailerBlockSize];
    BinaryPrimitives.WriteUInt32LittleEndian(
      trailer.AsSpan(EaseUsContainerIndex.TrailerVersionFieldOffset, 4),
      EaseUsContainerIndex.TrailerVersionFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      trailer.AsSpan(EaseUsContainerIndex.TrailerSizeFieldOffset, 4),
      EaseUsContainerIndex.TrailerSizeFieldExpectedValue);
    Array.Copy("IMGF"u8.ToArray(), 0, trailer, EaseUsContainerIndex.TrailerMagicFieldOffset, 4);

    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedTrailer(trailer), Is.True);
  }

  [Test, Category("ExceptionalPath")]
  public void LooksLikeWellFormedTrailer_RejectsWrongSize() {
    var trailer = new byte[EaseUsContainerIndex.TrailerBlockSize - 1];
    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedTrailer(trailer), Is.False);
  }

  [Test, Category("ExceptionalPath")]
  public void LooksLikeWellFormedTrailer_RejectsWrongMagicAtBC() {
    var trailer = new byte[EaseUsContainerIndex.TrailerBlockSize];
    BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0xB4, 4), 0x00010001u);
    BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0xB8, 4), 0x000000C0u);
    Array.Copy("ZZZZ"u8.ToArray(), 0, trailer, 0xBC, 4);
    Assert.That(EaseUsContainerIndex.LooksLikeWellFormedTrailer(trailer), Is.False);
  }

  // ---------------------------------------------------------------------
  // ComputeTrailerOffset
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void ComputeTrailerOffset_BacksOff_192_BytesAndPaddingFromEof() {
    // File of 0x2000 bytes with 0x10 trailing 0xFF padding bytes:
    //   trailer block ends at 0x2000 - 0x10 = 0x1FF0
    //   trailer block starts at 0x1FF0 - 0xC0 = 0x1F30
    Assert.That(EaseUsContainerIndex.ComputeTrailerOffset(0x2000, 0x10), Is.EqualTo(0x1F30));
  }

  [Test, Category("Boundary")]
  public void ComputeTrailerOffset_HandlesZeroPaddingCorrectly() {
    Assert.That(EaseUsContainerIndex.ComputeTrailerOffset(0x1000, 0), Is.EqualTo(0x1000 - 0xC0));
  }

  // ---------------------------------------------------------------------
  // DescribeStructure embedding — used by EaseUsReader.BuildMetadata.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void DescribeStructure_EmbedsAllPinnedConstantsAsKeyValueText() {
    var text = EaseUsContainerIndex.DescribeStructure();

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("header_block_size=1256"));
      Assert.That(text, Does.Contain("header_size_field_expected_value=0x000004E8"));
      Assert.That(text, Does.Contain("header_version_field_expected_value=0x00010001"));
      Assert.That(text, Does.Contain("trailer_block_size=192"));
      Assert.That(text, Does.Contain("trailer_magic_field_offset=188"));
      Assert.That(text, Does.Contain("trailer_size_field_expected_value=0x000000C0"));
      Assert.That(text, Does.Contain("indx_entry_size=24"));
      Assert.That(text, Does.Contain("indx_entry_length_mask=0x3FF"));
      Assert.That(text, Does.Contain("indx_block_header_size=16"));
    });
  }

  // ---------------------------------------------------------------------
  // End-to-end through EaseUsReader: a well-formed 0x4E8 header + 0xC0
  // trailer round-trip flips both the strict-form validation flags AND
  // surfaces the structure block in the metadata.ini entry.
  // ---------------------------------------------------------------------

  /// <summary>
  /// Builds a fully writer-side-compliant .pbd shell: 0x4E8 header
  /// (with the writer-pinned size + version words), an empty body
  /// (no zlib chunks — the chunk-stream scanner has its own coverage),
  /// and a 0xC0 trailer (with the writer-pinned size + version + magic).
  /// </summary>
  private static byte[] BuildWriterCompliantShell(string magic = "IMGF", int trailingFfPadding = 8) {
    var totalLength = EaseUsContainerIndex.HeaderBlockSize + 0x40 + EaseUsContainerIndex.TrailerBlockSize + trailingFfPadding;
    var buf = new byte[totalLength];

    // Header.
    Array.Copy(Encoding.ASCII.GetBytes(magic), 0, buf, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(EaseUsContainerIndex.HeaderSizeFieldOffset, 4),
      EaseUsContainerIndex.HeaderSizeFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(EaseUsContainerIndex.HeaderVersionFieldOffset, 4),
      EaseUsContainerIndex.HeaderVersionFieldExpectedValue);

    // Trailer at (totalLength - trailingFfPadding - 0xC0).
    var trailerOff = totalLength - trailingFfPadding - EaseUsContainerIndex.TrailerBlockSize;
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(trailerOff + EaseUsContainerIndex.TrailerVersionFieldOffset, 4),
      EaseUsContainerIndex.TrailerVersionFieldExpectedValue);
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(trailerOff + EaseUsContainerIndex.TrailerSizeFieldOffset, 4),
      EaseUsContainerIndex.TrailerSizeFieldExpectedValue);
    Array.Copy(Encoding.ASCII.GetBytes("IMGF"), 0, buf, trailerOff + EaseUsContainerIndex.TrailerMagicFieldOffset, 4);

    // 0xFF padding tail.
    for (var i = totalLength - trailingFfPadding; i < totalLength; i++)
      buf[i] = 0xFF;

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Reader_OnWriterCompliantShell_FlipsBothStrictFormFlags() {
    var img = BuildWriterCompliantShell();
    using var ms = new MemoryStream(img);

    var r = new EaseUsReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ValidHeader, Is.True, "magic at offset 0 must match");
      Assert.That(r.MagicVariant, Is.EqualTo("IMGF"));
      Assert.That(r.HeaderBlockFullyValidated, Is.True, "0x4E8 header block must validate strict-form");
      Assert.That(r.TrailerBlockFullyValidated, Is.True, "0xC0 trailer block must validate strict-form");
      Assert.That(r.TrailingFfPadding, Is.EqualTo(8));
      Assert.That(r.TrailerBlockOffset, Is.EqualTo(img.Length - 8 - EaseUsContainerIndex.TrailerBlockSize));
      Assert.That(r.TrailerImgfPresent, Is.True, "lenient trailer-scan must also see the magic");
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_OnWriterCompliantShell_EmbedsStructureBlockInMetadata() {
    var img = BuildWriterCompliantShell();
    using var ms = new MemoryStream(img);
    var r = new EaseUsReader(ms);

    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var metaText = Encoding.UTF8.GetString(meta.Data);

    Assert.Multiple(() => {
      Assert.That(metaText, Does.Contain("header_block_fully_validated=true"));
      Assert.That(metaText, Does.Contain("trailer_block_fully_validated=true"));
      Assert.That(metaText, Does.Contain("header_block_size=1256"));
      Assert.That(metaText, Does.Contain("trailer_block_size=192"));
      Assert.That(metaText, Does.Contain("indx_entry_size=24"));
    });
  }

  [Test, Category("ExceptionalPath")]
  public void Reader_OnLegacyMinimalImage_StrictFormFlagsRemainFalse() {
    // A "minimal" image — just the 12-byte IMGF magic + version words,
    // no full 0x4E8 header and no 0xC0 trailer — represents what the
    // previous-generation reader accepted. The strict-form flags must
    // stay false so downstream consumers can distinguish a real .pbd
    // from a fuzz hit / corrupted backup.
    var body = new List<byte>();
    body.AddRange("IMGF"u8.ToArray());
    body.AddRange(new byte[] { 0x2C, 0x05, 0x00, 0x00 });
    body.AddRange(new byte[] { 0x00, 0x00, 0x02, 0x00 });
    body.AddRange("IMGF"u8.ToArray());
    body.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
    using var ms = new MemoryStream(body.ToArray());

    var r = new EaseUsReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ValidHeader, Is.True, "old-style 12-byte magic still passes lenient validation");
      Assert.That(r.HeaderBlockFullyValidated, Is.False, "image is below the 0x4E8 header block size");
      Assert.That(r.TrailerBlockFullyValidated, Is.False, "no 0xC0 trailer block present");
    });
  }

  [Test, Category("Boundary")]
  public void Reader_OnFimgVariantShell_StrictFormFlagsStillFlip() {
    // FIMG (byte-reversed magic) ~15% of real-world files per
    // xyplorer.com community RE. Strict-form flags must still flip
    // when the rest of the header / trailer carries the writer-side
    // constants.
    var img = BuildWriterCompliantShell(magic: "FIMG");
    using var ms = new MemoryStream(img);
    var r = new EaseUsReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.MagicVariant, Is.EqualTo("FIMG"));
      Assert.That(r.HeaderBlockFullyValidated, Is.True);
      Assert.That(r.TrailerBlockFullyValidated, Is.True);
    });
  }
}
