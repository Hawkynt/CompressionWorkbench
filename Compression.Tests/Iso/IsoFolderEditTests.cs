#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// In-place add and remove inside ISO 9660 folders: what the reader shows afterwards, which
/// bytes stay where they were, and the images the editor refuses. The kernel, xorriso and
/// isoinfo verdicts on the same edits are in <see cref="IsoFolderEditOracleTests"/>.
/// </summary>
[TestFixture]
[Category("RoundTrip")]
public sealed class IsoFolderEditTests {

  private static readonly IsoFormatDescriptor Iso = new();

  /// <summary>Bytes no other seed's pattern contains, so a search finds only this file.</summary>
  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  private static MemoryStream Build(bool rockRidge, bool joliet, params (string Name, byte[] Data)[] files) {
    var ms = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["RockRidge"] = rockRidge.ToString(), ["Joliet"] = joliet.ToString() },
    };
    Iso.Create(ms, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], options);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> Files(Stream image) {
    image.Position = 0;
    var names = Iso.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    return names.ToDictionary(n => n, n => Iso.ExtractEntryToMemory(image, n, null), StringComparer.Ordinal);
  }

  private static void Add(Stream image, string name, byte[] data) => Iso.Add(image, [ArchiveInputInfo.InMemory(name, data)]);

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

  [TestCase(true, true)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  public void GivenAnImage_WhenAFileIsAddedIntoNewNestedFolders_ThenTheReaderShowsItAndNoOtherByteOfDataMoved(bool rockRidge, bool joliet) {
    var guide = Pattern(5000, 1);
    var keep = Pattern(9000, 2);
    using var image = Build(rockRidge, joliet, ("docs/guide.txt", guide), ("keep.bin", keep));
    var guideAt = IndexOf(image.ToArray(), guide.AsSpan(0, 64));
    var keepAt = IndexOf(image.ToArray(), keep.AsSpan(0, 64));

    var added = Pattern(3000, 3);
    Add(image, "docs/new folder/Report 2026.txt", added);

    var files = Files(image);
    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "docs/guide.txt", "keep.bin", "docs/new folder/Report 2026.txt" }));
      Assert.That(files["docs/new folder/Report 2026.txt"], Is.EqualTo(added));
      Assert.That(files["docs/guide.txt"], Is.EqualTo(guide));
      Assert.That(image.ToArray().AsSpan(guideAt, guide.Length).ToArray(), Is.EqualTo(guide), "existing data stays at its sectors");
      Assert.That(image.ToArray().AsSpan(keepAt, keep.Length).ToArray(), Is.EqualTo(keep), "existing data stays at its sectors");
    });
  }

  [Test]
  public void GivenAPlainIso9660Image_WhenTwoLongNamesWithTheSame83FormAreAdded_ThenBothStayApart() {
    using var image = Build(rockRidge: false, joliet: false, ("DIR/A.TXT", [1]));

    Add(image, "dir/longfilename_one.txt", "one"u8.ToArray());
    Add(image, "dir/longfilename_two.txt", "two"u8.ToArray());

    var files = Files(image);
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(files.Values.Select(v => System.Text.Encoding.ASCII.GetString(v)), Is.SupersetOf(new[] { "one", "two" }));
    Assert.That(files.Keys.Where(k => k.StartsWith("DIR/", StringComparison.Ordinal)).Distinct().Count(), Is.EqualTo(3),
      "each 8.3 identifier is unique in its folder");
  }

  [Test]
  public void GivenAFolderThatOutgrowsItsSector_WhenFilesKeepComing_ThenItMovesAndEveryFileReadsBack() {
    using var image = Build(rockRidge: true, joliet: true, ("big/first.txt", "first"u8.ToArray()), ("big/sub/deep.txt", "deep"u8.ToArray()));
    var inputs = Enumerable.Range(0, 70)
      .Select(i => ArchiveInputInfo.InMemory($"big/A rather long file name number {i:D3}.txt", System.Text.Encoding.ASCII.GetBytes($"file {i}")))
      .ToList();

    Iso.Add(image, inputs);

    var files = Files(image);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(72));
      Assert.That(files["big/A rather long file name number 069.txt"], Is.EqualTo("file 69"u8.ToArray()));
      Assert.That(files["big/sub/deep.txt"], Is.EqualTo("deep"u8.ToArray()), "the moved folder's subfolder is still reachable");
    });
  }

  [Test]
  public void GivenAnExistingNestedFile_WhenItIsAddedAgain_ThenItIsReplacedAndItsOldDataZeroed() {
    var old = Pattern(4096, 4);
    using var image = Build(rockRidge: true, joliet: true, ("a/b/c.txt", old));

    Add(image, "a/b/c.txt", "new"u8.ToArray());

    var files = Files(image);
    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "a/b/c.txt" }));
      Assert.That(files["a/b/c.txt"], Is.EqualTo("new"u8.ToArray()));
      Assert.That(IndexOf(image.ToArray(), old.AsSpan(0, 64)), Is.EqualTo(-1), "the replaced data is wiped");
    });
  }

  [TestCase(true, true)]
  [TestCase(false, true)]
  [TestCase(false, false)]
  public void GivenANestedFolder_WhenItIsRemoved_ThenEverythingInItIsGoneAndWiped(bool rockRidge, bool joliet) {
    var inner = Pattern(6000, 5);
    var kept = Pattern(3000, 6);
    using var image = Build(rockRidge, joliet, ("OLD/SUB/INNER.BIN", inner), ("OLD/X.TXT", [7]), ("KEPT/KEPT.BIN", kept));

    Iso.Remove(image, ["OLD"]);

    var files = Files(image);
    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "KEPT/KEPT.BIN" }));
      Assert.That(files["KEPT/KEPT.BIN"], Is.EqualTo(kept));
      Assert.That(IndexOf(image.ToArray(), inner.AsSpan(0, 64)), Is.EqualTo(-1), "the removed file's data is wiped");
      image.Position = 0;
      Assert.That(Iso.List(image, null).Any(e => e.Name.StartsWith("OLD", StringComparison.Ordinal)), Is.False, "the folder itself is gone");
    });
  }

  [Test]
  public void GivenNestedFiles_WhenPurged_ThenNothingIsLeftButTheVolumeKeepsItsLabel() {
    using var image = Build(rockRidge: true, joliet: true, ("x/y/z.txt", [1]), ("top.txt", [2]), ("x/w.txt", [3]));
    var label = image.ToArray().AsSpan(16 * 2048 + 40, 32).ToArray();

    Iso.Purge(image);

    image.Position = 0;
    Assert.Multiple(() => {
      Assert.That(Iso.List(image, null), Is.Empty);
      Assert.That(image.ToArray().AsSpan(16 * 2048 + 40, 32).ToArray(), Is.EqualTo(label));
    });
  }

  [Test]
  public void GivenAMissingName_WhenRemoved_ThenItIsReported() {
    using var image = Build(rockRidge: true, joliet: true, ("a/b.txt", [1]));
    Assert.Throws<FileNotFoundException>(() => Iso.Remove(image, ["a/missing.txt"]));
    Assert.Throws<FileNotFoundException>(() => Iso.Remove(image, ["nope/b.txt"]));
  }

  [Test]
  public void GivenAPathThroughAFile_WhenAdded_ThenItIsRefusedAndTheImageUnchanged() {
    using var image = Build(rockRidge: true, joliet: true, ("a/b.txt", [1]));
    var before = image.ToArray();
    Assert.Throws<IOException>(() => Add(image, "a/b.txt/c.txt", [2]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenDataAfterTheVolumeSpace_WhenAdded_ThenItIsRefusedAndTheImageUnchanged() {
    using var image = Build(rockRidge: true, joliet: true, ("a/b.txt", [1]));
    image.Seek(0, SeekOrigin.End);
    image.Write(Pattern(4096, 9));
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() => Add(image, "a/c.txt", [2]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAnEmptyFolderInput_WhenAdded_ThenTheFolderIsListed() {
    using var image = Build(rockRidge: true, joliet: true, ("a.txt", [1]));
    Iso.Add(image, [new ArchiveInputInfo("", "empty/inner/", true)]);
    image.Position = 0;
    var dirs = Iso.List(image, null).Where(e => e.IsDirectory).Select(e => e.Name.TrimEnd('/')).ToList();
    Assert.That(dirs, Is.SupersetOf(new[] { "empty", "empty/inner" }));
  }
}
