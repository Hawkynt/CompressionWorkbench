using Compression.Registry;
using FileFormat.Ghost;

namespace Compression.Tests.Ghost;

/// <summary>
/// Acceptance gate for <see cref="GhostFormatDescriptor"/> after promotion
/// from Stage-0 detection-only to R/W for the Ghost 11.x / 12.x record
/// container.
/// </summary>
/// <remarks>
/// <para>
/// <b>What's verified.</b> Descriptor surface (capabilities + magic + family),
/// modern-container parse, self-round-trip for all four compression-mode
/// equivalence classes (stored, FastLZ Z1, zlib Z3, zlib Z9), encrypted
/// round-trip, FastLZ codec correctness on synthetic inputs, version-gate
/// behaviour for legacy Ghost 4-7 shaped headers, plus the usual boundary
/// + exceptional cases (truncated header, empty stream, null stream,
/// missing entry name).
/// </para>
/// <para>
/// <b>What's not verified.</b> Interop with real Symantec-produced .gho
/// files — no public corpus exists. The honest treatment guard
/// (see <see cref="Descriptor_DocumentsScopeAcrossLineage"/>) ensures
/// the description keeps surfacing the scope statement so users with
/// legacy backups are still directed to Ghost Explorer.
/// </para>
/// </remarks>
[TestFixture]
public class GhostFormatTests {

  private static byte[] BuildSampleImage(byte compression, string? password = null, int partitionSize = 1024) {
    using var ms = new MemoryStream();
    using (var w = new GhostWriter(ms, compression, password: password, leaveOpen: true)) {
      var track0 = new byte[512];
      for (var i = 0; i < track0.Length; i++) track0[i] = (byte)(i & 0xFF);
      w.WriteTrack0(track0, sectors: 63);

      var part = new byte[partitionSize];
      for (var i = 0; i < part.Length; i++) part[i] = (byte)((i * 7 + 13) & 0xFF);
      w.WritePartition(part);

      w.WriteEnd();
    }
    return ms.ToArray();
  }

  // ── Descriptor surface ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_HasExpectedSurface() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ghost"));
    Assert.That(d.DisplayName, Does.Contain("Ghost"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".gho"));
    Assert.That(d.Extensions, Is.EquivalentTo(new[] { ".gho", ".ghs" }));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "Ghost 3.0+ is in-place R/W via the record-stream append flow exposed by GhostInPlaceModifier.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCreateAndPasswordCapabilities() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsPassword), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "Ghost 3.0+ is in-place R/W via the record-stream append flow.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DocumentsScopeAcrossLineage() {
    var d = new GhostFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("11.x"));
    Assert.That(desc, Does.Contain("3.0").Or.Contain("lineage"));
    Assert.That(desc, Does.Contain("pkware").Or.Contain("\"old\" compression"));
  }

  [Test, Category("HappyPath")]
  public void Detector_AdvertisesFeefMagic() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xFE, 0xEF }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  // ── Modern container parse ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_ParsesModernContainerAndExposesPartition() {
    var image = BuildSampleImage(GhostConstants.CompressionFast);
    var d = new GhostFormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, password: null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("track0.bin"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("partition1.bin"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ClassifiesModernContainerHint() {
    var image = BuildSampleImage(GhostConstants.CompressionFast);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.Modern11Plus));
  }

  // ── Round trip: each compression mode ─────────────────────────────

  [TestCase(GhostConstants.CompressionNone)]
  [TestCase(GhostConstants.CompressionFast)]
  [TestCase(GhostConstants.CompressionHigh3)]
  [TestCase(GhostConstants.CompressionHigh9)]
  [Category("HappyPath")]
  public void RoundTrip_PartitionBytesAreByteIdentical(byte compression) {
    var image = BuildSampleImage(compression, partitionSize: 8192);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    var part = r.Entries.FirstOrDefault(e => e.Name == "partition1.bin");
    Assert.That(part, Is.Not.Null, $"partition1.bin missing for compression={compression}");

    var expected = new byte[8192];
    for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 7 + 13) & 0xFF);
    Assert.That(part!.Data, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargePartitionSpanningMultipleBlocks() {
    // 100 KB > 32 KB block size so this exercises multi-block accumulation.
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 100_000);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    var part = r.Entries.First(e => e.Name == "partition1.bin");
    Assert.That(part.Data.Length, Is.EqualTo(100_000));

    var expected = new byte[100_000];
    for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 7 + 13) & 0xFF);
    Assert.That(part.Data, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_EncryptedPartitionDecryptsWithCorrectPassword() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, password: "hunter2", partitionSize: 2048);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms, password: "hunter2");
    var part = r.Entries.First(e => e.Name == "partition1.bin");
    Assert.That(r.IsEncrypted, Is.True);

    var expected = new byte[2048];
    for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 7 + 13) & 0xFF);
    Assert.That(part.Data, Is.EqualTo(expected));
  }

  [Test, Category("ExceptionalCase")]
  public void RoundTrip_EncryptedImageWithoutPasswordSurfacesError() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, password: "hunter2", partitionSize: 1024);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms); // No password supplied.
    Assert.That(r.IsEncrypted, Is.True);
    var err = r.Entries.FirstOrDefault(e => e.Name == "partition1.error.txt");
    Assert.That(err, Is.Not.Null, "Expected partition1.error.txt diagnostic when password missing.");
  }

  // ── Descriptor end-to-end via Create + List + Extract ─────────────

  [Test, Category("HappyPath")]
  public void Create_RoundTripsViaDescriptor() {
    var d = (IFormatDescriptor)new GhostFormatDescriptor();
    var creatable = (IArchiveCreatable)d;
    var part = new byte[4096];
    for (var i = 0; i < part.Length; i++) part[i] = (byte)((i * 3) & 0xFF);

    using var outMs = new MemoryStream();
    creatable.Create(outMs, [
      ArchiveInputInfo.InMemory("track0.bin", new byte[512]),
      ArchiveInputInfo.InMemory("partition1.bin", part)
    ], new FormatCreateOptions { MethodName = "fastlz" });

    outMs.Position = 0;
    var ops = (IArchiveFormatOperations)d;
    var entries = ops.List(outMs, password: null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("partition1.bin"));

    outMs.Position = 0;
    var bytes = ops.ExtractEntryToMemory(outMs, "partition1.bin", password: null);
    Assert.That(bytes, Is.EqualTo(part));
  }

  [Test, Category("HappyPath")]
  public void Create_StoredModeRoundTrips() {
    var d = (IArchiveCreatable)new GhostFormatDescriptor();
    var part = new byte[2048];
    for (var i = 0; i < part.Length; i++) part[i] = (byte)(i ^ 0x5A);
    using var outMs = new MemoryStream();
    d.Create(outMs, [ArchiveInputInfo.InMemory("partition1.bin", part)],
      new FormatCreateOptions { MethodName = "stored" });

    outMs.Position = 0;
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    var bytes = ops.ExtractEntryToMemory(outMs, "partition1.bin", password: null);
    Assert.That(bytes, Is.EqualTo(part));
  }

  [Test, Category("HappyPath")]
  public void Create_WithPasswordRoundTripsViaDescriptor() {
    var d = (IArchiveCreatable)new GhostFormatDescriptor();
    var part = new byte[1500];
    for (var i = 0; i < part.Length; i++) part[i] = (byte)(i);
    using var outMs = new MemoryStream();
    d.Create(outMs, [ArchiveInputInfo.InMemory("partition1.bin", part)],
      new FormatCreateOptions { MethodName = "zlib-6", Password = "secret" });

    outMs.Position = 0;
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    var bytes = ops.ExtractEntryToMemory(outMs, "partition1.bin", password: "secret");
    Assert.That(bytes, Is.EqualTo(part));
  }

  [Test, Category("ExceptionalCase")]
  public void Create_UnknownCompressionMethodIsRejected() {
    var d = (IArchiveCreatable)new GhostFormatDescriptor();
    using var outMs = new MemoryStream();
    Assert.That(() => d.Create(outMs, [ArchiveInputInfo.InMemory("partition1.bin", [1, 2, 3])],
        new FormatCreateOptions { MethodName = "nonsense" }),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Legacy version gate (Ghost 4-7 / unknown FE EF) ────────────────

  [Test, Category("BoundaryCase")]
  public void Reader_VersionGatesUnknownFeEfShapedHeader() {
    // FE EF with an unknown head-type byte (0xFF) — neither modern record
    // container nor pre-3.0 Ghost dump (which uses head types 0x01/0x02/0x03).
    // Our parser must fall through to the Stage-0 diagnostic surface.
    var legacy = new byte[1024];
    legacy[0] = 0xFE;
    legacy[1] = 0xEF;
    legacy[2] = 0xFF; // unknown head type
    for (var i = 3; i < legacy.Length; i++) legacy[i] = (byte)(i & 0xFF);
    using var ms = new MemoryStream(legacy);
    var d = new GhostFormatDescriptor();
    var entries = d.List(ms, password: null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("ghost-image.gho.bin"));

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PossiblyLegacy4To7));
  }

  [Test, Category("BoundaryCase")]
  public void Reader_LegacyHeaderMetadataIncludesGuidanceToGhostExplorer() {
    var legacy = new byte[1024];
    legacy[0] = 0xFE;
    legacy[1] = 0xEF;
    using var ms = new MemoryStream(legacy);
    var r = new GhostReader(ms);
    var meta = r.Entries.First(e => e.Name == "metadata.ini");
    var text = System.Text.Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("parse_status=detection-only"));
    Assert.That(text, Does.Contain("Ghost Explorer").IgnoreCase
      .Or.Contain("ghostexp").IgnoreCase);
  }

  // ── Pre-3.0 (Ghost 1.x / 2.x DOS) Stage-1 promotion ───────────────

  /// <summary>
  /// Build a synthetic pre-3.0 Ghost dump matching the Ghost 1.6 dump layout:
  /// 512-byte head with FE EF magic
  /// at offset 0, head-type byte at offset 2, zero-padded to 512 bytes, then
  /// a body of arbitrary bytes.
  /// </summary>
  private static byte[] BuildLegacyDump(byte headType, int bodyLen = 2048) {
    var image = new byte[GhostLegacyConstants.DumpHeadSize + bodyLen];
    image[0] = GhostLegacyConstants.MagicByte0;
    image[1] = GhostLegacyConstants.MagicByte1;
    image[GhostLegacyConstants.HeadTypeOffset] = headType;
    // Body bytes: just a deterministic pattern so we can verify byte-identity
    // when the body is surfaced.
    for (var i = 0; i < bodyLen; i++)
      image[GhostLegacyConstants.DumpHeadSize + i] = (byte)((i * 5 + 17) & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Reader_RecognisesPre30HeadType1() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypeFirst);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PreModern1And2));
    Assert.That(r.LegacyHeadType, Is.EqualTo(GhostLegacyConstants.HeadTypeFirst));
  }

  [Test, Category("HappyPath")]
  public void Reader_RecognisesPre30HeadType2() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypePartition);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PreModern1And2));
    Assert.That(r.LegacyHeadType, Is.EqualTo(GhostLegacyConstants.HeadTypePartition));
  }

  [Test, Category("HappyPath")]
  public void Reader_RecognisesPre30HeadType3() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypeBoot);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PreModern1And2));
    Assert.That(r.LegacyHeadType, Is.EqualTo(GhostLegacyConstants.HeadTypeBoot));
  }

  [Test, Category("HappyPath")]
  public void Reader_Pre30_SurfacesDumpHeadAndBodyVerbatim() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypeFirst, bodyLen: 4096);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    var head = r.Entries.FirstOrDefault(e => e.Name == "dump-head.bin");
    var body = r.Entries.FirstOrDefault(e => e.Name == "dump-body.bin");
    Assert.That(head, Is.Not.Null);
    Assert.That(body, Is.Not.Null);
    Assert.That(head!.Data.Length, Is.EqualTo(GhostLegacyConstants.DumpHeadSize));
    Assert.That(body!.Data.Length, Is.EqualTo(4096));
    Assert.That(head.Data.AsSpan(0, 3).ToArray(),
      Is.EqualTo(new byte[] { 0xFE, 0xEF, GhostLegacyConstants.HeadTypeFirst }));
    // Body must be byte-identical to the synthesised payload.
    for (var i = 0; i < 4096; i++)
      Assert.That(body.Data[i], Is.EqualTo((byte)((i * 5 + 17) & 0xFF)));
  }

  [Test, Category("HappyPath")]
  public void Reader_Pre30_MetadataDocumentsScope() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypeFirst);
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    var meta = r.Entries.First(e => e.Name == "metadata.ini");
    var text = System.Text.Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("pre-3.0").IgnoreCase);
    Assert.That(text, Does.Contain("stage=1"));
    Assert.That(text, Does.Contain("parse_status=ok"));
    Assert.That(text, Does.Contain("dump_head_type=0x01"));
    Assert.That(text, Does.Contain("first_record"));
    Assert.That(text, Does.Contain("Ghost 1.x").IgnoreCase);
  }

  [Test, Category("HappyPath")]
  public void List_Pre30_SurfacesAllStageOneEntries() {
    var image = BuildLegacyDump(GhostLegacyConstants.HeadTypePartition);
    using var ms = new MemoryStream(image);
    var d = new GhostFormatDescriptor();
    var entries = d.List(ms, password: null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("dump-head.bin"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("dump-body.bin"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("ghost-image.gho.bin"),
      "Stage-1 promotion must remove the Stage-0 raw-fallback entry.");
  }

  [Test, Category("BoundaryCase")]
  public void LegacyDetector_RejectsFeEfWhenModernRecordMagicPresent() {
    // FE EF + head-type-shaped byte at offset 2, but 0x012F18D8 appears
    // somewhere in the file → must NOT classify as pre-3.0 (modern wins).
    var image = new byte[2048];
    image[0] = 0xFE; image[1] = 0xEF; image[2] = 0x01;
    // Plant the modern record magic deep in the body.
    var off = 1024;
    image[off + 4] = 0xD8; image[off + 5] = 0x18;
    image[off + 6] = 0x2F; image[off + 7] = 0x01;
    Assert.That(GhostLegacyReader.LooksLikeLegacyHeader(image), Is.False);
  }

  [Test, Category("BoundaryCase")]
  public void LegacyDetector_RejectsFeEfWithUnknownHeadType() {
    var image = new byte[GhostLegacyConstants.DumpHeadSize + 16];
    image[0] = 0xFE; image[1] = 0xEF; image[2] = 0x42; // unknown head type
    Assert.That(GhostLegacyReader.LooksLikeLegacyHeader(image), Is.False);
  }

  [Test, Category("BoundaryCase")]
  public void LegacyDetector_RejectsBytesTooShortForHead() {
    var image = new byte[] { 0xFE, 0xEF, 0x01 }; // shorter than 512 bytes
    Assert.That(GhostLegacyReader.LooksLikeLegacyHeader(image), Is.False);
  }

  [Test, Category("BoundaryCase")]
  public void LegacyDetector_RejectsWrongMagic() {
    var image = new byte[GhostLegacyConstants.DumpHeadSize + 16];
    image[0] = 0x4D; image[1] = 0x5A; // MZ — wrong magic
    image[2] = 0x01;
    Assert.That(GhostLegacyReader.LooksLikeLegacyHeader(image), Is.False);
  }

  [Test, Category("HappyPath")]
  public void LegacyConstants_PinFormatValues() {
    // These constants are the pre-3.0 dump-format values. Any change to
    // them must update the GhostLegacyReader XML-doc references too.
    Assert.That(GhostLegacyConstants.MagicByte0, Is.EqualTo((byte)0xFE),
      "Magic byte 0 is 0xFE.");
    Assert.That(GhostLegacyConstants.MagicByte1, Is.EqualTo((byte)0xEF),
      "Magic byte 1 is 0xEF.");
    Assert.That(GhostLegacyConstants.DumpHeadSize, Is.EqualTo(512),
      "The dump head is 512 bytes.");
    Assert.That(GhostLegacyConstants.HeadTypeOffset, Is.EqualTo(2),
      "The head type sits at offset 2.");
    Assert.That(GhostLegacyConstants.HeadTypeFirst, Is.EqualTo((byte)0x01));
    Assert.That(GhostLegacyConstants.HeadTypePartition, Is.EqualTo((byte)0x02));
    Assert.That(GhostLegacyConstants.HeadTypeBoot, Is.EqualTo((byte)0x03));
  }

  [Test, Category("ExceptionalCase")]
  public void LegacyReader_HandlesEmptyAndShortPayloads() {
    // Just the 512-byte head and nothing else.
    var image = new byte[GhostLegacyConstants.DumpHeadSize];
    image[0] = 0xFE; image[1] = 0xEF; image[2] = 0x01;
    using var ms = new MemoryStream(image);
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.PreModern1And2));
    // No body entry because there are no bytes past the head.
    Assert.That(r.Entries.Select(e => e.Name), Does.Not.Contain("dump-body.bin"));
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("dump-head.bin"));
  }

  [Test, Category("HappyPath")]
  public void Reader_FlagsSpannedSegmentRoleWhenRequested() {
    // Spanned segment of arbitrary bytes (no modern container present).
    var legacy = new byte[256];
    legacy[0] = 0x12; legacy[1] = 0x34;
    using var ms = new MemoryStream(legacy);
    var r = new GhostReader(ms, isSpannedSegment: true);
    Assert.That(r.LikelySpannedSegment, Is.True);
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("ghost-image.ghs.bin"));
  }

  // ── OpenEntry ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsPartitionBytes() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream(image);
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    using var stream = ops.OpenEntry(ms, "partition1.bin", password: null);
    Assert.That(stream.Length, Is.EqualTo(1024));
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    var expected = new byte[1024];
    for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 7 + 13) & 0xFF);
    Assert.That(copy.ToArray(), Is.EqualTo(expected));
  }

  [Test, Category("ExceptionalCase")]
  public void OpenEntry_ThrowsForUnknownEntryName() {
    var image = BuildSampleImage(GhostConstants.CompressionFast);
    using var ms = new MemoryStream(image);
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    Assert.That(() => ops.OpenEntry(ms, "no-such-entry.bin", password: null),
      Throws.InstanceOf<FileNotFoundException>());
  }

  // ── Boundary / exceptional ─────────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void Reader_RejectsTruncatedStream() {
    var image = new byte[] { 0xFE, 0xEF, 0x00 };
    using var ms = new MemoryStream(image);
    var d = new GhostFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsEmptyStream() {
    using var ms = new MemoryStream([]);
    var d = new GhostFormatDescriptor();
    Assert.That(() => d.List(ms, password: null), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_ThrowsOnNullStream() {
    Assert.That(() => new GhostReader(null!), Throws.InstanceOf<ArgumentNullException>());
  }
}

/// <summary>
/// Direct codec-level coverage for <see cref="GhostFastLz"/> — keeps the
/// round-trip guarantee independent of the rest of the container so a
/// regression in the codec surfaces precisely.
/// </summary>
[TestFixture]
public class GhostFastLzTests {

  [Test, Category("HappyPath")]
  public void Compress_Decompress_RoundTripsLiterals() {
    // Input small enough to take the "store uncompressed" path.
    var src = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    var compressed = GhostFastLz.Compress(src);
    Assert.That(compressed[0], Is.EqualTo(1), "Small input must be stored uncompressed.");

    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(compressed, compressed.Length, dst);
    Assert.That(dst[..n], Is.EqualTo(src));
  }

  [Test, Category("HappyPath")]
  public void Compress_Decompress_RoundTripsRepeatingPattern() {
    var src = new byte[2048];
    for (var i = 0; i < src.Length; i++) src[i] = (byte)(i % 16);
    var compressed = GhostFastLz.Compress(src);
    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(compressed, compressed.Length, dst);
    Assert.That(dst[..n], Is.EqualTo(src));
  }

  [Test, Category("HappyPath")]
  public void Compress_Decompress_RoundTripsPseudoRandomBytes() {
    var src = new byte[8192];
    var rng = new Random(42);
    rng.NextBytes(src);
    var compressed = GhostFastLz.Compress(src);
    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(compressed, compressed.Length, dst);
    Assert.That(dst[..n], Is.EqualTo(src));
  }

  [Test, Category("HappyPath")]
  public void Compress_Decompress_RoundTripsFullBlockSize() {
    var src = new byte[GhostConstants.BlockSize];
    var rng = new Random(7);
    rng.NextBytes(src);
    var compressed = GhostFastLz.Compress(src);
    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(compressed, compressed.Length, dst);
    Assert.That(n, Is.EqualTo(src.Length));
    Assert.That(dst, Is.EqualTo(src));
  }

  [Test, Category("BoundaryCase")]
  public void Compress_EmptyInputReturnsEmpty() {
    var compressed = GhostFastLz.Compress(ReadOnlySpan<byte>.Empty);
    Assert.That(compressed, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void StoreUncompressed_RoundTripsAllBytes() {
    var src = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
    var stored = GhostFastLz.StoreUncompressed(src);
    Assert.That(stored.Length, Is.EqualTo(src.Length + 4));
    Assert.That(stored[0], Is.EqualTo(1));

    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(stored, stored.Length, dst);
    Assert.That(dst[..n], Is.EqualTo(src));
  }

  [Test, Category("HappyPath")]
  public void Hash_IsDeterministic() {
    var h1 = GhostFastLz.Hash(0x12, 0x34, 0x56);
    var h2 = GhostFastLz.Hash(0x12, 0x34, 0x56);
    Assert.That(h2, Is.EqualTo(h1));
    Assert.That(h1, Is.InRange(0, 0xFFF));
  }
}

/// <summary>
/// Direct coverage for the CRC-16 stream cipher round trip.
/// </summary>
[TestFixture]
public class GhostCrc16CipherTests {

  [Test, Category("HappyPath")]
  public void RoundTripsArbitraryBytes() {
    var src = new byte[256];
    for (var i = 0; i < src.Length; i++) src[i] = (byte)i;
    var buf = (byte[])src.Clone();

    var enc = new GhostCrc16Cipher("p@ssw0rd!");
    enc.Encrypt(buf);
    Assert.That(buf, Is.Not.EqualTo(src));

    var dec = new GhostCrc16Cipher("p@ssw0rd!");
    dec.Decrypt(buf);
    Assert.That(buf, Is.EqualTo(src));
  }

  [Test, Category("ExceptionalCase")]
  public void EmptyPasswordRejected() {
    Assert.That(() => new GhostCrc16Cipher(""), Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("HappyPath")]
  public void WrongPasswordYieldsGarbage() {
    var src = new byte[64];
    for (var i = 0; i < src.Length; i++) src[i] = (byte)i;
    var buf = (byte[])src.Clone();
    new GhostCrc16Cipher("right").Encrypt(buf);
    new GhostCrc16Cipher("wrong").Decrypt(buf);
    Assert.That(buf, Is.Not.EqualTo(src));
  }
}

/// <summary>
/// On-disk-constant lock-in — pins the Ghost FE EF + 0x012F18D8 + FastLZ
/// sentinel + hash-table size constants (shared by Ghost 2003.789 and Ghost
/// 11.5.1) so any change to them fails a test, forcing the editor to confirm
/// the change is intentional.
/// </summary>
/// <remarks>
/// These tests do <em>not</em> verify behaviour — the behaviour is already
/// covered by <see cref="GhostFormatTests"/> and <see cref="GhostFastLzTests"/>.
/// </remarks>
[TestFixture]
public class GhostSpecLockInTests {

  [Test, Category("HappyPath")]
  public void FileMagic_IsPinned() {
    Assert.That(GhostConstants.FileMagic, Is.EqualTo(0xEFFE),
      "FE EF file magic is pinned.");
  }

  [Test, Category("HappyPath")]
  public void RecordMagic_IsPinned() {
    Assert.That(GhostConstants.RecordMagic, Is.EqualTo(0x012F18D8u),
      "0x012F18D8 record magic is pinned.");
  }

  [Test, Category("HappyPath")]
  public void RecordHeaderSize_IsPinned() {
    Assert.That(GhostConstants.RecordHeaderSize, Is.EqualTo(10),
      "10-byte record header is pinned.");
  }

  [Test, Category("HappyPath")]
  public void HeaderSize_IsPinned() {
    Assert.That(GhostConstants.HeaderSize, Is.EqualTo(512),
      "512-byte header is pinned.");
  }

  [Test, Category("HappyPath")]
  public void FastLzHashSize_IsPinned() {
    Assert.That(GhostConstants.FastLzHashSize, Is.EqualTo(4096),
      "4096-entry FastLZ hash table is pinned.");
  }

  [Test, Category("HappyPath")]
  public void EndRecordType_IsPinned() {
    Assert.That(GhostConstants.RecordTypeEnd, Is.EqualTo((ushort)0x0023),
      "End-record type 0x23 is pinned.");
  }

  [Test, Category("HappyPath")]
  public void CompressionDispatchBytes_ArePinned() {
    // Compression dispatch on the first byte of the compression header:
    //   0 = None (passthrough)
    //   1 = Old (not supported)
    //   2 = Fast (FastLZ)
    //   3..9 = High (zlib)
    Assert.That(GhostConstants.CompressionNone, Is.EqualTo(0),
      "Compression byte 0 = None.");
    Assert.That(GhostConstants.CompressionOld, Is.EqualTo(1),
      "Compression byte 1 = Old (PKWARE DCL, refused by Ghost 3.0+).");
    Assert.That(GhostConstants.CompressionFast, Is.EqualTo(2),
      "Compression byte 2 = Fast (FastLZ).");
    Assert.That(GhostConstants.CompressionHigh3, Is.EqualTo(3),
      "Compression byte 3 = High zlib Z3.");
    Assert.That(GhostConstants.CompressionHigh9, Is.EqualTo(9),
      "Compression byte 9 = High zlib Z9.");
  }

  [Test, Category("HappyPath")]
  public void FastLzHash_UsesPinnedConstants() {
    // The hash is ((b0 << 4 ^ b1) << 4 ^ b2) * 0x9E5F (equivalently the signed
    // -0x61A1); only bits (>> 4) & 0xFFF are used, so the overflow is harmless.
    //
    // We don't pin the exact hash output (it's an implementation detail of the
    // multiplication overflow), but we DO pin that two distinct triples produce
    // hashes in the 12-bit range and that the function is deterministic.
    var h_abc = GhostFastLz.Hash(0x61, 0x62, 0x63);
    var h_xyz = GhostFastLz.Hash(0x78, 0x79, 0x7A);
    Assert.That(h_abc, Is.InRange(0, 0xFFF),
      "FastLZ hash output must fit in 12 bits ('& 0xfff' mask).");
    Assert.That(h_xyz, Is.InRange(0, 0xFFF),
      "FastLZ hash output must fit in 12 bits ('& 0xfff' mask).");

    // Recompute the encoder-side constant by hand to confirm we haven't drifted.
    // The encoder writes ((b0 << 4 ^ b1) << 4 ^ b2) * MULTIPLIER, multiplier = 0x9E5F.
    const uint multiplier = 0x9E5Fu;
    var v = (uint)(0x63 ^ (16 * (0x62 ^ (16 * 0x61))));
    var expected = (int)((unchecked(multiplier * v) >> 4) & 0xFFF);
    Assert.That(h_abc, Is.EqualTo(expected),
      "GhostFastLz.Hash must use the 0x9E5F multiplier.");
  }

  [Test, Category("HappyPath")]
  public void FastLzUncompressedEscape_IsPinned() {
    // A first byte of 0x01 marks a raw copy of the payload, so
    // GhostFastLz.StoreUncompressed must set output[0] = 1.
    var stored = GhostFastLz.StoreUncompressed(new byte[] { 0xAA, 0xBB });
    Assert.That(stored[0], Is.EqualTo((byte)1),
      "First-byte 0x01 marks a raw (uncompressed) block.");
  }

  [Test, Category("HappyPath")]
  public void FastLzSentinelString_IsPinned() {
    // Every hash-table slot is seeded to point into the literal
    // "123456789012345678".
    //
    // Our codec uses the same literal byte sequence via Encoding.ASCII —
    // pull the field by exercising the codec with an input that *would*
    // dereference a sentinel-pointed slot and observing that decompression
    // produces the documented '1'..'8','1'..'8','1' prefix.
    //
    // Construct a minimal block: control word 0x0001 (one match token),
    // match token (b0=0, b1=0) → hash index 0, extra_len 0 → copy 3 bytes
    // from sentinel start "123".
    var block = new byte[] { 0, 0, 0, 0,   // 4-byte block header (tag != 1)
                              0x01, 0x00,   // control word: bit 0 = match, others = literal
                              0x00, 0x00 }; // match token: index 0, extra_len 0
    var dst = new byte[GhostConstants.BlockSize];
    var n = GhostFastLz.Decompress(block, block.Length, dst);
    Assert.That(n, Is.GreaterThanOrEqualTo(3),
      "Decoder must produce at least 3 bytes from a single match token.");
    Assert.That(dst[0], Is.EqualTo((byte)'1'),
      "Sentinel byte 0 must be ASCII '1' — the hash table is seeded with the literal " +
      "\"123456789012345678\".");
    Assert.That(dst[1], Is.EqualTo((byte)'2'),
      "Sentinel byte 1 must be ASCII '2'.");
    Assert.That(dst[2], Is.EqualTo((byte)'3'),
      "Sentinel byte 2 must be ASCII '3'.");
  }
}
