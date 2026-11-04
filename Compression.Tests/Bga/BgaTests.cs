using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Bga;

namespace Compression.Tests.Bga;

/// <summary>
/// GZA/BZA archives against the original BGA32.DLL 0.37: archives it wrote read here, ours read
/// there. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class BgaTests {
  private const string NotesSha = "b1c7fc110eddefeb0e48a1253da9bcffc276cb092762ca66563cf90f495bef09";
  private const string TinySha = "8950abfda7b727630760dd35bcf5c3daa7631aff223a90f7728c0d2521dde10c";
  private const string NoiseSha = "5aa448bd3dcddfae74de537191592b860a55f1410e5bb0fe83013b9af19fd24d";
  private const string ZipSha = "8739c76e681f900923b900c9df0ef75cf421d39cabb54650c4b9ad19b6a76d85";

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BgaVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded BGA vector '{name}' is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("bga-gzip.gza", "gzip")]
  [TestCase("bga-bzip2.bza", "bz2")]
  [Category("ThirdPartyVector")]
  public void Given_Bga32Archive_When_Read_Then_MembersMatchTheInputs(string vector, string method) {
    var data = Vector(vector);
    var entries = BgaArchive.Read(data, out var truncated);
    Assert.That(truncated, Is.False);
    Assert.That(entries.Select(e => (e.FileName, e.IsDirectory, e.IsStored)), Is.EqualTo(new[] {
      ("already.zip", false, true), ("docs/notes.txt", false, false), ("docs/sub/tiny.txt", false, true),
      ("docs/sub", true, false), ("docs", true, false), ("empty.txt", false, true), ("noise.bin", false, true),
    }));
    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Method.ToLowerInvariant()), Is.All.EqualTo(method));
    var notes = entries.Single(e => e.FileName == "docs/notes.txt");
    Assert.That(Sha(BgaArchive.Extract(data, notes)), Is.EqualTo(NotesSha));
    Assert.That(notes.LastModified, Is.EqualTo(new DateTime(2024, 5, 6, 6, 8, 10)));
    Assert.That(Sha(BgaArchive.Extract(data, entries.Single(e => e.FileName == "docs/sub/tiny.txt"))), Is.EqualTo(TinySha));
    Assert.That(Sha(BgaArchive.Extract(data, entries.Single(e => e.FileName == "noise.bin"))), Is.EqualTo(NoiseSha));
    Assert.That(Sha(BgaArchive.Extract(data, entries.Single(e => e.FileName == "already.zip"))), Is.EqualTo(ZipSha));
    Assert.That(BgaArchive.Extract(data, entries.Single(e => e.FileName == "empty.txt")), Is.Empty);
  }

  // BGA32.DLL wrote these records; rebuilding each one from its decoded content must give the
  // same header bytes (the gzip/bzip2 payloads differ only where the compressors do).
  [TestCase("bga-gzip.gza", true)]
  [TestCase("bga-bzip2.bza", false)]
  [Category("ThirdPartyVector")]
  public void Given_Bga32Archive_When_StoredAndDirectoryRecordsRebuilt_Then_ByteIdentical(string vector, bool gzip) {
    var data = Vector(vector);
    foreach (var e in BgaArchive.Read(data, out _).Where(e => e.IsDirectory || e.IsStored)) {
      var original = data.AsSpan((int)e.RecordOffset, (int)(e.DataOffset + e.CompressedSize - e.RecordOffset)).ToArray();
      var rebuilt = BgaArchive.BuildRecord(gzip, e.FileName, BgaArchive.Extract(data, e), e.LastModified, e.IsDirectory);
      if (e.FileName is "already.zip")
        continue; // BGA32 stores an existing .zip without trying; our writer stores it because it does not shrink
      Assert.That(rebuilt, Is.EqualTo(original), e.FileName);
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_SelfExtractorStub_When_Read_Then_HeaderFoundAfterIt() {
    var data = (byte[])[.. new byte[5000], .. Vector("bga-gzip.gza")];
    "MZ"u8.CopyTo(data);
    var entries = BgaArchive.Read(data, out _);
    Assert.That(entries, Has.Count.EqualTo(7));
    Assert.That(Sha(BgaArchive.Extract(data, entries.Single(e => e.FileName == "docs/notes.txt"))), Is.EqualTo(NotesSha));
  }

  [Test, Category("EdgeCase")]
  public void Given_BadChecksum_When_Read_Then_LaterRecordsReportedTruncated() {
    var data = Vector("bga-gzip.gza");
    data[61 + 30] ^= 1; // a name byte of the second record
    var entries = BgaArchive.Read(data, out var truncated);
    Assert.That(truncated, Is.True);
    Assert.That(entries.Select(e => e.FileName), Is.EqualTo(new[] { "already.zip" }));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptGzipMember_When_Extracted_Then_InvalidData() {
    var data = Vector("bga-gzip.gza");
    var notes = BgaArchive.Read(data, out _).Single(e => e.FileName == "docs/notes.txt");
    data[notes.DataOffset + 200] ^= 0x40;
    Assert.Catch(() => BgaArchive.Extract(data, notes));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoHeader_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => BgaArchive.Read(new byte[100], out _));

  internal static byte[] WriteOurs(bool gzip) {
    var stamp = new DateTime(2024, 5, 6, 7, 8, 10);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"Our BGA line {i % 31}\r\n")));
    using var ms = new MemoryStream();
    var d = gzip ? (IArchiveCreatable)new GzaFormatDescriptor() : new BzaFormatDescriptor();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("docs/report.txt", text, new ArchiveEntryMetadata(LastWriteTimeUtc: new DateTimeOffset(stamp, TimeSpan.Zero))),
      ArchiveInputInfo.InMemory("raw.bin", [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))], new ArchiveEntryMetadata(LastWriteTimeUtc: new DateTimeOffset(stamp, TimeSpan.Zero))),
      ArchiveInputInfo.InMemory("empty.txt", [], new ArchiveEntryMetadata(LastWriteTimeUtc: new DateTimeOffset(stamp, TimeSpan.Zero))),
    ], new FormatCreateOptions());
    return ms.ToArray();
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("RoundTrip")]
  public void Given_Files_When_Created_Then_ReadBack(bool gzip) {
    var data = WriteOurs(gzip);
    var entries = BgaArchive.Read(data, out var truncated);
    Assert.That(truncated, Is.False);
    Assert.That(entries.Select(e => e.FileName), Is.EqualTo(new[] { "docs/report.txt", "raw.bin", "empty.txt" }));
    Assert.That(BgaArchive.Extract(data, entries[1]), Is.EqualTo(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray()));
    Assert.That(entries[0].IsStored, Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Given_Bga32Archive_When_FileAddedAndRemoved_Then_OtherRecordsByteIdentical() {
    var original = Vector("bga-bzip2.bza");
    var d = new BzaFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(original);
    d.Remove(ms, ["noise.bin"]);
    d.Add(ms, [ArchiveInputInfo.InMemory("added.txt", "added\r\n"u8.ToArray())]);
    var data = ms.ToArray();
    var noise = BgaArchive.Read(original, out _).Single(e => e.FileName == "noise.bin");
    Assert.That(data.AsSpan(0, (int)noise.RecordOffset).ToArray(), Is.EqualTo(original.AsSpan(0, (int)noise.RecordOffset).ToArray()));
    var entries = BgaArchive.Read(data, out _);
    Assert.That(entries.Select(e => e.FileName).Last(), Is.EqualTo("added.txt"));
    Assert.That(entries.Any(e => e.FileName == "noise.bin"), Is.False);
  }

  [Test, Category("EdgeCase")]
  public void Given_GzaArchive_When_EditedAsBza_Then_Refused() {
    using var ms = new MemoryStream();
    ms.Write(Vector("bga-gzip.gza"));
    Assert.Throws<NotSupportedException>(() => new BzaFormatDescriptor().Remove(ms, ["noise.bin"]));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Given_NoRecords_When_Creating_Then_ItIsRefusedBeforeWriting(bool gzip) {
    BgaFormatDescriptorBase descriptor = gzip ? new GzaFormatDescriptor() : new BzaFormatDescriptor();
    using var archive = new MemoryStream();
    Assert.That(() => descriptor.Create(archive, [], new FormatCreateOptions()), Throws.ArgumentException);
    Assert.That(archive.Length, Is.Zero);
  }

  [TestCase(true, false)]
  [TestCase(false, false)]
  [TestCase(true, true)]
  [TestCase(false, true)]
  public void Given_TheLastRecord_When_Removed_Then_TheArchiveAndStubStayUnchanged(bool gzip, bool stub) {
    BgaFormatDescriptorBase descriptor = gzip ? new GzaFormatDescriptor() : new BzaFormatDescriptor();
    using var archive = new MemoryStream();
    if (stub)
      archive.Write("MZ opaque self-extractor prefix"u8);
    descriptor.Create(archive, [ArchiveInputInfo.InMemory("only.txt", [])], new FormatCreateOptions());
    var before = archive.ToArray();

    Assert.That(() => descriptor.Remove(archive, ["only.txt"]), Throws.TypeOf<NotSupportedException>());
    Assert.That(archive.ToArray(), Is.EqualTo(before));
    Assert.That(descriptor.List(archive, null).Select(e => e.Name), Is.EqualTo(new[] { "only.txt" }));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Given_AHeaderPerRecordFormat_When_Purged_Then_ItRefusesWithoutChangingTheArchive(bool gzip) {
    BgaFormatDescriptorBase descriptor = gzip ? new GzaFormatDescriptor() : new BzaFormatDescriptor();
    using var archive = new MemoryStream();
    archive.Write(WriteOurs(gzip));
    var before = archive.ToArray();
    var purge = (IArchivePurgeable)descriptor;

    Assert.That(purge.CanPurgeToEmpty, Is.False);
    Assert.That(() => purge.Purge(archive), Throws.TypeOf<NotSupportedException>());
    Assert.That(archive.ToArray(), Is.EqualTo(before));
    Assert.That(descriptor.List(archive, null), Has.Count.EqualTo(3));
  }

  // ours-*.gza/bza were written here; BGA32.DLL listed and extracted them (bga32-listing.txt).
  [TestCase("ours.gza")]
  [TestCase("ours.bza")]
  [TestCase("ours-edited.bza")]
  [Category("ThirdPartyVector")]
  public void Given_ArchiveBga32Extracted_When_Read_Then_Valid(string vector) {
    var data = Vector(vector);
    foreach (var e in BgaArchive.Read(data, out _))
      Assert.That(BgaArchive.Extract(data, e), Has.Length.EqualTo(e.IsDirectory ? 0 : e.OriginalSize));
  }
}
