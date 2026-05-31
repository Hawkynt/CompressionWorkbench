namespace Compression.Tests.Btrfs;

/// <summary>
/// Subdirectory support for the Btrfs writer. A file added with a path that
/// contains separators must be placed inside real directory inodes — one
/// inode per path component — rather than flattened into the FS-tree root.
/// Each intermediate directory carries its own INODE_ITEM (mode S_IFDIR) with
/// DIR_ITEM / DIR_INDEX entries in its parent and an INODE_REF back to that
/// parent, so the tree round-trips through <see cref="FileSystem.Btrfs.BtrfsReader"/>
/// at the exact nested path.
/// </summary>
[TestFixture]
public class BtrfsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Btrfs.BtrfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();

    Assert.That(files.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(files.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(dirs.Contains("docs"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirs.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists");

    Assert.That(files["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested content intact");
    Assert.That(files["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested content intact");
  }
}
