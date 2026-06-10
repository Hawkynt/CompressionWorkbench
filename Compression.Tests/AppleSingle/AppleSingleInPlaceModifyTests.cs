using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.AppleSingle;

namespace Compression.Tests.AppleSingle;

[TestFixture]
public class AppleSingleInPlaceModifyTests {

  // ── Fixtures ──────────────────────────────────────────────────────

  private static byte[] BuildAs(params (uint Id, byte[] Data)[] entries)
    => AppleSingleWriter.Build(entries);

  private static MemoryStream BuildStream(params (uint Id, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    var bytes = BuildAs(entries);
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  // ── Descriptor surface ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsBothInterfaces() {
    var d = new AppleSingleFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  // ── Writer round-trip ─────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsThroughReader() {
    var data = AppleSingleWriter.Build([
      (3u, "Foo.txt"u8.ToArray()),
      (1u, "hello"u8.ToArray()),
    ]);
    var c = AppleSingleReader.Read(data);
    Assert.That(c.IsDouble, Is.False);
    Assert.That(c.Entries, Has.Count.EqualTo(2));
    Assert.That(c.Entries[0].EntryId, Is.EqualTo(3u));
    Assert.That(c.Entries[1].EntryId, Is.EqualTo(1u));
    Assert.That(c.Entries[1].Data, Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Writer_NameMappingRoundTrip() {
    Assert.That(AppleSingleWriter.EntryIdForName("data_fork.bin"), Is.EqualTo(1u));
    Assert.That(AppleSingleWriter.EntryIdForName("resource_fork.bin"), Is.EqualTo(2u));
    Assert.That(AppleSingleWriter.EntryIdForName("real_name.txt"), Is.EqualTo(3u));
    Assert.That(AppleSingleWriter.EntryIdForName("finder_info.bin"), Is.EqualTo(8u));
    Assert.That(AppleSingleWriter.EntryIdForName("entry_99999.bin"), Is.EqualTo(99999u));
  }

  [Test, Category("EdgeCase")]
  public void Writer_RejectsUnknownName() {
    Assert.That(() => AppleSingleWriter.EntryIdForName("random.bin"), Throws.ArgumentException);
  }

  // ── Add ───────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewEntryAppearsInListing() {
    using var ms = BuildStream(
      (3u, "OriginalName.txt"u8.ToArray()),
      (1u, "original"u8.ToArray()));

    AppleSingleInPlaceModifier.AddEntry(ms, 8u /* finder_info */, [0xDE, 0xAD, 0xBE, 0xEF]);

    ms.Position = 0;
    var c = AppleSingleReader.Read(ms.ToArray());
    var ids = c.Entries.Select(e => e.EntryId).ToList();
    Assert.That(ids, Does.Contain(3u));
    Assert.That(ids, Does.Contain(1u));
    Assert.That(ids, Does.Contain(8u));
    var added = c.Entries.First(e => e.EntryId == 8u);
    Assert.That(added.Data, Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
  }

  [Test, Category("RoundTrip")]
  public void Add_ExistingPayloadsByteContentSurvives() {
    var realName = "MyDocument.txt"u8.ToArray();
    var dataFork = "the quick brown fox jumps over the lazy dog"u8.ToArray();
    using var ms = BuildStream((3u, realName), (1u, dataFork));

    AppleSingleInPlaceModifier.AddEntry(ms, 8u, [0xCA, 0xFE]);

    ms.Position = 0;
    var c = AppleSingleReader.Read(ms.ToArray());
    Assert.That(c.Entries.First(e => e.EntryId == 3u).Data, Is.EqualTo(realName));
    Assert.That(c.Entries.First(e => e.EntryId == 1u).Data, Is.EqualTo(dataFork));
  }

  [Test, Category("RoundTrip")]
  public void Add_EntryCountIncremented() {
    using var ms = BuildStream((3u, "Foo"u8.ToArray()));
    AppleSingleInPlaceModifier.AddEntry(ms, 1u, "bar"u8.ToArray());

    var raw = ms.ToArray();
    var count = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(24));
    Assert.That(count, Is.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void Add_ViaDescriptor_AppendsEntry() {
    using var ms = BuildStream((3u, "Name"u8.ToArray()));
    var d = new AppleSingleFormatDescriptor();

    ((IArchiveModifiable)d).Add(ms, [
      ArchiveInputInfo.InMemory("data_fork.bin", "payload"u8.ToArray()),
    ]);

    ms.Position = 0;
    var c = AppleSingleReader.Read(ms.ToArray());
    Assert.That(c.Entries.Any(e => e.EntryId == 1u && Encoding.ASCII.GetString(e.Data) == "payload"), Is.True);
  }

  // ── Remove ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_DropsEntryFromDirectory() {
    using var ms = BuildStream(
      (3u, "Name"u8.ToArray()),
      (1u, "keep"u8.ToArray()),
      (8u, [0xAA, 0xBB]));

    var ok = AppleSingleInPlaceModifier.RemoveEntry(ms, 1u);

    Assert.That(ok, Is.True);
    ms.Position = 0;
    var c = AppleSingleReader.Read(ms.ToArray());
    var ids = c.Entries.Select(e => e.EntryId).ToList();
    Assert.That(ids, Does.Not.Contain(1u));
    Assert.That(ids, Does.Contain(3u));
    Assert.That(ids, Does.Contain(8u));
  }

  [Test, Category("Security")]
  public void Remove_PayloadBytesAreWiped() {
    var secret = "TOPSECRET"u8.ToArray();
    using var ms = BuildStream(
      (3u, "Name"u8.ToArray()),
      (1u, secret));

    // Snapshot the offset of the secret payload before remove.
    ms.Position = 0;
    var beforeC = AppleSingleReader.Read(ms.ToArray());
    var dataForkEntryIdx = beforeC.Entries.ToList().FindIndex(e => e.EntryId == 1u);
    var bytesBefore = ms.ToArray();
    var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(bytesBefore.AsSpan(26 + 12 * dataForkEntryIdx + 4));
    var dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytesBefore.AsSpan(26 + 12 * dataForkEntryIdx + 8));

    AppleSingleInPlaceModifier.RemoveEntry(ms, 1u);

    var bytesAfter = ms.ToArray();
    var wipedSlice = bytesAfter.AsSpan((int)dataOffset, dataLength).ToArray();
    Assert.That(wipedSlice, Is.All.EqualTo((byte)0));
  }

  [Test, Category("RoundTrip")]
  public void Remove_SurvivorPayloadOffsetsUnchanged() {
    var alpha = "alpha"u8.ToArray();
    var beta = "beta"u8.ToArray();
    var gamma = "gamma"u8.ToArray();

    using var ms = BuildStream((3u, alpha), (1u, beta), (8u, gamma));
    var rawBefore = ms.ToArray();

    // Capture beta + gamma offsets + bytes before remove.
    var betaOff = BinaryPrimitives.ReadUInt32BigEndian(rawBefore.AsSpan(26 + 12 + 4));
    var gammaOff = BinaryPrimitives.ReadUInt32BigEndian(rawBefore.AsSpan(26 + 24 + 4));
    var betaBytes = rawBefore.AsSpan((int)betaOff, beta.Length).ToArray();
    var gammaBytes = rawBefore.AsSpan((int)gammaOff, gamma.Length).ToArray();

    AppleSingleInPlaceModifier.RemoveEntry(ms, 3u);

    var rawAfter = ms.ToArray();
    // Beta and gamma bytes survive byte-identical at their original absolute offsets.
    Assert.That(rawAfter.AsSpan((int)betaOff, beta.Length).ToArray(), Is.EqualTo(betaBytes));
    Assert.That(rawAfter.AsSpan((int)gammaOff, gamma.Length).ToArray(), Is.EqualTo(gammaBytes));
  }

  [Test, Category("EdgeCase")]
  public void Remove_NonExistentReturnsFalse() {
    using var ms = BuildStream((3u, "Name"u8.ToArray()));
    Assert.That(AppleSingleInPlaceModifier.RemoveEntry(ms, 999u), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Remove_ViaDescriptor_DropsEntry() {
    using var ms = BuildStream(
      (3u, "Name"u8.ToArray()),
      (1u, "drop"u8.ToArray()));
    var d = new AppleSingleFormatDescriptor();

    ((IArchiveModifiable)d).Remove(ms, ["data_fork.bin"]);

    ms.Position = 0;
    var c = AppleSingleReader.Read(ms.ToArray());
    Assert.That(c.Entries.Any(e => e.EntryId == 1u), Is.False);
    Assert.That(c.Entries.Any(e => e.EntryId == 3u), Is.True);
  }

  // ── Replace ───────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Replace_SameSizeKeepsOffset() {
    using var ms = BuildStream((3u, "AAAAA"u8.ToArray()), (1u, "BBBBB"u8.ToArray()));
    var rawBefore = ms.ToArray();
    var aaaaaOff = BinaryPrimitives.ReadUInt32BigEndian(rawBefore.AsSpan(26 + 4));

    AppleSingleInPlaceModifier.ReplaceEntry(ms, 3u, "CCCCC"u8.ToArray());

    var rawAfter = ms.ToArray();
    // Offset slot did not change.
    var aaaaaOffAfter = BinaryPrimitives.ReadUInt32BigEndian(rawAfter.AsSpan(26 + 4));
    Assert.That(aaaaaOffAfter, Is.EqualTo(aaaaaOff));
    // Total length should be identical (no append).
    Assert.That(rawAfter.Length, Is.EqualTo(rawBefore.Length));
    // Content swapped.
    ms.Position = 0;
    var c = AppleSingleReader.Read(rawAfter);
    Assert.That(c.Entries.First(e => e.EntryId == 3u).Data, Is.EqualTo("CCCCC"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Replace_LargerSize_AppendsAtEofAndWipesOld() {
    using var ms = BuildStream((3u, "old"u8.ToArray()), (1u, "data"u8.ToArray()));
    var rawBefore = ms.ToArray();
    var oldOff = BinaryPrimitives.ReadUInt32BigEndian(rawBefore.AsSpan(26 + 4));

    AppleSingleInPlaceModifier.ReplaceEntry(ms, 3u, "this is a much longer string"u8.ToArray());

    var rawAfter = ms.ToArray();
    // The 3 bytes at the old offset must now be zero (wiped).
    Assert.That(rawAfter.AsSpan((int)oldOff, 3).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0 }));
    // The new entry must round-trip correctly.
    var c = AppleSingleReader.Read(rawAfter);
    Assert.That(Encoding.ASCII.GetString(c.Entries.First(e => e.EntryId == 3u).Data),
      Is.EqualTo("this is a much longer string"));
  }

  // ── Create ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Create_ProducesValidContainer() {
    var d = new AppleSingleFormatDescriptor();
    using var ms = new MemoryStream();

    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("real_name.txt", "MyDoc.txt"u8.ToArray()),
      ArchiveInputInfo.InMemory("data_fork.bin", "hello world"u8.ToArray()),
    ], new FormatCreateOptions());

    var c = AppleSingleReader.Read(ms.ToArray());
    Assert.That(c.IsDouble, Is.False);
    Assert.That(c.Entries, Has.Count.EqualTo(2));
    Assert.That(c.Entries.Any(e => e.EntryId == 3u && Encoding.ASCII.GetString(e.Data) == "MyDoc.txt"), Is.True);
    Assert.That(c.Entries.Any(e => e.EntryId == 1u && Encoding.ASCII.GetString(e.Data) == "hello world"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Create_DropsSyntheticMetadataIniEntry() {
    var d = new AppleSingleFormatDescriptor();
    using var ms = new MemoryStream();

    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("metadata.ini", "ignored"u8.ToArray()),
      ArchiveInputInfo.InMemory("data_fork.bin", "real"u8.ToArray()),
    ], new FormatCreateOptions());

    var c = AppleSingleReader.Read(ms.ToArray());
    Assert.That(c.Entries, Has.Count.EqualTo(1));
    Assert.That(c.Entries[0].EntryId, Is.EqualTo(1u));
  }

  // ── Mutate-then-Extract round-trip ───────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_AddThenReadBack() {
    var d = new AppleSingleFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, [
      ArchiveInputInfo.InMemory("data_fork.bin", "initial"u8.ToArray()),
    ], new FormatCreateOptions());

    ((IArchiveModifiable)d).Add(ms, [
      ArchiveInputInfo.InMemory("resource_fork.bin", "resource-bytes"u8.ToArray()),
    ]);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "data_fork.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "resource_fork.bin"), Is.True);

    var dataBytes = d.ExtractEntryToMemory(ms, "data_fork.bin", null);
    var rsrcBytes = d.ExtractEntryToMemory(ms, "resource_fork.bin", null);
    Assert.That(Encoding.ASCII.GetString(dataBytes), Is.EqualTo("initial"));
    Assert.That(Encoding.ASCII.GetString(rsrcBytes), Is.EqualTo("resource-bytes"));
  }
}
