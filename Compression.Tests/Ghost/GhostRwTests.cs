using Compression.Registry;
using FileFormat.Ghost;

namespace Compression.Tests.Ghost;

/// <summary>
/// Acceptance gate for the Ghost descriptor's promotion from WORM
/// (Create-only) to R/W (Create + Add/Remove/Replace) via
/// <see cref="IArchiveModifiable"/> + <see cref="GhostModifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What's verified.</b> The IArchiveModifiable surface on the
/// descriptor (Add + Remove with the documented semantics), the
/// GhostModifier.Replace sugar, the password-protected modify path,
/// preservation of the FE EF + 0x012F18D8 record framing through a
/// mutation cycle, preservation of the CRC-16 stream cipher across
/// mutation (decrypted payload survives an Add/Remove), and the standard
/// boundary + exceptional cases (read-only stream rejection, pre-3.0
/// rejection, encrypted-without-password rejection).
/// </para>
/// <para>
/// <b>Strategy.</b> Modify is rebuild-based: read existing entries,
/// mutate the list, re-emit through <see cref="GhostWriter"/> with the
/// source image's compression mode + encryption state. This is the same
/// rebuild-based approach taken by ZIP / NTFS / Btrfs and acknowledged by
/// memory.md as acceptable; the alternative (per-record patching) would
/// need to rewrite every downstream offset because Ghost's record
/// framing has no length-of-payload field.
/// </para>
/// </remarks>
[TestFixture]
public class GhostRwTests {

  private static byte[] BuildSampleImage(
      byte compression = GhostConstants.CompressionFast,
      string? password = null,
      int partitionCount = 2,
      int partitionSize = 1024) {
    using var ms = new MemoryStream();
    using (var w = new GhostWriter(ms, compression, password: password, leaveOpen: true)) {
      var track0 = new byte[512];
      for (var i = 0; i < track0.Length; i++) track0[i] = (byte)(i & 0xFF);
      w.WriteTrack0(track0, sectors: 63);

      for (var p = 0; p < partitionCount; p++) {
        var part = new byte[partitionSize];
        for (var i = 0; i < part.Length; i++)
          part[i] = (byte)(((i + p * 31) * 7 + 13 + p) & 0xFF);
        w.WritePartition(part);
      }
      w.WriteEnd();
    }
    return ms.ToArray();
  }

  private static byte[] PatternBytes(int len, int seed) {
    var b = new byte[len];
    for (var i = 0; i < len; i++) b[i] = (byte)((i * 11 + seed * 17 + 3) & 0xFF);
    return b;
  }

  private static List<string> ListEntries(MemoryStream ms, string? password) {
    ms.Position = 0;
    var d = new GhostFormatDescriptor();
    return d.List(ms, password).Select(e => e.Name).ToList();
  }

  private static byte[] ExtractEntry(MemoryStream ms, string entryName, string? password) {
    ms.Position = 0;
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    return ops.ExtractEntryToMemory(ms, entryName, password);
  }

  // ── Descriptor surface ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsArchiveModifiable() {
    var d = new GhostFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DocumentsRwScopeAndRebuildStrategy() {
    var d = new GhostFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    // Must surface the new R/W scope and the rebuild-based approach.
    Assert.That(desc, Does.Contain("add/remove/replace").Or.Contain("modify"));
    Assert.That(desc, Does.Contain("rebuild"));
  }

  // ── Add: read existing → add → re-read → new entry present ────────

  [Test, Category("HappyPath")]
  public void Add_AppendsNewPartitionAndRoundTrips() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var newPart = PatternBytes(1500, seed: 9);
    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", newPart)]);

    var names = ListEntries(ms, password: null);
    Assert.That(names, Does.Contain("track0.bin"));
    Assert.That(names, Does.Contain("partition1.bin"));
    Assert.That(names, Does.Contain("partition2.bin"));

    var extracted = ExtractEntry(ms, "partition2.bin", null);
    Assert.That(extracted, Is.EqualTo(newPart));
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesExistingEntryBytes() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 2048);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Snapshot existing partition1 bytes before mutation.
    var beforeBytes = ExtractEntry(ms, "partition1.bin", null);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", PatternBytes(800, seed: 5))]);

    var afterBytes = ExtractEntry(ms, "partition1.bin", null);
    Assert.That(afterBytes, Is.EqualTo(beforeBytes),
      "Add must not mutate the bytes of unrelated existing entries.");
  }

  [Test, Category("HappyPath")]
  public void Add_WithReplacementByName_OverwritesExistingPayloadInPlace() {
    // GhostModifier.Add does in-place replacement when the input name matches
    // an existing entry — preserves Ghost's positional partitionN.bin labels
    // so callers don't see "partition1.bin"'s payload migrate to "partition2.bin"
    // just because they overwrote it.
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 2, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var replacement = PatternBytes(2000, seed: 42);
    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition1.bin", replacement)]);

    var extracted = ExtractEntry(ms, "partition1.bin", null);
    Assert.That(extracted, Is.EqualTo(replacement),
      "Add with an existing name must replace the payload in place (no duplicate, same positional label).");

    // Listing must not have a duplicate name.
    var names = ListEntries(ms, password: null);
    var partitionCount = names.Count(n => n.StartsWith("partition", StringComparison.OrdinalIgnoreCase)
                                          && n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    Assert.That(partitionCount, Is.EqualTo(2),
      "Replacement-by-Add must keep the partition count at 2, not introduce duplicates.");
  }

  // ── Remove: read existing → remove entry → re-read → entry gone ───

  [Test, Category("HappyPath")]
  public void Remove_DropsNamedEntryAndKeepsOthers() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 3, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var beforePart1 = ExtractEntry(ms, "partition1.bin", null);
    var beforePart3 = ExtractEntry(ms, "partition3.bin", null);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Remove(ms, ["partition2.bin"]);

    var names = ListEntries(ms, password: null);
    // partition2 is gone, the remaining two get renumbered as partition1/partition2.
    var partitionNames = names.Where(n => n.StartsWith("partition")
                                           && n.EndsWith(".bin")).OrderBy(n => n).ToList();
    Assert.That(partitionNames, Has.Count.EqualTo(2),
      "Remove must drop exactly one entry, leaving two partitions.");

    // The first remaining partition is the original partition1 (kept order).
    var newFirst = ExtractEntry(ms, partitionNames[0], null);
    Assert.That(newFirst, Is.EqualTo(beforePart1));

    var newSecond = ExtractEntry(ms, partitionNames[1], null);
    Assert.That(newSecond, Is.EqualTo(beforePart3),
      "Remove must preserve the byte-content of the remaining entries.");
  }

  [Test, Category("BoundaryCase")]
  public void Remove_NonExistentNameIsNoOp() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 512);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var beforeNames = ListEntries(ms, password: null).OrderBy(n => n).ToList();

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Remove(ms, ["definitely-not-there.bin"]);

    var afterNames = ListEntries(ms, password: null).OrderBy(n => n).ToList();
    Assert.That(afterNames, Is.EqualTo(beforeNames),
      "Removing an entry that doesn't exist must be a no-op for the listing.");
  }

  // ── Replace: round-trips with new content ─────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_SwapsPayloadAndPreservesOthers() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 2, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var untouched = ExtractEntry(ms, "partition2.bin", null);
    var replacement = PatternBytes(3000, seed: 99);

    ms.Position = 0;
    GhostModifier.Replace(ms, "partition1.bin", replacement);

    var afterPart1 = ExtractEntry(ms, "partition1.bin", null);
    var afterPart2 = ExtractEntry(ms, "partition2.bin", null);
    Assert.That(afterPart1, Is.EqualTo(replacement));
    Assert.That(afterPart2, Is.EqualTo(untouched));
  }

  [Test, Category("BoundaryCase")]
  public void Replace_OnMissingEntry_AddsIt() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 512);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var addOn = PatternBytes(600, seed: 7);

    ms.Position = 0;
    GhostModifier.Replace(ms, "partition2.bin", addOn);

    var added = ExtractEntry(ms, "partition2.bin", null);
    Assert.That(added, Is.EqualTo(addOn));
  }

  // ── FE EF + record framing integrity after mutation ───────────────

  [Test, Category("HappyPath")]
  public void Add_PreservesFeefAndRecordFraming() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", PatternBytes(900, seed: 11))]);

    var bytes = ms.ToArray();
    // FE EF magic at offset 0 must survive the rebuild.
    Assert.That(bytes[0], Is.EqualTo((byte)0xFE), "FE byte 0 lost after mutation.");
    Assert.That(bytes[1], Is.EqualTo((byte)0xEF), "EF byte 1 lost after mutation.");

    // The 0x012F18D8 record magic must still appear in the body (at least
    // one record header — at minimum Track0 + Partition + End).
    var recordMagicCount = 0;
    for (var i = 0; i <= bytes.Length - 4; i++) {
      if (bytes[i] == 0xD8 && bytes[i + 1] == 0x18
          && bytes[i + 2] == 0x2F && bytes[i + 3] == 0x01)
        recordMagicCount++;
    }
    Assert.That(recordMagicCount, Is.GreaterThanOrEqualTo(3),
      "Rebuild must emit at least Track0 + 2 Partition + End record magics.");

    // Parses back as Modern11Plus.
    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.Modern11Plus));
  }

  [Test, Category("HappyPath")]
  public void Remove_PreservesFeefAndRecordFraming() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 3, partitionSize: 800);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Remove(ms, ["partition2.bin"]);

    var bytes = ms.ToArray();
    Assert.That(bytes[0], Is.EqualTo((byte)0xFE));
    Assert.That(bytes[1], Is.EqualTo((byte)0xEF));

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.GenerationHint, Is.EqualTo(GhostGenerationHint.Modern11Plus),
      "Rebuilt image must re-parse as a modern container.");
  }

  // ── Password-protected images round-trip through Add/Remove ───────

  [Test, Category("HappyPath")]
  public void Add_PasswordProtectedImage_PreservesCipherIntegrity() {
    const string password = "hunter2";
    var image = BuildSampleImage(GhostConstants.CompressionFast,
                                  password: password,
                                  partitionCount: 1,
                                  partitionSize: 1500);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var newPart = PatternBytes(2200, seed: 21);
    ms.Position = 0;
    GhostModifier.Add(ms,
      [ArchiveInputInfo.InMemory("partition2.bin", newPart)],
      password: password);

    // The rebuilt image must still self-report as encrypted.
    ms.Position = 0;
    var r = new GhostReader(ms, password: password);
    Assert.That(r.IsEncrypted, Is.True,
      "Encrypted source image must round-trip through Add as encrypted.");

    var part2 = ExtractEntry(ms, "partition2.bin", password);
    Assert.That(part2, Is.EqualTo(newPart));

    // The original encrypted entry's bytes must also still decrypt cleanly.
    var part1 = ExtractEntry(ms, "partition1.bin", password);
    var expected = new byte[1500];
    for (var i = 0; i < expected.Length; i++)
      expected[i] = (byte)(((i + 0 * 31) * 7 + 13 + 0) & 0xFF);
    Assert.That(part1, Is.EqualTo(expected),
      "Existing encrypted entry must decrypt to its original bytes after Add.");
  }

  [Test, Category("HappyPath")]
  public void Remove_PasswordProtectedImage_KeepsEncryptionAndOtherEntries() {
    const string password = "secret-pw";
    var image = BuildSampleImage(GhostConstants.CompressionHigh6,
                                  password: password,
                                  partitionCount: 2,
                                  partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    ms.Position = 0;
    GhostModifier.Remove(ms, ["partition1.bin"], password: password);

    ms.Position = 0;
    var r = new GhostReader(ms, password: password);
    Assert.That(r.IsEncrypted, Is.True);
    var names = r.Entries.Select(e => e.Name).ToList();
    var partitionNames = names.Where(n => n.StartsWith("partition")
                                           && n.EndsWith(".bin")).ToList();
    Assert.That(partitionNames, Has.Count.EqualTo(1),
      "Remove must drop one entry, leaving the surviving partition.");

    // The surviving partition's bytes must decrypt to the original partition2 content.
    var expectedPart2 = new byte[1024];
    for (var i = 0; i < expectedPart2.Length; i++)
      expectedPart2[i] = (byte)(((i + 1 * 31) * 7 + 13 + 1) & 0xFF);
    var got = ExtractEntry(ms, partitionNames[0], password);
    Assert.That(got, Is.EqualTo(expectedPart2));
  }

  [Test, Category("HappyPath")]
  public void Replace_PasswordProtectedImage_RoundTrips() {
    const string password = "p@ss!";
    var image = BuildSampleImage(GhostConstants.CompressionFast,
                                  password: password,
                                  partitionCount: 1,
                                  partitionSize: 1000);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var fresh = PatternBytes(1750, seed: 4);
    ms.Position = 0;
    GhostModifier.Replace(ms, "partition1.bin", fresh, password: password);

    ms.Position = 0;
    var r = new GhostReader(ms, password: password);
    Assert.That(r.IsEncrypted, Is.True);
    var got = ExtractEntry(ms, "partition1.bin", password);
    Assert.That(got, Is.EqualTo(fresh));
  }

  // ── Compression mode preservation ─────────────────────────────────

  [TestCase(GhostConstants.CompressionNone)]
  [TestCase(GhostConstants.CompressionFast)]
  [TestCase(GhostConstants.CompressionHigh3)]
  [TestCase(GhostConstants.CompressionHigh9)]
  [Category("HappyPath")]
  public void Add_PreservesSourceCompressionMode(byte compression) {
    var image = BuildSampleImage(compression, partitionCount: 1, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", PatternBytes(800, seed: 13))]);

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.HeaderCompression, Is.EqualTo(compression),
      $"Modify must preserve the source image's compression byte ({compression}).");
  }

  // ── Exceptional cases ──────────────────────────────────────────────

  [Test, Category("ExceptionalCase")]
  public void Add_OnEncryptedImageWithoutPassword_Throws() {
    var image = BuildSampleImage(GhostConstants.CompressionFast,
                                  password: "needed",
                                  partitionCount: 1,
                                  partitionSize: 512);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    ms.Position = 0;
    Assert.That(() => GhostModifier.Add(ms,
      [ArchiveInputInfo.InMemory("partition2.bin", new byte[] { 1, 2, 3 })], password: null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Add_OnPre30LegacyImage_ThrowsNotSupported() {
    // Pre-3.0 Ghost 1.x / 2.x DOS-era dump — modify must refuse so we don't
    // overwrite a Stage-1 R-only surface with bogus modern-container bytes.
    var legacy = new byte[GhostLegacyConstants.DumpHeadSize + 32];
    legacy[0] = 0xFE;
    legacy[1] = 0xEF;
    legacy[2] = GhostLegacyConstants.HeadTypeFirst;

    using var ms = new MemoryStream();
    ms.Write(legacy);
    ms.SetLength(legacy.Length);

    ms.Position = 0;
    Assert.That(() => GhostModifier.Add(ms,
      [ArchiveInputInfo.InMemory("anything.bin", new byte[] { 1 })]),
      Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Add_OnReadOnlyStream_Throws() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 256);
    // A non-writable (read-only) MemoryStream — Add must reject this up front.
    var ms = new MemoryStream(image, writable: false);
    Assert.That(() => GhostModifier.Add(ms,
      [ArchiveInputInfo.InMemory("partition2.bin", new byte[] { 1, 2 })]),
      Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Add_NullStream_ThrowsArgumentNull() {
    Assert.That(() => GhostModifier.Add(null!, [ArchiveInputInfo.InMemory("x.bin", new byte[] { 1 })]),
      Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Remove_NullEntryNames_ThrowsArgumentNull() {
    var image = BuildSampleImage(GhostConstants.CompressionFast);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;
    Assert.That(() => GhostModifier.Remove(ms, null!),
      Throws.InstanceOf<ArgumentNullException>());
  }

  // ── Synthetic-entry filtering ─────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void Add_DoesNotPersistMetadataIniAsAPayload() {
    // metadata.ini is synthesised by GhostReader for diagnostic surface; it
    // must not survive a rebuild as a partition entry.
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionCount: 1, partitionSize: 256);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    ms.Position = 0;
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", new byte[] { 1, 2, 3, 4 })]);

    // metadata.ini gets re-synthesised by the reader (it always appears for
    // modern images) — but the entry count of real payloads must be exactly
    // 1 track0 + 2 partitions, not 1 track0 + 2 partitions + a duplicated
    // metadata.ini-as-partition.
    var names = ListEntries(ms, null);
    var partitions = names.Where(n => n.StartsWith("partition")
                                       && n.EndsWith(".bin")).ToList();
    Assert.That(partitions, Has.Count.EqualTo(2));
    Assert.That(names.Count(n => n == "metadata.ini"), Is.EqualTo(1),
      "metadata.ini must appear exactly once (synthesised by reader, never re-emitted as a payload).");
  }
}
