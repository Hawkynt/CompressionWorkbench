using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Fossil;

namespace Compression.Tests.Fossil;

/// <summary>
/// The Fossil writer. The oracle is plan9port's own <c>fossil</c> server: it opens the volume we
/// write and serves its active tree over 9P (ReferenceVectors/serve-and-read.sh), which must equal
/// what we put in, sizes, contents and modes.
/// </summary>
[TestFixture]
public class FossilWriterTests {
  private const uint When = 1714979290;

  private static byte[] Noise(int n, int seed) {
    var b = new byte[n];
    new Random(seed).NextBytes(b);
    return b;
  }

  /// <summary>Nested and empty directories, an empty file, files across one and several pointer levels, a full meta block.</summary>
  internal static List<FossilWriteNode> Tree(int blockSize) => [
    new("docs", true) {
      Permissions = 0x1FD, MTime = When,
      Children = [
        new("notes.txt", false) { Data = "private notes\n"u8.ToArray(), Permissions = 0x180, MTime = When },
        new("deep", true) { Permissions = 0x1ED, MTime = When, Children = [new("leaf.dat", false) { Data = Noise(5000, 1), MTime = When }] },
        new("many", true) { MTime = When, Children = [.. Enumerable.Range(0, blockSize / 100 + 3).Select(i => new FossilWriteNode($"f{i:D3}.txt", false) { Data = [(byte)i], MTime = When })] },
      ],
    },
    new("empty-dir", true) { Permissions = 0x1ED, MTime = When },
    new("empty", false) { MTime = When },
    new("hello.txt", false) { Data = "hello from our fossil writer\n"u8.ToArray(), MTime = When },
    new("one-block.bin", false) { Data = Noise(blockSize, 2), MTime = When },
    new("two-levels.bin", false) { Data = Noise(blockSize * (blockSize / 20 + 5), 3), MTime = When },
  ];

  private static byte[] Volume(int blockSize) {
    using var ms = new MemoryStream();
    FossilWriter.Write(ms, Tree(blockSize), blockSize, When);
    return ms.ToArray();
  }

  /// <summary>Every file and directory of the tree: "D path" or "F path size sha256", sorted.</summary>
  private static List<string> Expected(int blockSize) {
    var lines = new List<string>();
    void Walk(IEnumerable<FossilWriteNode> nodes, string prefix) {
      foreach (var n in nodes) {
        var path = prefix + n.Name;
        if (n.IsDirectory) {
          lines.Add($"D {path}");
          Walk(n.Children, path + "/");
        } else
          lines.Add($"F {path} {n.Data.Length} {Convert.ToHexStringLower(SHA256.HashData(n.Data))}");
      }
    }
    Walk(Tree(blockSize), "");
    lines.Sort(StringComparer.Ordinal);
    return lines;
  }

  [TestCase(4096)]
  [TestCase(8192)]
  [Category("RoundTrip")]
  public void GivenATree_WhenWrittenAndReadByOurReader_ThenEveryFileAndDirectoryComesBack(int blockSize) {
    var r = new FossilReader(new MemoryStream(Volume(blockSize)));
    var ours = new List<string>();
    foreach (var e in r.Entries.Where(e => e.Path.StartsWith("active/", StringComparison.Ordinal))) {
      var path = e.Path["active/".Length..];
      ours.Add(e.IsDirectory ? $"D {path}" : $"F {path} {e.Size} {Convert.ToHexStringLower(SHA256.HashData(r.Extract(e)))}");
    }
    ours.Sort(StringComparer.Ordinal);
    Assert.That(ours, Is.EqualTo(Expected(blockSize)));
    Assert.That(r.Entries.Where(e => !e.Path.Contains('/')).Select(e => e.Path), Is.EquivalentTo(new[] { "active", "archive", "snapshot" }));
  }

  [TestCase("", TestName = "Empty Fossil name")]
  [TestCase("a/b", TestName = "Fossil name with a slash")]
  [TestCase("..", TestName = "Dot-dot Fossil name")]
  [Category("Exception")]
  public void GivenAnUnstorableName_WhenWritten_ThenRefused(string name)
    => Assert.Throws<ArgumentException>(() => FossilWriter.Write(new MemoryStream(), [new(name, false)]));

  [Test, Category("Exception")]
  public void GivenADuplicateName_WhenWritten_ThenRefused()
    => Assert.Throws<ArgumentException>(() => FossilWriter.Write(new MemoryStream(), [new("a", false), new("a", true)]));

  [Test, Category("HappyPath")]
  public void GivenInputs_WhenCreatedByTheDescriptor_ThenTheyAreTheActiveTree() {
    using var ms = new MemoryStream();
    new FossilFormatDescriptor().Create(ms, [
      new ArchiveInputInfo("", "docs/", true),
      ArchiveInputInfo.InMemory("docs/readme.txt", "hello fossil\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("top.bin", [1, 2, 3]),
    ], new FormatCreateOptions());
    ms.Position = 0;
    var listing = new FossilFormatDescriptor().List(ms, null).Select(e => $"{e.Name}|{e.OriginalSize}");
    Assert.That(listing, Is.SupersetOf(new[] { "active/docs|0", "active/docs/readme.txt|13", "active/top.bin|3" }));
  }

  // ── plan9port's fossil server as the oracle ───────────────────────────────

  [TestCase(4096)]
  [TestCase(8192)]
  [Category("ExternalInterop")]
  public void GivenOurVolume_WhenPlan9portsFossilServesIt_ThenItServesTheTree(int blockSize) {
    if (!FsInteropToolbox.WslHasTool("$HOME/plan9port/bin/fossil/fossil")) Assert.Ignore("plan9port's fossil is not built in WSL (~/plan9port).");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-fossil-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var image = Path.Combine(dir, "ours.fossil");
      File.WriteAllBytes(image, Volume(blockSize));
      var script = FsInteropToolbox.WinToWsl(Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Fossil", "ReferenceVectors", "serve-and-read.sh"));
      var changed = Path.Combine(dir, "changed.fossil");
      var run = FsInteropToolbox.RunWsl($"bash {script} {FsInteropToolbox.WinToWsl(image)} {FsInteropToolbox.WinToWsl(changed)}");
      var lines = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
      var served = lines.TakeWhile(l => !l.StartsWith("== ", StringComparison.Ordinal))
        .Select(l => l.Split(' ')).Select(p => p[0] == "D" ? $"D {p[1]}" : $"F {p[1]} {p[2]} {p[3]}").OrderBy(l => l, StringComparer.Ordinal).ToList();
      Assert.That(served, Is.EqualTo(Expected(blockSize)), run.StdOut + run.StdErr);
      // modes as served: docs/notes.txt 0600, docs/deep 0755, files 0664
      Assert.That(lines, Has.Some.EqualTo($"F docs/notes.txt 14 {Convert.ToHexStringLower(SHA256.HashData("private notes\n"u8.ToArray()))} --rw-------"));
      Assert.That(lines, Has.Some.EqualTo("D docs/deep d-rwxr-xr-x"));

      // the server also wrote a file into our volume; we read the volume it left
      var added = "written by the fossil server\n"u8.ToArray();
      Assert.That(lines, Has.Some.EqualTo($"== added {Convert.ToHexStringLower(SHA256.HashData(added))}"), run.StdOut);
      var after = new FossilReader(new MemoryStream(File.ReadAllBytes(changed)));
      Assert.That(after.Extract(after.Entries.Single(e => e.Path == "active/added.txt")), Is.EqualTo(added));
      Assert.That(after.Extract(after.Entries.Single(e => e.Path == "active/hello.txt")), Is.EqualTo("hello from our fossil writer\n"u8.ToArray()));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
