using System.Text;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Subdirectory support for the OCFS2 writer. A file added with a path-separated
/// name such as "docs/api/reference.txt" must be placed inside real directory
/// dinodes ("docs", "docs/api") referenced from their parent's inline directory
/// data — not flattened into the root with its basename. The descriptor's reader
/// must in turn recurse the directory tree and surface every file at its full
/// nested path.
/// </summary>
[TestFixture]
public class Ocfs2SubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);

    // The reader surfaces every file at its full nested path.
    var entries = d.List(ms, null);
    var paths = entries.Select(e => e.Name.Replace('\\', '/')).ToHashSet();

    Assert.That(paths, Does.Contain("readme.txt"), "root file present");
    Assert.That(paths, Does.Contain("docs/guide.txt"), "one-level nested file present at its path");
    Assert.That(paths, Does.Contain("docs/api/reference.txt"), "two-level nested file present at its path");

    // Intact content at the nested paths.
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_sub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "readme.txt")),
                  Is.EqualTo("root file"u8.ToArray()), "root file content intact");
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "docs", "guide.txt")),
                  Is.EqualTo("in docs"u8.ToArray()), "one-level nested file content intact");
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "docs", "api", "reference.txt")),
                  Is.EqualTo("deep file"u8.ToArray()), "two-level nested file content intact");
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("Spec")]
  public void IntermediateDirectories_ExistAsDirectoryEntries() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);

    // Listing recurses into "docs" and "docs/api"; the leaf file appears at its
    // full path, which is only possible if both intermediate directories were
    // created as real dinodes and walked by the reader.
    var paths = d.List(ms, null).Select(e => e.Name.Replace('\\', '/')).ToHashSet();
    Assert.That(paths, Does.Contain("docs/api/reference.txt"),
                "intermediate directories docs and docs/api must exist and be recursed");
  }
}
