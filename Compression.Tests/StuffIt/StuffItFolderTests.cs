using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// Folders in both StuffIt writers. The Unarchiver's lsar must list the tree we wrote and its unar
/// must extract every file byte for byte.
/// </summary>
[TestFixture]
public class StuffItFolderTests {
  private static readonly DateTime When = new(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);

  private static byte[] Text(int length, int seed) {
    var r = new Random(seed);
    var sb = new StringBuilder();
    while (sb.Length < length) sb.Append("lorem ipsum dolor sit amet consectetur adipiscing elit ".Split(' ')[r.Next(8)]).Append(' ');
    return Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  private static readonly (string Path, byte[] Data, byte[]? Resource)[] Files = [
    ("top.txt", Text(3_000, 1), null),
    ("docs/readme.txt", Text(9_000, 2), Text(700, 3)),
    ("docs/sub/deep.bin", Text(20_000, 4), null),
    ("docs/sub/tiny.txt", "x"u8.ToArray(), null),
    ("other/a.txt", Text(500, 5), null),
  ];

  private static readonly string[] Folders = ["docs", "docs/sub", "other", "empty", "empty/nested"];

  private static byte[] Write(bool stuffIt5, StuffItCompression compression = StuffItCompression.Smallest) {
    using var ms = new MemoryStream();
    if (stuffIt5) {
      using var w = new StuffIt5Writer(ms, compression, leaveOpen: true);
      foreach (var f in Files) w.AddFile(f.Path, f.Data, f.Resource, lastModified: When);
      w.AddDirectory("empty/nested", When);
    } else {
      using var w = new StuffItWriter(ms, compression, leaveOpen: true);
      foreach (var f in Files) w.AddFileWithResourceFork(f.Path, f.Data, f.Resource ?? [], lastModified: When);
      w.AddDirectory("empty/nested", When);
    }
    return ms.ToArray();
  }

  [TestCase(false), TestCase(true), Category("RoundTrip")]
  public void GivenNestedFolders_WhenReadBack_ThenTreeAndForksMatch(bool stuffIt5) {
    using var r = new StuffItReader(new MemoryStream(Write(stuffIt5)));
    Assert.That(r.Entries.Where(e => e.IsDirectory).Select(e => e.FileName), Is.EquivalentTo(Folders));
    foreach (var f in Files) {
      var e = r.Entries.Single(x => x.FileName == f.Path);
      Assert.That(r.Extract(e), Is.EqualTo(f.Data), f.Path);
      if (f.Resource != null) Assert.That(r.ExtractResourceFork(e), Is.EqualTo(f.Resource), f.Path);
    }
  }

  [TestCase(false), TestCase(true), Category("HappyPath")]
  public void GivenDescriptorInputsWithFolders_WhenCreated_ThenPathsSurvive(bool stuffIt5) {
    var options = stuffIt5 ? new FormatCreateOptions { FormatSpecific = new() { [FormatOptionKeys.TargetCompatibility] = "StuffIt5" } } : new FormatCreateOptions();
    using var ms = new MemoryStream();
    new StuffItFormatDescriptor().Create(ms, [
      new ArchiveInputInfo("d", "d", true),
      new ArchiveInputInfo("d/e.txt", "d/e.txt", false, "hello"u8.ToArray()),
    ], options);
    ms.Position = 0;
    var list = new StuffItFormatDescriptor().List(ms, null);
    Assert.That(list.Select(e => (e.Name, e.IsDirectory)), Is.EquivalentTo(new[] { ("d", true), ("d/e.txt", false) }));
  }

  [Test, Category("Exception")]
  public void GivenAPathThatIsBothFileAndFolder_WhenAdded_ThenRefused() {
    using var ms = new MemoryStream();
    var w = new StuffItWriter(ms, leaveOpen: true);
    w.AddFile("a", [1]);
    Assert.That(() => w.AddFile("a/b", [2]), Throws.ArgumentException);
    Assert.That(() => w.AddFile("a", [3]), Throws.ArgumentException);
    Assert.That(() => w.AddFile("x/" + new string('n', 32), [3]), Throws.InstanceOf<NotSupportedException>());
  }

  // ── The Unarchiver ───────────────────────────────────────────────────────

  /// <summary>lsar writes its flags as 0/1 numbers.</summary>
  private static bool Flag(JsonElement entry, string name)
    => entry.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0);

  [TestCase(false, StuffItCompression.Smallest), TestCase(true, StuffItCompression.Smallest)]
  [TestCase(false, StuffItCompression.Arsenic), TestCase(true, StuffItCompression.Arsenic)]
  [Category("ExternalInterop")]
  public void GivenOurArchiveWithFolders_WhenLsarListsAndUnarExtracts_ThenTreeAndBytesMatch(bool stuffIt5, StuffItCompression compression) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar") || !FsInteropToolbox.WslHasTool("lsar")) Assert.Ignore("unar/lsar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitdir_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, stuffIt5 ? "tree5.sit" : "tree.sit");
      File.WriteAllBytes(path, Write(stuffIt5, compression));

      var listing = FsInteropToolbox.RunWsl($"lsar -j '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(listing.ExitCode, Is.EqualTo(0), listing.StdErr);
      using var json = JsonDocument.Parse(listing.StdOut);
      var listed = json.RootElement.GetProperty("lsarContents").EnumerateArray()
        .Select(e => (Name: e.GetProperty("XADFileName").GetString()!,
                      Dir: Flag(e, "XADIsDirectory"),
                      Res: Flag(e, "XADIsResourceFork")))
        .ToList();
      Assert.That(listed.Where(x => x.Dir).Select(x => x.Name), Is.EquivalentTo(Folders), "lsar's folders");
      Assert.That(listed.Where(x => !x.Dir && !x.Res).Select(x => x.Name), Is.EquivalentTo(Files.Select(f => f.Path)), "lsar's files");
      Assert.That(listed.Where(x => x.Res).Select(x => x.Name), Is.EquivalentTo(Files.Where(f => f.Resource != null).Select(f => f.Path)), "lsar's resource forks");

      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k visible -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdErr);
      foreach (var f in Files)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(f.Data), f.Path);
      Assert.That(Directory.Exists(Path.Combine(outDir, "empty", "nested")), Is.True, "the empty folder");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
