using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Archives written by the reference zpaq 7.15 (see ReferenceVectors/README.md): every version of
/// every archive must list and extract to exactly what <c>manifest.tsv</c> records.
/// </summary>
[TestFixture]
public sealed class ZpaqReferenceVectorTests {
  private sealed record Row(string Archive, int Version, string Name, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => {
    using var reader = new StreamReader(Open("manifest.tsv"));
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r'))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split('\t'))
      .Select(f => new Row(f[0], int.Parse(f[1]), f[2], long.Parse(f[3]), f[4]))
      .ToArray();
  });

  private static Stream Open(string name) =>
    Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZpaqVectors.{name}")
    ?? throw new FileNotFoundException($"Embedded zpaq vector '{name}' is missing from the test assembly.");

  private static byte[] Load(string name) {
    using var s = Open(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<TestCaseData> ArchiveVersions() =>
    Manifest.Value.Select(r => (r.Archive, r.Version)).Distinct()
      .Select(v => new TestCaseData(v.Archive, v.Version).SetName($"{v.Archive} v{v.Version}"));

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(ArchiveVersions))]
  public void ZpaqArchive_ExtractsToManifest(string archive, int version) {
    using var r = new ZpaqReader(new MemoryStream(Load(archive)));
    var entries = r.GetEntries(version).ToDictionary(e => e.FileName);
    var expected = Manifest.Value.Where(m => m.Archive == archive && m.Version == version).ToArray();

    Assert.Multiple(() => {
      Assert.That(r.Errors, Is.Zero);
      foreach (var row in expected) {
        Assert.That(entries.ContainsKey(row.Name), $"{row.Name} is listed");
        if (!entries.TryGetValue(row.Name, out var e))
          continue;
        if (row.Size < 0) {
          Assert.That(e.IsDirectory, $"{row.Name} is a directory");
          continue;
        }
        var data = r.ExtractBytes(e);
        Assert.That(data.LongLength, Is.EqualTo(row.Size), $"{row.Name} size");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), $"{row.Name} content");
        if (e.Size >= 0)
          Assert.That(e.Size, Is.EqualTo(row.Size), $"{row.Name} listed size");
      }
      var files = entries.Values.Where(e => !e.IsDirectory).Select(e => e.FileName);
      Assert.That(files, Is.EquivalentTo(expected.Where(m => m.Size >= 0).Select(m => m.Name)), "no extra or missing files");
    });
  }

  [Test, Category("ThemVsUs")]
  public void Versions_RecordUpdateAndDeletion() {
    using var r = new ZpaqReader(new MemoryStream(Load("versions.zpaq")));
    Assert.Multiple(() => {
      Assert.That(r.VersionCount, Is.EqualTo(3));
      Assert.That(r.Entries.Select(e => e.FileName), Does.Not.Contain("v/notes.txt"), "deleted in version 3");
      Assert.That(r.GetEntries(2).Select(e => e.FileName), Does.Contain("v/notes.txt"));
      Assert.That(r.Entries.Single(e => e.FileName == "v/a.bin").Version, Is.EqualTo(2));
      Assert.That(r.GetVersionDate(1), Is.LessThan(r.GetVersionDate(3)!.Value));
    });
  }

  [Test, Category("ThemVsUs")]
  public void Dedup_SharesFragmentsBetweenIdenticalFiles([Values(0, 1, 2, 3, 4, 5)] int method) {
    using var r = new ZpaqReader(new MemoryStream(Load($"set.m{method}.zpaq")));
    var text = r.Entries.Single(e => e.FileName == "set/text.txt");
    var dup = r.Entries.Single(e => e.FileName == "set/dup.txt");
    Assert.That(dup.Fragments, Is.EqualTo(text.Fragments));
  }

  [Test, Category("ThemVsUs")]
  public void StreamingArchive_HasNoRecordedSizes() {
    using var r = new ZpaqReader(new MemoryStream(Load("stream.zpaq")));
    Assert.Multiple(() => {
      Assert.That(r.Entries.Select(e => e.FileName), Is.EquivalentTo(new[] { "st/one.txt", "st/two.bin" }));
      Assert.That(r.Entries.Select(e => e.Size), Is.All.EqualTo(-1));
    });
  }

  [Test, Category("ThemVsUs")]
  public void Descriptor_ListsAndExtractsZpaqArchive() {
    var d = new ZpaqFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_zpaq_ref_{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(Load("set.m3.zpaq")), dir, null, null);
      var expected = Manifest.Value.Where(m => m.Archive == "set.m3.zpaq" && m.Size >= 0);
      Assert.Multiple(() => {
        foreach (var row in expected)
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, row.Name)))), Is.EqualTo(row.Sha256), row.Name);
      });
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
