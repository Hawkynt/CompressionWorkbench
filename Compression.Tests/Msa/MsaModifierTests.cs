#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.Fat;
using FileSystem.Msa;

namespace Compression.Tests.Msa;

[TestFixture]
public class MsaModifierTests {

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>Builds a FAT12 720KB ST-style floppy with the given files and wraps it in MSA.</summary>
  private static MemoryStream BuildMsaWithFat(params (string Name, byte[] Data)[] files) {
    var fw = new FatWriter();
    foreach (var (name, data) in files) fw.AddFile(name, data);
    // 720 KB DSDD floppy = 1440 sectors × 512 = 720 KB. SPT=9, sides=2 (numSides=2).
    var disk = fw.Build(totalSectors: 1440);

    var msa = new MemoryStream();
    MsaWriter.Write(msa, disk, sectorsPerTrack: 9, sides: 1); // sides=1 means 2 sides (header value).
    msa.Position = 0;
    return msa;
  }

  private static List<string> ListFatNames(Stream msaStream) {
    msaStream.Position = 0;
    var reader = new MsaReader(msaStream);
    var flat = reader.Extract(reader.Entries[0]);
    using var fs = new MemoryStream(flat, writable: false);
    var fr = new FatReader(fs);
    return fr.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private static byte[] ReadFatFile(Stream msaStream, string name) {
    msaStream.Position = 0;
    var reader = new MsaReader(msaStream);
    var flat = reader.Extract(reader.Entries[0]);
    using var fs = new MemoryStream(flat, writable: false);
    var fr = new FatReader(fs);
    var entry = fr.Entries.First(e => !e.IsDirectory &&
      e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    return fr.Extract(entry);
  }

  // ── Geometry preservation ─────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesMsaGeometry() {
    var ms = BuildMsaWithFat(("README.TXT", "first"u8.ToArray()));
    MsaModifier.AddFile(ms, "NEW.TXT", "added"u8.ToArray());

    ms.Position = 0;
    var r = new MsaReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.SectorsPerTrack, Is.EqualTo(9));
      Assert.That(r.Sides, Is.EqualTo(1));
      Assert.That(r.StartTrack, Is.EqualTo(0));
      Assert.That(r.EndTrack, Is.EqualTo(79));
    });
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_PreservesMsaGeometry() {
    var ms = BuildMsaWithFat(("X.TXT", "data"u8.ToArray()));
    MsaModifier.RemoveFile(ms, "X.TXT");

    ms.Position = 0;
    var r = new MsaReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.SectorsPerTrack, Is.EqualTo(9));
      Assert.That(r.Sides, Is.EqualTo(1));
      Assert.That(r.EndTrack, Is.EqualTo(79));
    });
  }

  [Test, Category("RoundTrip")]
  public void AddFile_OutputStillStartsWithMsaMagic() {
    var ms = BuildMsaWithFat();
    MsaModifier.AddFile(ms, "FOO.BIN", new byte[] { 1, 2, 3 });

    ms.Position = 0;
    var bytes = ms.ToArray();
    var magic = BinaryPrimitives.ReadUInt16BigEndian(bytes);
    Assert.That(magic, Is.EqualTo(MsaReader.MsaMagic));
  }

  // ── Add round-trip ────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_NewFileAppearsInDirectory() {
    var ms = BuildMsaWithFat();
    MsaModifier.AddFile(ms, "HELLO.TXT", "world"u8.ToArray());

    var names = ListFatNames(ms);
    Assert.That(names, Has.Some.Matches<string>(n => n.Equals("HELLO.TXT", StringComparison.OrdinalIgnoreCase)));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ContentReadsBackByteForByte() {
    var ms = BuildMsaWithFat();
    var payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();
    MsaModifier.AddFile(ms, "DATA.BIN", payload);

    Assert.That(ReadFatFile(ms, "DATA.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_AddsAlongsidePreExistingFiles() {
    var ms = BuildMsaWithFat(("OLD.TXT", "old data"u8.ToArray()));
    MsaModifier.AddFile(ms, "NEW.TXT", "fresh"u8.ToArray());

    var names = ListFatNames(ms);
    Assert.Multiple(() => {
      Assert.That(names, Has.Some.Matches<string>(n => n.Equals("OLD.TXT", StringComparison.OrdinalIgnoreCase)));
      Assert.That(names, Has.Some.Matches<string>(n => n.Equals("NEW.TXT", StringComparison.OrdinalIgnoreCase)));
    });
    // Both contents intact.
    Assert.Multiple(() => {
      Assert.That(ReadFatFile(ms, "OLD.TXT"), Is.EqualTo("old data"u8.ToArray()));
      Assert.That(ReadFatFile(ms, "NEW.TXT"), Is.EqualTo("fresh"u8.ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildMsaWithFat();
    MsaModifier.AddFile(ms, "A.TXT", "alpha"u8.ToArray());
    MsaModifier.AddFile(ms, "B.TXT", "bravo"u8.ToArray());
    MsaModifier.AddFile(ms, "C.TXT", "charlie"u8.ToArray());

    var names = ListFatNames(ms);
    Assert.Multiple(() => {
      Assert.That(names, Has.Some.Matches<string>(n => n.Equals("A.TXT", StringComparison.OrdinalIgnoreCase)));
      Assert.That(names, Has.Some.Matches<string>(n => n.Equals("B.TXT", StringComparison.OrdinalIgnoreCase)));
      Assert.That(names, Has.Some.Matches<string>(n => n.Equals("C.TXT", StringComparison.OrdinalIgnoreCase)));
    });
  }

  // ── Remove round-trip ─────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RemoveFile_FileDisappearsFromDirectory() {
    var ms = BuildMsaWithFat(("DOOMED.TXT", "bye"u8.ToArray()));
    var ok = MsaModifier.RemoveFile(ms, "DOOMED.TXT");
    Assert.That(ok, Is.True);

    var names = ListFatNames(ms);
    Assert.That(names, Has.None.Matches<string>(n => n.Equals("DOOMED.TXT", StringComparison.OrdinalIgnoreCase)));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NonExistentReturnsFalse() {
    var ms = BuildMsaWithFat(("PRESENT.TXT", "here"u8.ToArray()));
    var ok = MsaModifier.RemoveFile(ms, "ABSENT.TXT");
    Assert.That(ok, Is.False);
    // Existing file still readable.
    Assert.That(ReadFatFile(ms, "PRESENT.TXT"), Is.EqualTo("here"u8.ToArray()));
  }

  [Test, Category("Wipe")]
  public void RemoveFile_WipesContentBytes() {
    // Use a payload distinctive enough to scan for in the decoded flat image.
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = 0xA5;
    var ms = BuildMsaWithFat(("SECRET.BIN", payload));
    MsaModifier.RemoveFile(ms, "SECRET.BIN");

    ms.Position = 0;
    var reader = new MsaReader(ms);
    var flat = reader.Extract(reader.Entries[0]);
    // After wipe, no 16-byte run of 0xA5 should remain in the cluster data
    // region (boot sector / FAT areas could legitimately hold 0xFF chains
    // but never long 0xA5 runs since 0xA5 is not a FAT sentinel).
    var run = 0;
    var maxRun = 0;
    foreach (var b in flat) {
      if (b == 0xA5) { run++; if (run > maxRun) maxRun = run; }
      else run = 0;
    }
    Assert.That(maxRun, Is.LessThan(16), "FatRemover should have wiped the cluster contents.");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_RemainingFilesIntact() {
    var ms = BuildMsaWithFat(
      ("KEEP1.TXT", "alpha"u8.ToArray()),
      ("DROP.TXT", "bravo"u8.ToArray()),
      ("KEEP2.TXT", "charlie"u8.ToArray()));

    Assert.That(MsaModifier.RemoveFile(ms, "DROP.TXT"), Is.True);

    Assert.Multiple(() => {
      Assert.That(ReadFatFile(ms, "KEEP1.TXT"), Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(ReadFatFile(ms, "KEEP2.TXT"), Is.EqualTo("charlie"u8.ToArray()));
      var names = ListFatNames(ms);
      Assert.That(names, Has.None.Matches<string>(n => n.Equals("DROP.TXT", StringComparison.OrdinalIgnoreCase)));
    });
  }

  // ── Add then Remove round-trip ────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddThenRemove_RoundTrip_FileGone() {
    var ms = BuildMsaWithFat();
    MsaModifier.AddFile(ms, "TEMP.TXT", "scratch"u8.ToArray());
    Assert.That(MsaModifier.RemoveFile(ms, "TEMP.TXT"), Is.True);

    var names = ListFatNames(ms);
    Assert.That(names, Has.None.Matches<string>(n => n.Equals("TEMP.TXT", StringComparison.OrdinalIgnoreCase)));
  }

  // ── Argument validation ───────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void AddFile_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => MsaModifier.AddFile(null!, "X.TXT", []));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_NullName_Throws() {
    using var ms = BuildMsaWithFat();
    Assert.Throws<ArgumentNullException>(() => MsaModifier.AddFile(ms, null!, []));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_NullData_Throws() {
    using var ms = BuildMsaWithFat();
    Assert.Throws<ArgumentNullException>(() => MsaModifier.AddFile(ms, "X.TXT", null!));
  }

  [Test, Category("ErrorHandling")]
  public void RemoveFile_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => MsaModifier.RemoveFile(null!, "X.TXT"));
  }

  [Test, Category("ErrorHandling")]
  public void RemoveFile_NullName_Throws() {
    using var ms = BuildMsaWithFat();
    Assert.Throws<ArgumentNullException>(() => MsaModifier.RemoveFile(ms, null!));
  }

  // ── Descriptor IArchiveModifiable surface ─────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModifyCapability() {
    var desc = new MsaFormatDescriptor();
    Assert.That(desc.Capabilities & Compression.Registry.FormatCapabilities.CanModify,
      Is.EqualTo(Compression.Registry.FormatCapabilities.CanModify));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    var desc = new MsaFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
  }
}
