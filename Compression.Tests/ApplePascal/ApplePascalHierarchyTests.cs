#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

/// <summary>
/// Apple UCSD Pascal is flat-only by spec — there is no parent-pointer in the
/// 26-byte directory entry, so subdirectories cannot be expressed. The "hierarchy"
/// test here is therefore a flat round trip asserting List() returns N files all
/// with IsDirectory == false.
/// </summary>
[TestFixture]
public class ApplePascalHierarchyTests {

  private static byte[] BuildFlatImage() {
    var w = new ApplePascalWriter();
    w.AddFile("FILE1.TXT", Encoding.ASCII.GetBytes("First file"));
    w.AddFile("FILE2.TXT", Encoding.ASCII.GetBytes("Second file"));
    w.AddFile("FILE3.DAT", Encoding.ASCII.GetBytes("Third file payload"));
    w.AddFile("FILE4.DAT", Encoding.ASCII.GetBytes("Fourth file"));
    w.AddFile("FILE5.DAT", Encoding.ASCII.GetBytes("Fifth file"));
    return w.Build();
  }

  [Test]
  public void List_FlatVolume_ReturnsFiveFiles_AllNonDirectory() {
    var d = new ApplePascalFormatDescriptor();
    using var ms = new MemoryStream(BuildFlatImage());
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(5));
    Assert.That(entries.All(e => !e.IsDirectory), Is.True,
                "Apple Pascal is flat by spec — every entry must be IsDirectory=false.");
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FILE1.TXT"));
    Assert.That(names, Does.Contain("FILE5.DAT"));
  }

  [Test]
  public void Extract_FlatVolume_WritesAllFilesAtRoot() {
    var d = new ApplePascalFormatDescriptor();
    using var ms = new MemoryStream(BuildFlatImage());
    var tmp = Path.Combine(Path.GetTempPath(), $"applepascal-flat-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      d.Extract(ms, tmp, null, null);
      // No subdirectories created.
      Assert.That(Directory.GetDirectories(tmp), Is.Empty);
      Assert.That(File.Exists(Path.Combine(tmp, "FILE1.TXT")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "FILE5.DAT")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test]
  public void SubdirInputName_FlattensToLeafName() {
    // Sanity-check: the writer strips path components and stores the leaf name
    // (uppercased) so external tools can find the file even when the caller
    // happened to pass a slashed name.
    var w = new ApplePascalWriter();
    w.AddFile("subdir/leaf.txt", Encoding.ASCII.GetBytes("flat"));
    var img = w.Build();
    using var r = new ApplePascalReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("LEAF.TXT"), "Subdir path components must be stripped to flat leaf.");
  }
}
