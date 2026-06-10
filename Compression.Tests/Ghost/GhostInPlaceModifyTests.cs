using System.Text;
using Compression.Registry;
using FileFormat.Ghost;

namespace Compression.Tests.Ghost;

/// <summary>
/// Acceptance gates for <see cref="GhostInPlaceModifier"/> — the true
/// in-place Add / Replace / Remove flow that promotes the Ghost 3.0+ FE EF
/// record container from honest WORM to R/W.
/// </summary>
/// <remarks>
/// <para>
/// <b>Load-bearing.</b> The byte-identical-untouched-prefix gate is the
/// thing that turns a rebuild masquerading as R/W into honest R/W. The
/// snapshot tests in this fixture compare the on-disk bytes
/// <c>[0, original-end-record-offset)</c> before and after each mutation and
/// fail if a single byte drifts.
/// </para>
/// <para>
/// <b>Out of scope.</b> Encrypted in-place writes preserve the original
/// ciphertext at original offsets — that property is independent of cipher
/// state continuity because Ghost's cipher is per-partition-record (each
/// new partition reseeds from the password).
/// </para>
/// </remarks>
[TestFixture]
public class GhostInPlaceModifyTests {

  private static byte[] BuildSampleImage(byte compression, string? password = null,
      int partitionSize = 1024, bool writeTrack0 = true) {
    using var ms = new MemoryStream();
    using (var w = new GhostWriter(ms, compression, password: password, leaveOpen: true)) {
      if (writeTrack0) {
        var track0 = new byte[512];
        for (var i = 0; i < track0.Length; i++) track0[i] = (byte)(i & 0xFF);
        w.WriteTrack0(track0, sectors: 63);
      }
      var part = new byte[partitionSize];
      for (var i = 0; i < part.Length; i++) part[i] = (byte)((i * 7 + 13) & 0xFF);
      w.WritePartition(part);
      w.WriteEnd();
    }
    return ms.ToArray();
  }

  private static long EndRecordOffsetOf(byte[] image, string? password = null) {
    using var ms = new MemoryStream(image, writable: false);
    var r = new GhostReader(ms, password: password);
    return r.EndRecordOffset;
  }

  // ── Add ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_PreservesUntouchedPrefixByteIdentical() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 4096);
    var endOff = EndRecordOffsetOf(image);
    Assert.That(endOff, Is.GreaterThan(0), "test fixture must have a parseable end record");
    var prefix = image.AsSpan(0, (int)endOff).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var extra = new byte[2048];
    for (var i = 0; i < extra.Length; i++) extra[i] = (byte)(i ^ 0xAA);

    GhostInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", extra)]);

    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(prefix.Length), "Add must grow the file");
    Assert.That(after.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix),
      "[0, original-end-offset) bytes must stay byte-identical after Add.");
  }

  [Test, Category("HappyPath")]
  public void Add_NewPartitionIsReadableAfterMutation() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var extra = new byte[3000];
    for (var i = 0; i < extra.Length; i++) extra[i] = (byte)((i * 13 + 5) & 0xFF);
    GhostInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", extra)]);

    ms.Position = 0;
    var r = new GhostReader(ms);
    var p2 = r.Entries.FirstOrDefault(e => e.Name == "partition2.bin");
    Assert.That(p2, Is.Not.Null, "appended partition must be visible to the reader");
    Assert.That(p2!.Data, Is.EqualTo(extra), "appended partition data must round-trip");
  }

  [Test, Category("HappyPath")]
  public void Add_OriginalPartitionStillExtractable() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Add(ms,
      [ArchiveInputInfo.InMemory("partition2.bin", new byte[] { 1, 2, 3, 4 })]);

    ms.Position = 0;
    var r = new GhostReader(ms);
    var p1 = r.Entries.First(e => e.Name == "partition1.bin");
    var expected = new byte[1024];
    for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 7 + 13) & 0xFF);
    Assert.That(p1.Data, Is.EqualTo(expected));
  }

  // ── Replace ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_PreservesOriginalRecordBytesAtOriginalOffsets() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 4096);
    var endOff = EndRecordOffsetOf(image);
    var prefix = image.AsSpan(0, (int)endOff).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var fresh = Encoding.UTF8.GetBytes("replacement payload — original partition bytes must stay intact");
    GhostInPlaceModifier.Replace(ms, "partition1.bin", fresh);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix),
      "Original record bytes [0, end-record-offset) must be byte-identical after Replace.");
  }

  [Test, Category("HappyPath")]
  public void Replace_ReaderSurfacesNewContent() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var fresh = new byte[256];
    for (var i = 0; i < fresh.Length; i++) fresh[i] = (byte)(0xFF - i);
    GhostInPlaceModifier.Replace(ms, "partition1.bin", fresh);

    ms.Position = 0;
    var r = new GhostReader(ms);
    var p1 = r.Entries.First(e => e.Name == "partition1.bin");
    Assert.That(p1.Data, Is.EqualTo(fresh), "Reader must return replaced payload for partition1.bin.");
  }

  [Test, Category("HappyPath")]
  public void Replace_OfUnknownNameAddsSyntheticEntry() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 256);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var fresh = Encoding.UTF8.GetBytes("hello world");
    GhostInPlaceModifier.Replace(ms, "userdata.bin", fresh);

    ms.Position = 0;
    var r = new GhostReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == "userdata.bin");
    Assert.That(entry, Is.Not.Null, "Replace with an unknown name must surface a fresh annotation entry.");
    Assert.That(entry!.Data, Is.EqualTo(fresh));
  }

  // ── Remove ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_PreservesOriginalRecordBytesAtOriginalOffsets() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    var endOff = EndRecordOffsetOf(image);
    var prefix = image.AsSpan(0, (int)endOff).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Remove(ms, "partition1.bin");

    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix),
      "Original record bytes must be byte-identical after Remove tombstone append.");
  }

  [Test, Category("HappyPath")]
  public void Remove_ReaderTreatsEntryAsAbsent() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Remove(ms, "partition1.bin");

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "partition1.bin"), Is.False,
      "Remove tombstone must make the named entry disappear from the listing.");
    // track0.bin must still be present.
    Assert.That(r.Entries.Any(e => e.Name == "track0.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Remove_TombstoneIsAnnotationRecord() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Remove(ms, "partition1.bin");

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.Annotations, Has.Count.EqualTo(1));
    Assert.That(r.Annotations[0].Op, Is.EqualTo(GhostConstants.AnnotationOpRemove));
    Assert.That(r.Annotations[0].TargetName, Is.EqualTo("partition1.bin"));
  }

  // ── Encrypted image ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_OnEncryptedImage_PreservesCiphertextBytesByteIdentical() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, password: "hunter2", partitionSize: 2048);
    var endOff = EndRecordOffsetOf(image, password: "hunter2");
    var prefix = image.AsSpan(0, (int)endOff).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var extra = new byte[1024];
    for (var i = 0; i < extra.Length; i++) extra[i] = (byte)i;
    GhostInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", extra)],
      password: "hunter2");

    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix),
      "Encrypted image: ciphertext bytes [0, end-offset) must stay byte-identical after Add.");
  }

  [Test, Category("HappyPath")]
  public void Add_OnEncryptedImage_AppendedPartitionRoundTripsWithSamePassword() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, password: "hunter2", partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var extra = new byte[768];
    for (var i = 0; i < extra.Length; i++) extra[i] = (byte)(0x55 ^ i);
    GhostInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", extra)],
      password: "hunter2");

    ms.Position = 0;
    var r = new GhostReader(ms, password: "hunter2");
    Assert.That(r.IsEncrypted, Is.True);
    var p2 = r.Entries.First(e => e.Name == "partition2.bin");
    Assert.That(p2.Data, Is.EqualTo(extra),
      "Appended partition on an encrypted image must round-trip with the original password.");
  }

  // ── Codec equivalence classes ───────────────────────────────────────

  [TestCase(GhostConstants.CompressionNone)]
  [TestCase(GhostConstants.CompressionFast)]
  [TestCase(GhostConstants.CompressionHigh3)]
  [TestCase(GhostConstants.CompressionHigh9)]
  [Category("HappyPath")]
  public void Add_PreservesPrefixAcrossAllCompressionModes(byte compression) {
    var image = BuildSampleImage(compression, partitionSize: 2048);
    var endOff = EndRecordOffsetOf(image);
    var prefix = image.AsSpan(0, (int)endOff).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Add(ms,
      [ArchiveInputInfo.InMemory("partition2.bin", new byte[] { 9, 8, 7, 6, 5 })]);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix),
      $"compression={compression}: prefix must stay byte-identical after Add.");
  }

  // ── Pre-3.0 reject ──────────────────────────────────────────────────

  [Test, Category("ExceptionalCase")]
  public void Add_RejectsPre30Image_WithSpecificMessage() {
    // Pre-3.0 dump: FE EF + head-type byte 1 + 512-byte head + body, no record stream.
    var legacy = new byte[GhostLegacyConstants.DumpHeadSize + 256];
    legacy[0] = 0xFE;
    legacy[1] = 0xEF;
    legacy[2] = GhostLegacyConstants.HeadTypeFirst;

    using var ms = new MemoryStream();
    ms.Write(legacy);
    ms.Position = 0;

    var ex = Assert.Throws<NotSupportedException>(() =>
      GhostInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("partition1.bin", [0, 1, 2])]));
    Assert.That(ex!.Message, Does.Contain("3.0+").Or.Contain("record stream"));
    Assert.That(ex.Message, Does.Contain("pre-3.0").IgnoreCase.Or.Contain("DOS").IgnoreCase);
  }

  [Test, Category("ExceptionalCase")]
  public void Replace_RejectsPre30Image() {
    var legacy = new byte[GhostLegacyConstants.DumpHeadSize + 64];
    legacy[0] = 0xFE; legacy[1] = 0xEF;
    legacy[2] = GhostLegacyConstants.HeadTypeFirst;
    using var ms = new MemoryStream();
    ms.Write(legacy);
    ms.Position = 0;

    Assert.That(() => GhostInPlaceModifier.Replace(ms, "partition1.bin", [1, 2, 3]),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Remove_RejectsPre30Image() {
    var legacy = new byte[GhostLegacyConstants.DumpHeadSize + 64];
    legacy[0] = 0xFE; legacy[1] = 0xEF;
    legacy[2] = GhostLegacyConstants.HeadTypePartition;
    using var ms = new MemoryStream();
    ms.Write(legacy);
    ms.Position = 0;

    Assert.That(() => GhostInPlaceModifier.Remove(ms, "partition1.bin"),
      Throws.TypeOf<NotSupportedException>());
  }

  // ── Roundtrip via descriptor surface ────────────────────────────────

  [Test, Category("HappyPath")]
  public void Mutate_Then_Extract_RoundTripsViaDescriptor() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 2048);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    var fresh = new byte[1500];
    for (var i = 0; i < fresh.Length; i++) fresh[i] = (byte)(i * 3);
    d.Add(ms, [ArchiveInputInfo.InMemory("partition2.bin", fresh)]);

    ms.Position = 0;
    var ops = (IArchiveFormatOperations)new GhostFormatDescriptor();
    var bytes = ops.ExtractEntryToMemory(ms, "partition2.bin", password: null);
    Assert.That(bytes, Is.EqualTo(fresh));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_RemoveDelegatesToTombstoneAppend() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 1024);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var d = (IArchiveModifiable)new GhostFormatDescriptor();
    d.Remove(ms, ["partition1.bin"]);

    ms.Position = 0;
    var r = new GhostReader(ms);
    Assert.That(r.Annotations, Has.Count.EqualTo(1));
    Assert.That(r.Annotations[0].Op, Is.EqualTo(GhostConstants.AnnotationOpRemove));
    Assert.That(r.Entries.Any(e => e.Name == "partition1.bin"), Is.False);
  }

  // ── Boundary / exceptional ──────────────────────────────────────────

  [Test, Category("ExceptionalCase")]
  public void Add_RejectsNullStream() {
    Assert.That(() => GhostInPlaceModifier.Add(null!,
      [ArchiveInputInfo.InMemory("p", new byte[1])]), Throws.ArgumentNullException);
  }

  [Test, Category("ExceptionalCase")]
  public void Add_RejectsNullInputs() {
    using var ms = new MemoryStream(BuildSampleImage(GhostConstants.CompressionFast));
    Assert.That(() => GhostInPlaceModifier.Add(ms, null!), Throws.ArgumentNullException);
  }

  [Test, Category("ExceptionalCase")]
  public void Add_RejectsReadOnlyStream() {
    var image = BuildSampleImage(GhostConstants.CompressionFast);
    using var ms = new MemoryStream(image, writable: false);
    Assert.That(() => GhostInPlaceModifier.Add(ms,
      [ArchiveInputInfo.InMemory("p", new byte[1])]), Throws.ArgumentException);
  }

  [Test, Category("ExceptionalCase")]
  public void Add_OnEncryptedImageWithoutPassword_Throws() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, password: "secret");
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    Assert.That(() => GhostInPlaceModifier.Add(ms,
      [ArchiveInputInfo.InMemory("p", new byte[1])]),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Latest-write-wins ───────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_ThenReplace_LatestWriteWins() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 256);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Replace(ms, "partition1.bin", Encoding.UTF8.GetBytes("first"));
    GhostInPlaceModifier.Replace(ms, "partition1.bin", Encoding.UTF8.GetBytes("second"));

    ms.Position = 0;
    var r = new GhostReader(ms);
    var p1 = r.Entries.First(e => e.Name == "partition1.bin");
    Assert.That(Encoding.UTF8.GetString(p1.Data), Is.EqualTo("second"));
  }

  [Test, Category("HappyPath")]
  public void Remove_ThenReplace_ReplaceWins() {
    var image = BuildSampleImage(GhostConstants.CompressionFast, partitionSize: 256);
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    GhostInPlaceModifier.Remove(ms, "partition1.bin");
    GhostInPlaceModifier.Replace(ms, "partition1.bin", Encoding.UTF8.GetBytes("comeback"));

    ms.Position = 0;
    var r = new GhostReader(ms);
    var p1 = r.Entries.FirstOrDefault(e => e.Name == "partition1.bin");
    Assert.That(p1, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(p1!.Data), Is.EqualTo("comeback"));
  }
}
