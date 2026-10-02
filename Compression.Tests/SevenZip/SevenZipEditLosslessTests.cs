using System.Text;
using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Adding to and removing from a 7z archive keeps every property 7-Zip itself reports for
/// the entries the edit did not touch — modification, creation and access time, attributes,
/// size, CRC — on archives 7-Zip made with <c>-mtc=on -mta=on</c> and file attributes, for
/// both the in-place editors (plain header) and the metadata-preserving rewrite (compressed
/// header, solid folders). A header holding properties the codec does not interpret (7-Zip's
/// anti-items) is refused with the archive untouched.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class SevenZipEditLosslessTests {

  private static readonly string[] KeptFields = ["Path", "Folder", "Size", "Modified", "Created", "Accessed", "Attributes", "CRC", "Anti"];

  private string _work = "";

  [SetUp]
  public void SetUp() {
    FsInteropToolbox.Require7z();
    this._work = Path.Combine(Path.GetTempPath(), "cwb_7zedit_" + Guid.NewGuid().ToString("N")[..8]);
    var src = Path.Combine(this._work, "src");
    Directory.CreateDirectory(Path.Combine(src, "dir"));
    var time = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    Write("a.txt", Repeat("the quick brown fox\n", 500), time);
    Write("dir/b.txt", Repeat("lorem ipsum ", 700), time.AddHours(5));
    Write("c.bin", Repeat("0123456789abcdef", 900), time.AddDays(1));
    Write("hidden.txt", Repeat("hidden ", 50), time.AddDays(2));
    foreach (var (name, offset) in new[] { ("a.txt", 1), ("dir/b.txt", 2), ("c.bin", 3), ("hidden.txt", 4) }) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      File.SetCreationTimeUtc(path, time.AddMinutes(-offset));
      File.SetLastAccessTimeUtc(path, time.AddMinutes(offset * 7));
    }
    File.SetAttributes(Path.Combine(src, "hidden.txt"), FileAttributes.Hidden | FileAttributes.ReadOnly);
    Directory.SetLastWriteTimeUtc(Path.Combine(src, "dir"), time.AddDays(3));
  }

  [TearDown]
  public void TearDown() {
    try {
      foreach (var f in Directory.EnumerateFiles(this._work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
      Directory.Delete(this._work, recursive: true);
    } catch { /* best effort */ }
  }

  private void Write(string name, byte[] data, DateTime time) {
    var path = Path.Combine(this._work, "src", name.Replace('/', Path.DirectorySeparatorChar));
    File.WriteAllBytes(path, data);
    File.SetLastWriteTimeUtc(path, time);
  }

  private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

  private string Make(string name, string switches) {
    var archive = Path.Combine(this._work, name);
    var r = FsInteropToolbox.Run7z($"a -t7z {switches} -mtc=on -mta=on -mtm=on \"{archive}\" \"{Path.Combine(this._work, "src", "*")}\" -r");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z could not make {name}\n{r.StdOut}\n{r.StdErr}");
    return archive;
  }

  /// <summary>Every entry 7-Zip lists, as path → its content and metadata fields.</summary>
  private static Dictionary<string, string> Listing(string archive) {
    var r = FsInteropToolbox.Run7z($"l -slt \"{archive}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z cannot list {archive}\n{r.StdOut}\n{r.StdErr}");
    var body = r.StdOut.Replace("\r", "");
    var start = body.IndexOf("----------", StringComparison.Ordinal);
    Assert.That(start, Is.GreaterThanOrEqualTo(0), "7z printed no entry list");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var block in body[start..].Split("\n\n", StringSplitOptions.RemoveEmptyEntries)) {
      var fields = block.Split('\n').Select(l => l.Split(" = ", 2)).Where(kv => kv.Length == 2 && KeptFields.Contains(kv[0])).ToList();
      var path = fields.FirstOrDefault(kv => kv[0] == "Path")?[1];
      if (path is null) continue;
      result[path.Replace('\\', '/')] = string.Join("|", fields.Select(kv => $"{kv[0]}={kv[1]}"));
    }
    return result;
  }

  private static void AssertTestsClean(string archive) {
    var r = FsInteropToolbox.Run7z($"t \"{archive}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z t rejects {archive}\n{r.StdOut}\n{r.StdErr}");
  }

  private static void Edit(string archive, Action<SevenZipFormatDescriptor, Stream> edit) {
    using var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite);
    edit(new SevenZipFormatDescriptor(), stream);
  }

  private static IEnumerable<TestCaseData> Profiles() {
    yield return new TestCaseData("-mx=0 -mhc=off -ms=off").SetName("{m}_PlainHeader_InPlace");
    yield return new TestCaseData("-mx=5 -ms=on").SetName("{m}_CompressedHeaderSolid_Rewrite");
  }

  [TestCaseSource(nameof(Profiles))]
  public void Add_KeepsEveryPropertyOfTheEntriesAlreadyThere(string switches) {
    var archive = Make("add.7z", switches);
    var before = Listing(archive);
    Assume.That(before.Values, Has.Some.Contains("Accessed=").And.Some.Contains("Created="), "precondition: 7-Zip stored access and creation times");

    Edit(archive, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("added.txt", "added\n"u8)]));

    AssertTestsClean(archive);
    var after = Listing(archive);
    Assert.Multiple(() => {
      Assert.That(after.Keys, Does.Contain("added.txt"));
      foreach (var (path, fields) in before)
        Assert.That(after.GetValueOrDefault(path), Is.EqualTo(fields), path);
    });
  }

  [TestCaseSource(nameof(Profiles))]
  public void Remove_KeepsEveryPropertyOfTheEntriesThatStay(string switches) {
    var archive = Make("remove.7z", switches);
    var before = Listing(archive);

    Edit(archive, (d, s) => d.Remove(s, ["c.bin"]));

    AssertTestsClean(archive);
    var after = Listing(archive);
    before.Remove("c.bin");
    Assert.That(after, Is.EquivalentTo(before));
  }

  /// <summary>
  /// An update archive 7-Zip writes with <c>-u</c> records a deleted file as an anti-item —
  /// a FilesInfo property this codec does not interpret. An edit cannot carry it to the right
  /// entry after the file list changes, so it is refused and the archive left as it was.
  /// </summary>
  [Test]
  public void Add_ToAnArchiveWithAntiItems_IsRefusedAndLeftUntouched() {
    var baseArchive = Make("base.7z", "-mx=0 -mhc=off -ms=off");
    File.Delete(Path.Combine(this._work, "src", "c.bin"));
    var diff = Path.Combine(this._work, "diff.7z");
    var r = FsInteropToolbox.Run7z($"u \"{baseArchive}\" -u- \"-up0q3r2x2y2z0w2!{diff}\" \"{Path.Combine(this._work, "src", "*")}\" -r");
    Assume.That(r.ExitCode, Is.EqualTo(0), $"7z could not write an update archive\n{r.StdOut}\n{r.StdErr}");
    Assume.That(string.Join("\n", Listing(diff).Values), Does.Contain("Anti=+"), "precondition: 7-Zip wrote an anti-item");
    var original = File.ReadAllBytes(diff);

    Assert.Multiple(() => {
      Assert.That(() => Edit(diff, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("added.txt", "x"u8)])),
        Throws.TypeOf<NotSupportedException>().With.Message.Contains("file property 0x10"));
      Assert.That(() => Edit(diff, (d, s) => d.Remove(s, ["a.txt"])), Throws.TypeOf<NotSupportedException>());
      Assert.That(File.ReadAllBytes(diff), Is.EqualTo(original));
    });
  }
}
