using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Archives that RAR wrote, from RAR 1.40 to 7.12, and libarchive's RAR test suite: every file
/// must extract byte for byte (checked against its SHA-256 here and its CRC-32/BLAKE2sp inside the
/// reader), the metadata must match what RAR stored, and what unrar refuses must be refused.
/// See <c>Rar/Conformance/README.md</c> for how each archive was made.
/// </summary>
[TestFixture]
[Category("ThemVsUs")]
public class RarConformanceTests {

  public static IEnumerable<TestCaseData> ModernArchives()
    => RarConformanceSet.Manifest(RarConformanceSet.Modern).Select(a => new TestCaseData(a).SetName("Modern_" + a.Archive));

  public static IEnumerable<TestCaseData> DosArchives()
    => RarConformanceSet.Manifest(RarConformanceSet.Dos, "manifest-dos.tsv").Select(a => new TestCaseData(a).SetName("Dos_" + a.Archive));

  public static IEnumerable<TestCaseData> LibarchiveArchives()
    => RarConformanceSet.Manifest(RarConformanceSet.Libarchive).Select(a => new TestCaseData(a).SetName("Libarchive_" + a.Archive));

  [TestCaseSource(nameof(ModernArchives))]
  [TestCaseSource(nameof(DosArchives))]
  [TestCaseSource(nameof(LibarchiveArchives))]
  public void Given_AnArchiveRarWrote_When_EveryEntryIsExtracted_Then_EachMatchesTheOriginal(RarExpectedArchive archive) {
    using var reader = RarReader.Open(RarConformanceSet.PathOf(archive.Set, archive.Archive), archive.Password);
    var got = RarConformanceSet.ExtractAll(reader);
    foreach (var e in archive.Entries) {
      Assert.That(got.ContainsKey(e.Name), Is.True, $"{archive}: '{e.Name}' missing; have {string.Join(", ", got.Keys)}");
      Assert.That(got[e.Name].LongLength, Is.EqualTo(e.Size), $"{archive}: size of {e.Name}");
      Assert.That(RarConformanceSet.Sha256(got[e.Name]), Is.EqualTo(e.Sha256), $"{archive}: content of {e.Name}");
    }
  }

  public static IEnumerable<TestCaseData> RefusedByUnrar() {
    foreach (var line in File.ReadAllLines(RarConformanceSet.PathOf(RarConformanceSet.Libarchive, "refused.tsv"))) {
      if (line.Length == 0) continue;
      var f = line.Split('\t');
      var produced = f.Skip(3).Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
      yield return new TestCaseData(f[0], f[2].Length > 0 ? f[2] : null, produced).SetName("Refused_" + f[0]);
    }
  }

  /// <summary>
  /// Damaged and malicious archives (libarchive's fuzz cases): whatever we extract, unrar must
  /// have extracted identically; everything else is refused with an exception, never hangs.
  /// </summary>
  [TestCaseSource(nameof(RefusedByUnrar))]
  public void Given_AnArchiveUnrarRefuses_When_Read_Then_NothingUnverifiedComesOut(string archive, string? password, Dictionary<string, string> unrarProduced) {
    RarReader reader;
    try {
      reader = RarReader.Open(RarConformanceSet.PathOf(RarConformanceSet.Libarchive, archive), password);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException) {
      return;
    }
    using (reader) {
      for (var i = 0; i < reader.Entries.Count; ++i) {
        var e = reader.Entries[i];
        if (e.IsDirectory || e.IsSymlink) continue;
        byte[] data;
        try {
          data = reader.Extract(i);
        } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException) {
          continue;
        }
        Assert.That(unrarProduced.TryGetValue(e.Name, out var sha), Is.True, $"{archive}: extracted '{e.Name}', which unrar did not");
        Assert.That(RarConformanceSet.Sha256(data), Is.EqualTo(sha), $"{archive}: '{e.Name}' differs from unrar's");
      }
    }
  }

  [TestCase("test_read_format_rar5_bad_tables.rar")]
  [TestCase("test_read_format_rar5_loop_bug.rar")]
  public void Given_AStreamWithoutChecksumThatCannotBeDecodedAsWritten_When_Extracted_Then_ItIsRefused(string archive) {
    using var reader = RarReader.Open(RarConformanceSet.PathOf(RarConformanceSet.Libarchive, archive));
    Assert.That(reader.Entries, Is.Not.Empty);
    Assert.Throws<InvalidDataException>(() => reader.Extract(0));
  }

  // ---- metadata RAR stored ----

  private static RarReader Modern(string name, string? password = null)
    => RarReader.Open(RarConformanceSet.PathOf(RarConformanceSet.Modern, name), password);

  private static RarEntry Entry(RarReader r, string name) => r.Entries.Single(e => e.Name == name);

  [TestCase("r5-times.rar")]
  [TestCase("r4-times.rar")]
  public void Given_HighPrecisionTimes_When_Listed_Then_ModificationCreationAndAccessComeBackTo100ns(string name) {
    using var r = Modern(name);
    var e = Entry(r, "empty.dat");
    Assert.That(e.ModifiedTime?.UtcDateTime, Is.EqualTo(new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567)));
    Assert.That(e.CreatedTime?.UtcDateTime, Is.EqualTo(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(7654321)));
    Assert.That(e.AccessedTime?.UtcDateTime, Is.EqualTo(new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1111111)));
  }

  [TestCase("r5-m3.rar")]
  [TestCase("r4-m3.rar")]
  public void Given_ReadOnlyAndHiddenFiles_When_Listed_Then_TheirWindowsAttributesSurvive(string name) {
    using var r = Modern(name);
    Assert.That(Entry(r, "noise.bin").Attributes & 0x01, Is.EqualTo(1u), "read-only");
    Assert.That(Entry(r, "sub/deep/small.txt").Attributes & 0x02, Is.EqualTo(2u), "hidden");
    Assert.That(r.Entries.Where(e => e.IsDirectory).Select(e => e.Name), Is.SupersetOf(new[] { "emptydir", "sub", "sub/deep" }));
    Assert.That(r.Entries.Any(e => e.Name == "Grüße-日本語.txt"), Is.True, "Unicode name");
  }

  [TestCase("r5-comment.rar")]
  [TestCase("r4-comment.rar")]
  public void Given_AnArchiveComment_When_Opened_Then_ItReadsBack(string name) {
    using var r = Modern(name);
    // rar stores the comment file with CRLF line ends.
    Assert.That(r.Comment, Is.EqualTo("archive comment line 1\r\narchive comment line 2\r\n"));
  }

  [Test]
  public void Given_ArchiveFlags_When_Opened_Then_LockedSolidAndRecoveryRecordAreReported() {
    using (var r = Modern("r5-locked.rar")) Assert.That(r.IsLocked, Is.True);
    using (var r = Modern("r4-locked.rar")) Assert.That(r.IsLocked, Is.True);
    using (var r = Modern("r5-rr.rar")) Assert.That(r.HasRecoveryRecord, Is.True);
    using (var r = Modern("r4-rr.rar")) Assert.That(r.HasRecoveryRecord, Is.True);
    using (var r = Modern("r5-solid.rar")) Assert.That(r.IsSolid, Is.True);
    using (var r = Modern("r5-m3.rar")) Assert.That(r.IsLocked || r.HasRecoveryRecord || r.IsSolid, Is.False);
  }

  [Test]
  public void Given_HardLinksAndFileCopies_When_Extracted_Then_TheyCarryTheirTargetsData() {
    using (var r = Modern("r5-hardlink.rar")) {
      var link = Entry(r, "hard.txt");
      Assert.That(link.LinkKind, Is.EqualTo(RarLinkKind.HardLink));
      Assert.That(link.LinkTarget, Is.EqualTo("target.txt"));
      Assert.That(r.Extract(r.Entries.ToList().IndexOf(link)), Is.EqualTo("link target\n"u8.ToArray()));
    }
    using (var r = Modern("r5-filecopy.rar")) {
      var copy = Entry(r, "two.txt");
      Assert.That(copy.LinkKind, Is.EqualTo(RarLinkKind.FileCopy));
      Assert.That(copy.CompressedSize, Is.Zero);
      Assert.That(r.Extract(1), Is.EqualTo(r.Extract(0)));
    }
  }

  [TestCase("r5-streams.rar")]
  [TestCase("r4-streams.rar")]
  public void Given_AnNtfsAlternateStream_When_Listed_Then_ItIsAnEntryOfItsFile(string name) {
    using var r = Modern(name);
    var stream = Entry(r, "host.txt:extra");
    Assert.That(stream.StreamName, Is.EqualTo("extra"));
    Assert.That(r.Extract(r.Entries.ToList().IndexOf(stream)), Is.EqualTo("alternate stream data"u8.ToArray()));
  }

  [Test]
  public void Given_FileVersions_When_Listed_Then_TheOldVersionCarriesItsNumber() {
    using var r = Modern("r5-versions.rar");
    Assert.That(Entry(r, "doc.txt;1").FileVersion, Is.EqualTo(1));
    Assert.That(Entry(r, "doc.txt").FileVersion, Is.Null);
  }

  [TestCase("r5-prefixed.exe", 5)]
  [TestCase("r4-prefixed.exe", 4)]
  public void Given_AnArchiveBehindAStub_When_Opened_Then_TheSignatureIsFoundAfterIt(string name, int format) {
    using var r = Modern(name);
    Assert.That(r.SfxSize, Is.EqualTo(4096));
    Assert.That(r.Version, Is.EqualTo(format));
  }

  [Test]
  public void Given_Rar140Archive_When_Opened_Then_ItIsTheRe14FormatWithChecksums() {
    using var r = RarReader.Open(RarConformanceSet.PathOf(RarConformanceSet.Dos, "rar140-m3.rar"));
    Assert.That(r.Version, Is.EqualTo(4));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "TEXT.TXT", "NOISE.BIN", "EMPTY.DAT" }));
  }

  [Test]
  public void Given_Rar140ArchiveWithAFlippedByte_When_Extracted_Then_TheChecksumRefusesIt() {
    var bytes = File.ReadAllBytes(RarConformanceSet.PathOf(RarConformanceSet.Dos, "rar140-m0.rar"));
    bytes[^100] ^= 0x40;   // inside NOISE.BIN's or TEXT.TXT's stored data
    using var r = new RarReader(new MemoryStream(bytes));
    Assert.That(() => { for (var i = 0; i < r.Entries.Count; ++i) r.Extract(i); }, Throws.InstanceOf<InvalidDataException>());
  }

  // ---- passwords ----

  [TestCase("r5-enc.rar")]
  [TestCase("r5-enc-hp.rar")]
  [TestCase("r4-enc.rar")]
  [TestCase("r4-enc-hp.rar")]
  public void Given_AnEncryptedArchive_When_TheWrongPasswordIsGiven_Then_NoDataComesOut(string name) {
    Assert.Throws<InvalidDataException>(() => {
      using var r = Modern(name, "wrong");
      for (var i = 0; i < r.Entries.Count; ++i)
        if (!r.Entries[i].IsDirectory) r.Extract(i);
    });
  }

  [TestCase("r5-enc-hp.rar")]
  [TestCase("r4-enc-hp.rar")]
  public void Given_EncryptedHeaders_When_NoPasswordIsGiven_Then_OpeningAsksForOne(string name)
    => Assert.Throws<InvalidOperationException>(() => Modern(name).Dispose());

  [Test]
  public void Given_Rar5EncryptedData_When_NoPasswordIsGiven_Then_NamesListButExtractionAsksForIt() {
    using var r = Modern("r5-enc.rar");
    Assert.That(r.Entries.All(e => e.IsEncrypted || e.IsDirectory || e.Size == 0), Is.True);
    var file = r.Entries.ToList().FindIndex(e => e.Name == "text.txt");
    Assert.Throws<InvalidOperationException>(() => r.Extract(file));
  }

  // ---- volume sets ----

  private static string CopySet(string prefix, params string[] names) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rarvol_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    foreach (var n in names) File.Copy(RarConformanceSet.PathOf(RarConformanceSet.Modern, n), Path.Combine(dir, n));
    return dir;
  }

  [Test]
  public void Given_AVolumeSetOpenedAtItsSecondVolume_When_Read_Then_TheWholeSetIsRead() {
    using var r = Modern("r5-vol.part2.rar");
    Assert.That(r.VolumeCount, Is.EqualTo(3));
    Assert.That(r.Entries.Single(e => e.Name == "random.bin").VolumeCount, Is.EqualTo(3));
    Assert.DoesNotThrow(() => RarConformanceSet.ExtractAll(r));
  }

  [Test]
  public void Given_AVolumeSetWithItsMiddleVolumeMissing_When_Read_Then_TheSplitFileIsIncompleteAndRefused() {
    var dir = CopySet("r5-vol", "r5-vol.part1.rar", "r5-vol.part3.rar");
    try {
      using var r = RarReader.Open(Path.Combine(dir, "r5-vol.part1.rar"));
      Assert.That(r.MissingVolume, Does.EndWith("r5-vol.part2.rar"));
      var split = r.Entries.ToList().FindIndex(e => e.Name == "random.bin");
      Assert.That(r.Entries[split].IsIncomplete, Is.True);
      Assert.Throws<InvalidDataException>(() => r.Extract(split));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_AVolumeSetFromTheDescriptor_When_Listed_Then_ListingDoesNotThrowForAMissingVolume() {
    var dir = CopySet("r4-vol", "r4-vol.part1.rar");
    try {
      using var fs = File.OpenRead(Path.Combine(dir, "r4-vol.part1.rar"));
      var list = new RarFormatDescriptor().List(fs, null);
      Assert.That(list.Select(e => e.Name), Does.Contain("random.bin"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase("r4-vol-old.rar")]
  [TestCase("r4-vol.part1.rar")]
  [TestCase("r5-vol-hp.part1.rar")]
  public void Given_AVolumeSet_When_ExtractedThroughTheDescriptor_Then_FilesSpanningVolumesComeOutWhole(string first) {
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_rarvolx_{Guid.NewGuid():N}");
    try {
      using (var fs = File.OpenRead(RarConformanceSet.PathOf(RarConformanceSet.Modern, first)))
        new RarFormatDescriptor().Extract(fs, outDir, first.Contains("hp") ? "secret" : null, null);
      var expected = RarConformanceSet.Manifest(RarConformanceSet.Modern).Single(a => a.Archive == first);
      foreach (var e in expected.Entries)
        Assert.That(RarConformanceSet.Sha256(File.ReadAllBytes(Path.Combine(outDir, e.Name))), Is.EqualTo(e.Sha256), e.Name);
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }
}
