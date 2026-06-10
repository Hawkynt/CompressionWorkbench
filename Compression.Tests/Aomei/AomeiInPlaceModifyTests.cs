using System.Text;
using Compression.Registry;
using FileFormat.Aomei;

namespace Compression.Tests.Aomei;

/// <summary>
/// True in-place modify tests for the AOMEI BR_IMAGE_INDEX-based R/W
/// surface. Pins the load-bearing properties of
/// <see cref="AomeiInPlaceModifier"/>:
/// <list type="bullet">
///   <item><description>Add: user-data envelopes before the OLD index
///     start stay byte-identical; existing VDB entries in
///     [shipped+0x18, +0x18+oldCount*0x20) stay byte-identical; new
///     entries land immediately after.</description></item>
///   <item><description>Replace: appends a fresh VDB entry sharing the
///     target's RegNo; old envelope bytes survive at original offset;
///     reader's latest-wins gate surfaces the new envelope.</description></item>
///   <item><description>Remove: appends a tombstone VDB entry; reader
///     hides the entry; original bytes survive (byte-preserving, not
///     forensic wipe).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AomeiInPlaceModifyTests {

  private static byte[] BuildSeedImage(params (string Name, byte[] Data)[] inputs) {
    var writer = new AomeiWriter { UserData = inputs };
    return writer.Build();
  }

  private static AomeiReader ReadImage(byte[] image) {
    using var ms = new MemoryStream(image);
    return new AomeiReader(ms);
  }

  // ─── Add ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_AppendsVdbEntries_AndReaderSurfacesAll() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", Encoding.UTF8.GetBytes("BBB")),
      ArchiveInputInfo.InMemory("c.txt", Encoding.UTF8.GetBytes("CCC")),
    ]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(3));
    Assert.That(live[0].Name, Is.EqualTo("a.txt"));
    Assert.That(live[0].Payload, Is.EqualTo(Encoding.UTF8.GetBytes("AAA")));
    Assert.That(live[1].Name, Is.EqualTo("b.txt"));
    Assert.That(live[1].Payload, Is.EqualTo(Encoding.UTF8.GetBytes("BBB")));
    Assert.That(live[2].Name, Is.EqualTo("c.txt"));
    Assert.That(live[2].Payload, Is.EqualTo(Encoding.UTF8.GetBytes("CCC")));
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesExistingEnvelopeBytes_ByteIdentical() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    var seedReader = ReadImage(seed);
    var oldIdxOffset = seedReader.DataBlockIndexFileOffset!.Value;
    var preIndexBytes = seed.AsSpan(0, (int)oldIdxOffset).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", Encoding.UTF8.GetBytes("BBB")),
    ]);
    var mutated = ms.ToArray();
    // Bytes [0, oldIndexOffset) must stay byte-identical — every existing
    // user-data envelope's bytes are below that boundary.
    Assert.That(mutated.AsSpan(0, preIndexBytes.Length).ToArray(),
                Is.EqualTo(preIndexBytes));
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesExistingVdbEntryBytes_InNewIndex() {
    var seed = BuildSeedImage(
      ("a.txt", Encoding.UTF8.GetBytes("AAA")),
      ("b.txt", Encoding.UTF8.GetBytes("BBB")));
    var seedReader = ReadImage(seed);
    var oldEntries = seedReader.AllVdbEntries.ToArray();

    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("c.txt", Encoding.UTF8.GetBytes("CCC")),
    ]);
    var mutatedReader = ReadImage(ms.ToArray());
    Assert.That(mutatedReader.AllVdbEntries, Has.Count.EqualTo(3));
    // Old entries must be byte-identical at their original [0, oldCount)
    // positions within the index entries array.
    for (var i = 0; i < oldEntries.Length; ++i) {
      Assert.That(mutatedReader.AllVdbEntries[i].RegNo, Is.EqualTo(oldEntries[i].RegNo));
      Assert.That(mutatedReader.AllVdbEntries[i].BlockNo, Is.EqualTo(oldEntries[i].BlockNo));
      Assert.That(mutatedReader.AllVdbEntries[i].ImgOffset, Is.EqualTo(oldEntries[i].ImgOffset));
      Assert.That(mutatedReader.AllVdbEntries[i].OldSize, Is.EqualTo(oldEntries[i].OldSize));
      Assert.That(mutatedReader.AllVdbEntries[i].NewSize, Is.EqualTo(oldEntries[i].NewSize));
      Assert.That(mutatedReader.AllVdbEntries[i].Crc32, Is.EqualTo(oldEntries[i].Crc32));
    }
  }

  [Test, Category("HappyPath")]
  public void Add_PatchesEntryCountAtKnownOffset_AndSealsCrc() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", Encoding.UTF8.GetBytes("BBB")),
    ]);
    var mutated = ms.ToArray();
    var reader = ReadImage(mutated);
    Assert.That(reader.AllVdbEntries, Has.Count.EqualTo(2));
    Assert.That(reader.LiveVdbEntries, Has.Count.EqualTo(2));
    // CRC over the index must verify per the standard-header sealing protocol.
    var idxOff = (int)reader.DataBlockIndexFileOffset!.Value;
    var idxSize = reader.DataBlockIndexSize!.Value;
    var idxRecord = mutated.AsSpan(idxOff, idxSize).ToArray();
    Assert.That(BrStandardHeader.VerifyCrc(idxRecord), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Add_TwoCallsInARow_AccumulatesEntries() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("b.txt", new byte[] { 2 })]);
    AomeiInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("c.txt", new byte[] { 3 })]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(3));
    Assert.That(live.Select(l => l.Name).ToArray(),
                Is.EqualTo(new[] { "a.txt", "b.txt", "c.txt" }));
  }

  [Test, Category("Boundary")]
  public void Add_EmptyInputList_NoOp() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    var before = ms.ToArray();
    AomeiInPlaceModifier.Add(ms, []);
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void Add_NonModifiableContainer_Throws() {
    var seed = new AomeiWriter().Build(); // no user-data, no index
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.Throws<InvalidOperationException>(() =>
      AomeiInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("x", [1])]));
  }

  [Test, Category("ErrorHandling")]
  public void Add_ReadOnlyStream_Throws() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream(seed, writable: false);
    Assert.Throws<ArgumentException>(() =>
      AomeiInPlaceModifier.Add(ms, [ArchiveInputInfo.InMemory("b", [2])]));
  }

  // ─── Replace ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_AppendsFreshEntryWithSameRegNo_LatestWins() {
    var seed = BuildSeedImage(
      ("a.txt", Encoding.UTF8.GetBytes("AAA")),
      ("b.txt", Encoding.UTF8.GetBytes("BBB")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Replace(ms, regNo: 1, "a.txt", Encoding.UTF8.GetBytes("A2"));

    var reader = ReadImage(ms.ToArray());
    // On disk: 3 VDB entries (old #1, original #2, new #1) — wire view.
    Assert.That(reader.AllVdbEntries, Has.Count.EqualTo(3));
    // Live view: 2 (latest #1, original #2).
    Assert.That(reader.LiveVdbEntries, Has.Count.EqualTo(2));
    var live = reader.ResolveLiveUserData();
    var a = live.First(l => l.RegNo == 1);
    Assert.That(a.Payload, Is.EqualTo(Encoding.UTF8.GetBytes("A2")));
  }

  [Test, Category("HappyPath")]
  public void Replace_KeepsOriginalEnvelopeBytes_ByteIdentical() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    var seedReader = ReadImage(seed);
    var originalEnvelopeOffset = (int)seedReader.AllVdbEntries[0].ImgOffset;
    var originalEnvelopeSize = (int)seedReader.AllVdbEntries[0].NewSize;
    var originalEnvelopeBytes = seed.AsSpan(originalEnvelopeOffset, originalEnvelopeSize).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Replace(ms, regNo: 1, "a.txt", Encoding.UTF8.GetBytes("REPLACED"));
    var mutated = ms.ToArray();

    Assert.That(mutated.AsSpan(originalEnvelopeOffset, originalEnvelopeSize).ToArray(),
                Is.EqualTo(originalEnvelopeBytes));
  }

  [Test, Category("ErrorHandling")]
  public void Replace_UnknownRegNo_Throws() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.Throws<FileNotFoundException>(() =>
      AomeiInPlaceModifier.Replace(ms, regNo: 999, "x.txt", [1]));
  }

  [Test, Category("HappyPath")]
  public void Replace_ThenAdd_StackedMutationsConverge() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Replace(ms, regNo: 1, "a.txt", Encoding.UTF8.GetBytes("A2"));
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", Encoding.UTF8.GetBytes("BBB")),
    ]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(2));
    Assert.That(live[0].Name, Is.EqualTo("a.txt"));
    Assert.That(live[0].Payload, Is.EqualTo(Encoding.UTF8.GetBytes("A2")));
    Assert.That(live[1].Name, Is.EqualTo("b.txt"));
    Assert.That(live[1].Payload, Is.EqualTo(Encoding.UTF8.GetBytes("BBB")));
  }

  // ─── Remove ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_AppendsTombstone_AndReaderHidesEntry() {
    var seed = BuildSeedImage(
      ("a.txt", Encoding.UTF8.GetBytes("AAA")),
      ("b.txt", Encoding.UTF8.GetBytes("BBB")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Remove(ms, regNo: 1);

    var reader = ReadImage(ms.ToArray());
    Assert.That(reader.AllVdbEntries, Has.Count.EqualTo(3));
    Assert.That(reader.LiveVdbEntries, Has.Count.EqualTo(1));
    Assert.That(reader.LiveVdbEntries[0].RegNo, Is.EqualTo(2u));
    var live = reader.ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(1));
    Assert.That(live[0].Name, Is.EqualTo("b.txt"));
  }

  [Test, Category("HappyPath")]
  public void Remove_TombstoneCarriesSentinelNewSize() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Remove(ms, regNo: 1);

    var reader = ReadImage(ms.ToArray());
    Assert.That(reader.AllVdbEntries, Has.Count.EqualTo(2));
    Assert.That(reader.AllVdbEntries[1].RegNo, Is.EqualTo(1u));
    Assert.That(reader.AllVdbEntries[1].NewSize,
                Is.EqualTo(AomeiConstants.TombstoneNewSizeSentinel));
    Assert.That(reader.AllVdbEntries[1].ImgOffset, Is.EqualTo(0ul));
    Assert.That(reader.AllVdbEntries[1].OldSize, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath")]
  public void Remove_KeepsOriginalEnvelopeBytes_ByteIdentical() {
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    var seedReader = ReadImage(seed);
    var originalEnvelopeOffset = (int)seedReader.AllVdbEntries[0].ImgOffset;
    var originalEnvelopeSize = (int)seedReader.AllVdbEntries[0].NewSize;
    var originalBytes = seed.AsSpan(originalEnvelopeOffset, originalEnvelopeSize).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Remove(ms, regNo: 1);

    var mutated = ms.ToArray();
    Assert.That(mutated.AsSpan(originalEnvelopeOffset, originalEnvelopeSize).ToArray(),
                Is.EqualTo(originalBytes));
  }

  [Test, Category("ErrorHandling")]
  public void Remove_UnknownRegNo_Throws() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.Throws<FileNotFoundException>(() =>
      AomeiInPlaceModifier.Remove(ms, regNo: 999));
  }

  [Test, Category("HappyPath")]
  public void Remove_ThenAdd_ReusesIndexCleanly() {
    var seed = BuildSeedImage(
      ("a.txt", Encoding.UTF8.GetBytes("AAA")),
      ("b.txt", Encoding.UTF8.GetBytes("BBB")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Remove(ms, regNo: 1);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("c.txt", Encoding.UTF8.GetBytes("CCC")),
    ]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(2));
    Assert.That(live[0].Name, Is.EqualTo("b.txt"));
    Assert.That(live[1].Name, Is.EqualTo("c.txt"));
  }

  // ─── CRC verification on the mutated index ────────────────────────────

  [Test, Category("HappyPath")]
  public void AfterMutation_IndexCrc_VerifiesAtKnownOffsets() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1, 2, 3 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", new byte[] { 4, 5, 6 }),
    ]);
    AomeiInPlaceModifier.Replace(ms, regNo: 1, "a.txt", new byte[] { 7, 8, 9 });
    AomeiInPlaceModifier.Remove(ms, regNo: 2);

    var mutated = ms.ToArray();
    var reader = ReadImage(mutated);
    Assert.That(reader.HeadCrcValid, Is.True);
    Assert.That(reader.TailCrcValid, Is.True);
    // Every record's CRC must still verify after the cascade of mutations.
    foreach (var record in reader.Records)
      Assert.That(record.CrcValid, Is.True,
        $"record at offset 0x{record.FileOffset:X} (type 0x{record.Header.Type:X4}) failed CRC after mutation cascade");

    var live = reader.ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(1));
    Assert.That(live[0].Name, Is.EqualTo("a.txt"));
    Assert.That(live[0].Payload, Is.EqualTo(new byte[] { 7, 8, 9 }));
  }

  [Test, Category("HappyPath")]
  public void AfterMutation_HeadAndTailUntouched() {
    var seed = BuildSeedImage(("a.txt", new byte[] { 1, 2, 3 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    AomeiInPlaceModifier.Add(ms, [
      ArchiveInputInfo.InMemory("b.txt", new byte[] { 4, 5, 6 }),
    ]);
    var mutated = ms.ToArray();
    // Head is at offset 0 and stays byte-identical.
    Assert.That(mutated.AsSpan(0, AomeiConstants.BifhSize).ToArray(),
                Is.EqualTo(seed.AsSpan(0, AomeiConstants.BifhSize).ToArray()));
    // Tail bytes are independent of position (BuildEmpty produces a fixed
    // 0x674-byte buffer with sealed CRC) so they're byte-identical too.
    var newTail = mutated.AsSpan(mutated.Length - AomeiConstants.BiftSize).ToArray();
    var oldTail = seed.AsSpan(seed.Length - AomeiConstants.BiftSize).ToArray();
    Assert.That(newTail, Is.EqualTo(oldTail));
  }

  // ─── Descriptor wiring ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify_AndIsArchiveModifiable() {
    var d = new AomeiFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d is IArchiveModifiable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_DelegatesToInPlaceModifier() {
    var d = new AomeiFormatDescriptor();
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    d.Add(ms, [ArchiveInputInfo.InMemory("b.txt", new byte[] { 2 })]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(2));
    Assert.That(live[1].Name, Is.EqualTo("b.txt"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_ByEnvelopeName_Tombstones() {
    var d = new AomeiFormatDescriptor();
    var seed = BuildSeedImage(
      ("a.txt", new byte[] { 1 }),
      ("b.txt", new byte[] { 2 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    d.Remove(ms, ["a.txt"]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(1));
    Assert.That(live[0].Name, Is.EqualTo("b.txt"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_ByUserdataPrefix_Tombstones() {
    var d = new AomeiFormatDescriptor();
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }), ("b.txt", new byte[] { 2 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    d.Remove(ms, ["userdata/b.txt"]);
    var live = ReadImage(ms.ToArray()).ResolveLiveUserData();
    Assert.That(live, Has.Count.EqualTo(1));
    Assert.That(live[0].Name, Is.EqualTo("a.txt"));
  }

  [Test, Category("ErrorHandling")]
  public void Descriptor_Remove_UnknownName_Throws() {
    var d = new AomeiFormatDescriptor();
    var seed = BuildSeedImage(("a.txt", new byte[] { 1 }));
    using var ms = new MemoryStream();
    ms.Write(seed);
    Assert.Throws<FileNotFoundException>(() => d.Remove(ms, ["nope.txt"]));
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsRwAndCitesVdbAppend() {
    var d = new AomeiFormatDescriptor();
    Assert.That(d.Description, Does.Contain("VDB"));
    Assert.That(d.Description, Does.Contain("Add"));
    Assert.That(d.Description, Does.Contain("Replace"));
    Assert.That(d.Description, Does.Contain("Remove"));
    Assert.That(d.Description, Does.Contain("tombstone"));
  }

  [Test, Category("HappyPath")]
  public void AfterMutation_DescriptorListExtract_RoundTrips() {
    var d = new AomeiFormatDescriptor();
    var seed = BuildSeedImage(("a.txt", Encoding.UTF8.GetBytes("AAA")));
    using var ms = new MemoryStream();
    ms.Write(seed);
    d.Add(ms, [ArchiveInputInfo.InMemory("b.txt", Encoding.UTF8.GetBytes("BBB"))]);
    d.Remove(ms, ["a.txt"]);
    ms.Position = 0;
    var entries = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(entries, Does.Contain("userdata/b.txt"));
    Assert.That(entries, Does.Not.Contain("userdata/a.txt"));

    var outDir = Path.Combine(Path.GetTempPath(), "aomei_rw_extract_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);
      var b = File.ReadAllBytes(Path.Combine(outDir, "userdata", "b.txt"));
      Assert.That(b, Is.EqualTo(Encoding.UTF8.GetBytes("BBB")));
      Assert.That(File.Exists(Path.Combine(outDir, "userdata", "a.txt")), Is.False);
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }
}
