using System.IO.Compression;
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Maintenance;

/// <summary>
/// The shared guard behind every maintenance operation: <see cref="ArchiveSemanticManifest"/>
/// sees a lost path, byte, timestamp or folder, and <see cref="DefragContentGuard.RunVerifiedInPlace"/>
/// keeps an in-place pass only when nothing it can see changed, rolling every write back
/// otherwise.
/// </summary>
[TestFixture]
public sealed class ContentPreservationGuardTests {

  private static readonly DateTimeOffset Stamp = new(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
  private static readonly ZipFormatDescriptor Zip = new();

  private static byte[] Archive(Action<ZipArchive>? extra = null, DateTimeOffset? time = null, string secondName = "dir/b.txt", string content = "bravo") {
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
      void Add(string name, string text) {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = time ?? Stamp;
        using var stream = entry.Open();
        stream.Write(System.Text.Encoding.ASCII.GetBytes(text));
      }
      Add("a.txt", "alpha");
      Add(secondName, content);
      zip.CreateEntry("empty/").LastWriteTime = time ?? Stamp;
      extra?.Invoke(zip);
    }
    return buffer.ToArray();
  }

  private static ArchiveSemanticManifest Manifest(byte[] archive) => ArchiveSemanticManifest.Capture(new MemoryStream(archive), Zip);

  // ── the manifest ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenTheSameArchiveTwice_ThenTheManifestsAgree()
    => Assert.That(Manifest(Archive()).DifferencesTo(Manifest(Archive())), Is.Empty);

  [Test, Category("HappyPath")]
  public void GivenTheSameEntriesStoredDifferently_ThenTheManifestsAgree() {
    using var buffer = new MemoryStream(Archive());
    using var deflated = new MemoryStream();
    ZipRawRewriter.Recompress(buffer, deflated);
    Assert.That(Manifest(Archive()).DifferencesTo(Manifest(deflated.ToArray())), Is.Empty,
      "compression method and stored size are representation, not content");
  }

  [TestCase("dir/b.txt", "BRAVO", "content changed", TestName = "Manifest_SeesChangedBytes")]
  [TestCase("dir/c.txt", "bravo", "gone", TestName = "Manifest_SeesARename")]
  [TestCase("b.txt", "bravo", "gone", TestName = "Manifest_SeesAMoveToAnotherFolder")]
  [Category("Exception")]
  public void GivenADifferentEntry_ThenTheManifestNamesTheDifference(string name, string content, string expected) {
    var differences = Manifest(Archive()).DifferencesTo(Manifest(Archive(secondName: name, content: content)));
    Assert.That(differences, Has.Some.Contains(expected));
  }

  [Test, Category("Exception")]
  public void GivenADifferentTimestamp_ThenTheManifestSeesIt() {
    var differences = Manifest(Archive()).DifferencesTo(Manifest(Archive(time: Stamp.AddSeconds(2))));
    Assert.That(differences, Has.Some.Contains("modified"));
  }

  [Test, Category("Exception")]
  public void GivenADroppedEmptyFolder_ThenTheManifestSeesIt() {
    using var buffer = new MemoryStream(Archive());
    ZipModifier.RemoveFile(buffer, "empty/");
    Assert.That(Manifest(Archive()).DifferencesTo(Manifest(buffer.ToArray())), Has.Some.Contains("empty"));
  }

  [Test, Category("Exception")]
  public void GivenAPathListedTwice_ThenNoManifestCanBeTaken() {
    var duplicated = Archive(zip => {
      using var stream = zip.CreateEntry("a.txt").Open();
      stream.WriteByte(1);
    });
    Assert.That(() => Manifest(duplicated), Throws.TypeOf<NotSupportedException>());
  }

  // ── the in-place guard ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAPassThatKeepsEverything_ThenItsWritesAreKept() {
    using var image = new MemoryStream(Archive());
    var eocd = image.Length - 22;

    DefragContentGuard.RunVerifiedInPlace(image, Zip, (s, journal) => journal.Write(s, eocd + 20, [0, 0]), "probe");

    Assert.That(image.ToArray(), Is.EqualTo(Archive()), "a pass that rewrites a field with its own value changes nothing");
  }

  [Test, Category("Exception")]
  public void GivenAPassThatCorruptsAFile_ThenEveryWriteIsRolledBackAndItIsRefused() {
    using var image = new MemoryStream(Archive());
    var original = image.ToArray();
    var payload = original.AsSpan().IndexOf("alpha"u8);

    Assert.That(() => DefragContentGuard.RunVerifiedInPlace(image, Zip,
      (s, journal) => journal.Write(s, payload, "ALPHA"u8), "probe"), Throws.TypeOf<NotSupportedException>());
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Exception")]
  public void GivenAPassThatFailsHalfway_ThenWhatItWroteIsRolledBack() {
    using var image = new MemoryStream(Archive());
    var original = image.ToArray();

    Assert.That(() => DefragContentGuard.RunVerifiedInPlace(image, Zip, (s, journal) => {
      journal.Write(s, 0, [0xFF, 0xFF, 0xFF, 0xFF]);
      throw new InvalidDataException("halfway");
    }, "probe"), Throws.TypeOf<NotSupportedException>().With.Message.Contains("halfway"));
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Exception")]
  public void GivenAPassThatChangesTheImageSize_ThenItIsRefusedAndTheSizeRestored() {
    using var image = new MemoryStream(Archive());
    var original = image.ToArray();

    Assert.That(() => DefragContentGuard.RunVerifiedInPlace(image, Zip, (s, _) => s.SetLength(s.Length + 512), "probe"),
      Throws.TypeOf<NotSupportedException>());
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Exception")]
  public void GivenAnImageItsReaderRejects_ThenThePassIsNotAttempted() {
    using var image = new MemoryStream(new byte[100]);
    var ran = false;
    Assert.That(() => DefragContentGuard.RunVerifiedInPlace(image, Zip, (_, _) => ran = true, "probe"), Throws.TypeOf<NotSupportedException>());
    Assert.That(ran, Is.False);
  }

  [Test, Category("Boundary")]
  public void GivenAJournalledWriteOfTheSameBytes_ThenNothingIsRecorded() {
    using var image = new MemoryStream([1, 2, 3, 4]);
    var journal = new InPlacePatchJournal();
    journal.Write(image, 1, [2, 3]);
    Assert.That(journal.Count, Is.Zero);
  }

  [Test, Category("Exception")]
  public void GivenAJournalledWritePastTheEnd_ThenItIsRejected() {
    using var image = new MemoryStream([1, 2, 3, 4]);
    Assert.That(() => new InPlacePatchJournal().Write(image, 3, [9, 9]), Throws.TypeOf<ArgumentOutOfRangeException>());
  }
}
