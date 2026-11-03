using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Flex;

namespace Compression.Tests.Flex;

/// <summary>
/// FLEX disks: the reader against a disk <c>flexfloppy</c> formatted and filled (and,
/// when present in <c>test-corpus/flex</c>, the real TSC FLEX 9 distribution disk), the
/// writer's NEWDISK layout against the TSC Adaptation Guide, in-place edits and refusals.
/// The two-way <c>flexfloppy</c> oracle is <see cref="FlexExternalConformanceTests"/>.
/// </summary>
[TestFixture]
public class FlexTests {

  internal static byte[] Resource(string name) {
    using var res = typeof(FlexTests).Assembly.GetManifestResourceStream($"FlexVectors.{name}")
      ?? throw new InvalidOperationException($"{name} not embedded");
    using var ms = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gz = new GZipStream(res, CompressionMode.Decompress);
      gz.CopyTo(ms);
    } else res.CopyTo(ms);
    return ms.ToArray();
  }

  internal static Dictionary<string, string> Manifest(string name)
    => Encoding.ASCII.GetString(Resource(name)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  ", 2)).ToDictionary(p => p[1], p => p[0]);

  internal static string Sha(byte[] d) => Convert.ToHexStringLower(SHA256.HashData(d));

  private static Dictionary<string, byte[]> ReadAll(byte[] img) {
    var d = new FlexDisk(new MemoryStream(img, writable: false));
    return d.Entries.ToDictionary(e => e.FullName, d.Extract);
  }

  // ── third-party images ────────────────────────────────────────────────

  [Test]
  public void GivenFlexfloppyDisk_WhenRead_ThenEveryFileMatchesWhatFlexfloppyExtracts() {
    var files = ReadAll(Resource("flexfloppy.dsk.gz"));
    var manifest = Manifest("flexfloppy.manifest");
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    foreach (var (n, sha) in manifest) Assert.That(Sha(files[n]), Is.EqualTo(sha), n);
  }

  [Test]
  public void GivenRealTscFlex9Disk_WhenRead_ThenEveryFileMatchesWhatFlexfloppyExtracts() {
    var path = Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "test-corpus", "flex", "TSCFLX09.dsk");
    if (!File.Exists(path))
      Assert.Ignore("test-corpus/flex/TSCFLX09.dsk (TSC FLEX 9.1 distribution disk, sha256 cd61d5f3…a5c) is not present; see Flex/ReferenceVectors/README.md.");
    var img = File.ReadAllBytes(path);
    var d = new FlexDisk(new MemoryStream(img));
    Assert.That((d.Label, d.VolumeNumber, d.Tracks, d.SectorsPerTrack, d.FreeSectors), Is.EqualTo(("FLEX-SWT", (ushort)6809, 35, 10, (ushort)161)));
    var manifest = Manifest("TSCFLX09.manifest");
    var files = d.Entries.ToDictionary(e => e.FullName, d.Extract);
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    foreach (var (n, sha) in manifest) Assert.That(Sha(files[n]), Is.EqualTo(sha), n);
    Assert.That(d.Entries.Single(e => e.FullName == "ERRORS.SYS").IsRandom, Is.True, "ERRORS.SYS is a random file");
    Assert.That(d.Entries.Single(e => e.FullName == "FLEX.SYS").Created, Is.EqualTo(new DateTime(1980, 10, 3, 0, 0, 0, DateTimeKind.Utc)));
  }

  // ── NEWDISK layout (Adaptation Guide 9.1) ─────────────────────────────

  [Test]
  public void GivenEmptyDisk_WhenFormatted_ThenSirDirectoryAndFreeChainFollowNewdisk() {
    var img = FlexWriter.Format(35, 10, "VOLUME", 7, new DateTime(1984, 5, 6));
    var sir = img.AsSpan(0x200, 256).ToArray();
    Assert.Multiple(() => {
      Assert.That(sir.AsSpan(0, 16).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "bytes 0-15 zero");
      Assert.That(Encoding.ASCII.GetString(sir.AsSpan(16, 6)), Is.EqualTo("VOLUME"));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(sir.AsSpan(27)), Is.EqualTo(7), "volume number");
      Assert.That(sir.AsSpan(29, 4).ToArray(), Is.EqualTo(new byte[] { 1, 1, 34, 10 }), "first and last data sector");
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(sir.AsSpan(33)), Is.EqualTo(340), "34 tracks × 10 data sectors");
      Assert.That(sir.AsSpan(35, 3).ToArray(), Is.EqualTo(new byte[] { 5, 6, 84 }), "date month-day-year");
      Assert.That(sir[38], Is.EqualTo(34), "highest track");
      Assert.That(sir[39], Is.EqualTo(10), "highest sector");
    });
    // Directory sectors 5..10 on track 0 linked, last link zero.
    Assert.That(img.AsSpan(4 * 256, 2).ToArray(), Is.EqualTo(new byte[] { 0, 6 }));
    Assert.That(img.AsSpan(9 * 256, 2).ToArray(), Is.EqualTo(new byte[] { 0, 0 }));
    // Free chain: last sector of track 1 links to track 2 sector 1; the disk's last sector ends it.
    Assert.That(img.AsSpan((1 * 10 + 9) * 256, 2).ToArray(), Is.EqualTo(new byte[] { 2, 1 }));
    Assert.That(img.AsSpan((34 * 10 + 9) * 256, 2).ToArray(), Is.EqualTo(new byte[] { 0, 0 }));
  }

  // ── round trips and edits ─────────────────────────────────────────────

  private static byte[] Padded(byte[] data) {
    var sectors = Math.Max(1, (data.Length + 251) / 252);
    var p = new byte[sectors * 252];
    data.CopyTo(p, 0);
    return p;
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(251)]
  [TestCase(252)]
  [TestCase(253)]
  [TestCase(20_000)]
  public void GivenFileOfBoundarySize_WhenWrittenAndRead_ThenBytesComeBackInWholeRecords(int size) {
    var data = Enumerable.Range(0, size).Select(i => (byte)(i * 7 + 1)).ToArray();
    var img = FlexWriter.Format(35, 10);
    FlexWriter.AddFile(img, "f.bin", data);
    Assert.That(ReadAll(img)["F.BIN"], Is.EqualTo(Padded(data)));
  }

  [Test]
  public void GivenMoreFilesThanTrackZeroHolds_WhenAdded_ThenTheDirectoryGrowsOntoTheFreeChain() {
    var img = FlexWriter.Format(35, 10);          // 6 directory sectors = 60 entries
    for (var i = 0; i < 65; ++i) FlexWriter.AddFile(img, $"F{i}.DAT", [(byte)i]);
    var d = new FlexDisk(new MemoryStream(img));
    Assert.That(d.Entries, Has.Count.EqualTo(65));
    Assert.That(d.DirectorySectors, Has.Count.EqualTo(7));
    Assert.That(d.DirectorySectors[^1].Track, Is.Not.Zero);
    Assert.That(d.FreeSectors, Is.EqualTo(340 - 65 - 1));
  }

  [Test]
  public void GivenFile_WhenRemoved_ThenEntryIsDeletedSectorsZeroedAndReturnedToTheFreeChain() {
    var img = FlexWriter.Format(35, 10);
    FlexWriter.AddFile(img, "KEEP.TXT", "keep"u8);
    FlexWriter.AddFile(img, "DROP.TXT", Encoding.ASCII.GetBytes(new string('W', 600)));
    Assert.That(FlexWriter.RemoveFile(img, "drop.txt"), Is.True);
    Assert.That(FlexWriter.RemoveFile(img, "drop.txt"), Is.False);
    var d = new FlexDisk(new MemoryStream(img));
    Assert.That(d.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "KEEP.TXT" }));
    Assert.That(d.FreeSectors, Is.EqualTo(339));
    Assert.That(Encoding.ASCII.GetString(img).Contains("WWWW"), Is.False);
    Assert.That(img[0x400 + 16 + 24], Is.EqualTo(0xFF), "deleted entry's first byte");
    // The freed sectors are reused by the next file once the chain head is exhausted:
    // the free chain still adds up.
    FlexWriter.AddFile(img, "NEW.TXT", new byte[300]);
    Assert.That(new FlexDisk(new MemoryStream(img)).FreeSectors, Is.EqualTo(337));
  }

  [Test]
  public void GivenFlexfloppyDisk_WhenWeAddAndRemove_ThenItsOwnFilesAreUntouched() {
    var img = Resource("flexfloppy.dsk.gz");
    using var ms = new MemoryStream();
    ms.Write(img);
    var d = new FlexFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("ADDED.DAT", new byte[1000])]);
    d.Remove(ms, ["HELLO.TXT"]);
    Assert.That(ms.Length, Is.EqualTo(img.Length));
    var files = ReadAll(ms.ToArray());
    var manifest = Manifest("flexfloppy.manifest");
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "NUMBERS.TXT", "EXACT.BIN", "RANDOM.BIN", "ADDED.DAT" }));
    foreach (var n in new[] { "NUMBERS.TXT", "EXACT.BIN", "RANDOM.BIN" }) Assert.That(Sha(files[n]), Is.EqualTo(manifest[n]), n);
  }

  [Test]
  public void GivenDescriptorCreate_WhenTreeIsGiven_ThenFilesLandByLeafNameAndGeometryFits() {
    var d = new FlexFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("HELLO.TXT", "hi"u8.ToArray()), new ArchiveInputInfo("", "SUB/", true), ArchiveInputInfo.InMemory("SUB/INNER.TXT", new byte[100_000])],
      new FormatCreateOptions());
    var disk = new FlexDisk(new MemoryStream(ms.ToArray()));
    Assert.That(disk.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { "HELLO.TXT", "INNER.TXT" }));
    Assert.That((disk.Tracks, disk.SectorsPerTrack), Is.EqualTo((40, 18)), "100 KB (398 sectors) outgrows 35×10 but fits 40×18");
  }

  // ── refusals ──────────────────────────────────────────────────────────

  [TestCase("NOEXT", TestName = "Flex_NoExtension_Refused")]
  [TestCase("1ABC.TXT", TestName = "Flex_NameStartsWithDigit_Refused")]
  [TestCase("TOOLONGNAME.TXT", TestName = "Flex_NineCharName_Refused")]
  [TestCase("A.TEXT", TestName = "Flex_FourCharExtension_Refused")]
  [TestCase("A B.TXT", TestName = "Flex_Space_Refused")]
  public void GivenUnstorableName_WhenAdded_ThenRefused(string name)
    => Assert.Throws<NotSupportedException>(() => FlexWriter.AddFile(FlexWriter.Format(35, 10), name, [1]));

  [Test]
  public void GivenDiskTooFull_WhenAddingABatch_ThenRefusedAndImageUnchanged() {
    using var ms = new MemoryStream();
    ms.Write(FlexWriter.Format(35, 10));
    var before = ms.ToArray();
    var d = new FlexFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Add(ms,
      [ArchiveInputInfo.InMemory("OK.DAT", [1]), ArchiveInputInfo.InMemory("HUGE.DAT", new byte[341 * 252])]));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenProtectedFile_WhenRemoved_ThenRefused() {
    var img = FlexWriter.Format(35, 10);
    FlexWriter.AddFile(img, "LOCK.SYS", [1]);
    img[0x400 + 16 + 11] = 0x40;   // delete protect
    Assert.Throws<NotSupportedException>(() => FlexWriter.RemoveFile(img, "LOCK.SYS"));
  }

  [Test]
  public void GivenFreeChainDisagreeingWithSir_WhenEdited_ThenRefused() {
    var img = FlexWriter.Format(35, 10);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(0x200 + 33), 5);
    Assert.Throws<NotSupportedException>(() => FlexWriter.AddFile(img, "A.TXT", [1]));
  }

  [Test]
  public void GivenChainShorterThanTheDirectorySays_WhenExtracted_ThenRefused() {
    var img = FlexWriter.Format(35, 10);
    FlexWriter.AddFile(img, "A.TXT", new byte[1000]);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(0x400 + 16 + 17), 9);
    var d = new FlexDisk(new MemoryStream(img));
    Assert.Throws<InvalidDataException>(() => d.Extract(d.Entries.Single()));
  }

  [Test]
  public void GivenNonFlexBytes_WhenOpened_ThenInvalidData() {
    var img = FlexWriter.Format(35, 10);
    img[0x200] = 1;
    Assert.Throws<InvalidDataException>(() => new FlexDisk(new MemoryStream(img)));
  }

  [Test]
  public void GivenFlexDisk_WhenDetectedByMagic_ThenFlex()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Resource("flexfloppy.dsk.gz")).ToString(), Is.EqualTo("Flex"));
}
