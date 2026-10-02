using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Maintenance;

/// <summary>
/// The staged maintenance operations judged by an outside reader: archives 7-Zip made —
/// ZIP with NTFS timestamp extra fields, 7z, gzip with a stored name — go through compress
/// or repack, and 7-Zip must then test them clean and list every entry with the same path,
/// size, times, attributes and CRC it listed before. What 7-Zip shows and our own reader
/// does not (the NTFS extra field, a gzip member's name) is exactly what this catches.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class MaintenanceSevenZipOracleTests {

  /// <summary>The <c>7z l -slt</c> fields that describe content and metadata; packing details may change.</summary>
  private static readonly string[] KeptFields = ["Path", "Folder", "Size", "Modified", "Created", "Accessed", "Attributes", "CRC", "Comment", "Encrypted"];

  private string _work = "";

  [SetUp]
  public void SetUp() {
    FsInteropToolbox.Require7z();
    FormatRegistration.EnsureInitialized();
    this._work = Path.Combine(Path.GetTempPath(), "cwb_maint7z_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(this._work, "src", "dir"));
    var time = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    WriteSource("a.txt", Repeat("the quick brown fox jumps over the lazy dog\n", 2000), time);
    WriteSource("dir/b.txt", Repeat("lorem ipsum dolor sit amet ", 1500), time.AddHours(5));
    WriteSource("c.bin", Repeat("0123456789abcdef", 4000), time.AddDays(1));
    Directory.SetLastWriteTimeUtc(Path.Combine(this._work, "src", "dir"), time.AddDays(2));
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._work, recursive: true); } catch { /* best effort */ }
  }

  private void WriteSource(string name, byte[] data, DateTime time) {
    var path = Path.Combine(this._work, "src", name.Replace('/', Path.DirectorySeparatorChar));
    File.WriteAllBytes(path, data);
    File.SetLastWriteTimeUtc(path, time);
  }

  private static byte[] Repeat(string text, int times) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

  private string Make(string name, string switches, string inputs = "*") {
    var archive = Path.Combine(this._work, name);
    var src = Path.Combine(this._work, "src");
    var r = FsInteropToolbox.Run7z($"a {switches} \"{archive}\" \"{Path.Combine(src, inputs)}\" -r");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z could not make {name}\n{r.StdOut}\n{r.StdErr}");
    return archive;
  }

  /// <summary>Every entry 7-Zip lists, reduced to the fields that must survive, in path order.</summary>
  private static List<string> Listing(string archive) {
    var r = FsInteropToolbox.Run7z($"l -slt \"{archive}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z cannot list {archive}\n{r.StdOut}\n{r.StdErr}");
    var body = r.StdOut.Replace("\r", "");
    var start = body.IndexOf("----------", StringComparison.Ordinal);
    Assert.That(start, Is.GreaterThanOrEqualTo(0), "7z printed no entry list");
    return [.. body[start..].Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
      .Select(block => string.Join("|", block.Split('\n')
        .Select(line => line.Split(" = ", 2))
        .Where(kv => kv.Length == 2 && KeptFields.Contains(kv[0]))
        .Select(kv => $"{kv[0]}={kv[1]}")))
      .Where(line => line.Contains("Path=", StringComparison.Ordinal))
      .Order(StringComparer.Ordinal)];
  }

  private static void AssertTestsClean(string archive) {
    var r = FsInteropToolbox.Run7z($"t \"{archive}\"");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z t rejects {archive}\n{r.StdOut}\n{r.StdErr}");
  }

  [TestCase("weak.zip", "-tzip -mx=1 -mtc=on", TestName = "Compress_ZipWithNtfsTimes_Keeps7zListing")]
  [TestCase("stored.zip", "-tzip -mx=0", TestName = "Compress_StoredZip_Keeps7zListing")]
  [TestCase("stored.7z", "-t7z -mx=0", TestName = "Compress_Stored7z_Keeps7zListing")]
  public void Compress_KeepsEverything7ZipLists(string name, string switches) {
    var archive = Make(name, switches);
    var before = Listing(archive);
    var size = new FileInfo(archive).Length;

    var result = MaintenanceOperations.Compress(archive, archive);

    AssertTestsClean(archive);
    Assert.Multiple(() => {
      Assert.That(Listing(archive), Is.EqualTo(before));
      Assert.That(result.NewSize, Is.LessThan(size), "a weakly compressed archive must get smaller");
    });
  }

  [Test]
  public void Compress_GzipWithAStoredName_Keeps7zListing() {
    var archive = Make("a.txt.gz", "-tgzip -mx=1", "a.txt");
    var before = Listing(archive);

    MaintenanceOperations.Compress(archive, archive);

    AssertTestsClean(archive);
    Assert.That(Listing(archive), Is.EqualTo(before));
  }

  [Test]
  public void Repack_ZipWithARemovedEntry_Keeps7zListingAndShrinks() {
    var archive = Make("holed.zip", "-tzip -mx=5 -mtc=on");
    using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite))
      FileFormat.Zip.ZipModifier.RemoveFile(stream, "c.bin");
    var before = Listing(archive);
    var size = new FileInfo(archive).Length;

    var result = MaintenanceOperations.Repack(archive, archive);

    AssertTestsClean(archive);
    Assert.Multiple(() => {
      Assert.That(Listing(archive), Is.EqualTo(before));
      Assert.That(result.NewSize, Is.LessThan(size));
    });
  }

  [Test]
  public void Compress_7zWithAccessAndCreationTimes_Keeps7zListing() {
    var archive = Make("atime.7z", "-t7z -mx=0 -mtc=on -mta=on");
    var before = Listing(archive);
    Assume.That(string.Join("|", before), Does.Contain("Accessed="), "precondition: 7-Zip stored access times");

    MaintenanceOperations.Compress(archive, archive);

    AssertTestsClean(archive);
    Assert.That(Listing(archive), Is.EqualTo(before));
  }
}
