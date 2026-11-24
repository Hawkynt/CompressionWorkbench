using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Reflinked files: a volume this package wrote, into which the Linux 6.8 kernel cloned files with
/// <c>cp --reflink=always</c> (moving their data into the reflink tree as indirect extents) and
/// overwrote part of one clone. The reader follows each <c>reflink_p</c> into the indirect
/// extents; the in-place editor carries them, counts references down when a reflinked file is
/// removed or replaced, and drops an indirect extent no file references any more.
/// Provenance: <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class BcacheFsReflinkTests {

  private const string Volume = "linux-6.8-reflink.img.gz";

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_bcachefs_reflink_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static MemoryStream Image() {
    using var resource = typeof(BcacheFsReflinkTests).Assembly.GetManifestResourceStream("BcacheFsVectors." + Volume)
      ?? throw new FileNotFoundException($"embedded reference vector {Volume} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, (string Sha256, long Size)> Manifest() {
    using var resource = typeof(BcacheFsReflinkTests).Assembly.GetManifestResourceStream("BcacheFsVectors.manifest-reflink.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      var parts = line.Split(' ', 3);
      files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
    return files;
  }

  private static Dictionary<string, (string Sha256, long Size)> Read(Stream image) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    Assert.That(reader.Valid, Is.True, reader.Status);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries.Where(static e => e.LinkTarget == null)) {
      Assert.That(entry.Unreadable, Is.Null, entry.Name);
      using var ms = new MemoryStream();
      reader.ExtractTo(entry, ms);
      files[entry.Name] = (Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())), ms.Length);
    }
    image.Position = 0;
    return files;
  }

  /// <summary>The indirect extents: end position → refcount.</summary>
  private static Dictionary<ulong, ulong> Refcounts(Stream image) {
    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    var result = new Dictionary<ulong, ulong>();
    if (core.Root(BcacheFsBtreeId.Reflink) is not null)
      foreach (var key in BcacheFsBtreeReader.ReadTree(core, BcacheFsBtreeId.Reflink).MaterializedLeafSlots)
        if (key.RawType == BcacheFsFormat.KeyReflinkV)
          result[key.Position.Offset] = BinaryPrimitives.ReadUInt64LittleEndian(key.Value);
    image.Position = 0;
    return result;
  }

  private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text)));

  // ── Reading ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAVolumeWithReflinkedFiles_WhenRead_ThenEveryFileIsWhatTheKernelRead() {
    using var image = Image();
    Assert.That(Refcounts(image), Is.Not.Empty, "precondition: the kernel moved data into the reflink tree");
    Assert.That(Read(image), Is.EquivalentTo(Manifest()));
  }

  // ── Editing ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenReflinkedFiles_WhenOneCloneIsRemovedAndAnotherReplaced_ThenTheOthersStayAndReferencesAreCountedDown() {
    using var image = Image();
    var before = Refcounts(image);
    var descriptor = new BcacheFsFormatDescriptor();
    descriptor.Add(image, [
      ArchiveInputInfo.InMemory("added.txt", Encoding.ASCII.GetBytes("added beside the clones\n")),
      ArchiveInputInfo.InMemory("kseq-clone.txt", Encoding.ASCII.GetBytes("replaced\n")),
    ]);
    image.Position = 0;
    descriptor.Remove(image, ["clone.bin"]);

    var expected = Manifest();
    expected.Remove("clone.bin");
    expected["kseq-clone.txt"] = (Sha("replaced\n"), 9);
    expected["added.txt"] = (Sha("added beside the clones\n"), 24);
    Assert.That(Read(image), Is.EquivalentTo(expected));
    var after = Refcounts(image);
    Assert.Multiple(() => {
      Assert.That(after.Keys, Is.EquivalentTo(before.Keys), "every indirect extent is still referenced");
      Assert.That(after.Values.Sum(static v => (long)v), Is.LessThan(before.Values.Sum(static v => (long)v)));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenEveryNameOfReflinkedData_WhenRemoved_ThenItsIndirectExtentsGo() {
    using var image = Image();
    var before = Refcounts(image);
    var descriptor = new BcacheFsFormatDescriptor();
    descriptor.Remove(image, ["kseq.txt", "kseq-clone.txt"]);
    var mid = Refcounts(image);
    descriptor.Remove(image, ["orig.bin", "clone.bin", "clone2.bin"]);

    var expected = Manifest();
    foreach (var name in new[] { "kseq.txt", "kseq-clone.txt", "orig.bin", "clone.bin", "clone2.bin" }) expected.Remove(name);
    Assert.Multiple(() => {
      Assert.That(mid.Count, Is.LessThan(before.Count), "the kseq data's indirect extents went with its last name");
      Assert.That(Refcounts(image), Is.Empty, "no indirect extent outlives its last reference");
      Assert.That(Read(image), Is.EquivalentTo(expected));
    });
  }

  [Test, Category("Exception")]
  public void GivenReflinkedData_WhenDefragmented_ThenItIsRefusedAndTheVolumeUnchanged() {
    using var image = Image();
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() =>
      new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }));
    Assert.That(image.ToArray().SequenceEqual(before), Is.True);
  }

  // ── fsck ───────────────────────────────────────────────────────────────

  private static IEnumerable<TestCaseData> Edits() {
    yield return new TestCaseData(new[] { "clone.bin" }, new string[0]).SetName("GivenAReflinkedVolume_WhenACloneIsRemoved_ThenFsckIsClean");
    yield return new TestCaseData(new[] { "kseq.txt", "kseq-clone.txt" }, new string[0]).SetName("GivenAReflinkedVolume_WhenEveryNameOfOneDataIsRemoved_ThenFsckIsClean");
    yield return new TestCaseData(new string[0], new[] { "clone2.bin", "orig.bin" }).SetName("GivenAReflinkedVolume_WhenReflinkedFilesAreReplaced_ThenFsckIsClean");
    yield return new TestCaseData(new string[0], new[] { "new/file.bin" }).SetName("GivenAReflinkedVolume_WhenAFileIsAdded_ThenFsckIsClean");
  }

  [TestCaseSource(nameof(Edits)), Category("ExternalFsInterop")]
  public void EditedFsck(string[] remove, string[] add) {
    BcacheFsOracleGate.RequireBcachefsTools();
    using var image = Image();
    var path = Path.Combine(this._tmpDir, "reflink.img");
    File.WriteAllBytes(path, image.ToArray());
    BcacheFsKernel.AssertFsckClean(path);
    var descriptor = new BcacheFsFormatDescriptor();
    if (add.Length > 0)
      descriptor.Add(image, [.. add.Select(n => ArchiveInputInfo.InMemory(n, Encoding.ASCII.GetBytes("new contents of " + n + "\n")))]);
    image.Position = 0;
    if (remove.Length > 0) descriptor.Remove(image, remove);
    File.WriteAllBytes(path, image.ToArray());
    BcacheFsKernel.AssertFsckClean(path);
  }
}
