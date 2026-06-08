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
/// (see <see cref="Descriptor_DocumentsScopeAndLegacyVersionGate"/>) ensures
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
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCreateAndPasswordCapabilities() {
    var d = new GhostFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsPassword), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DocumentsScopeAndLegacyVersionGate() {
    var d = new GhostFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("11.x"));
    Assert.That(desc, Does.Contain("legacy").Or.Contain("ghost 4-7"));
    Assert.That(desc, Does.Contain("ghost explorer").Or.Contain("ghostexp"));
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

  // ── Legacy version gate (Ghost 4-7) ───────────────────────────────

  [Test, Category("BoundaryCase")]
  public void Reader_VersionGatesLegacyShapedHeader() {
    // Ghost 4-7 signature shape: FE EF, but no 0x012F18D8 record magic anywhere.
    // Our parser must classify modern parse as failed and surface stage-0 metadata.
    var legacy = new byte[1024];
    legacy[0] = 0xFE;
    legacy[1] = 0xEF;
    for (var i = 2; i < legacy.Length; i++) legacy[i] = (byte)(i & 0xFF);
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
