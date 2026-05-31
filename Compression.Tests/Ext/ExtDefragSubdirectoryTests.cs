#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Ext;

/// <summary>
/// Defragmenting a non-block-mover filesystem (ext) must preserve a nested
/// directory tree. The rebuild-based defrag path reads every live file at its
/// full path and rebuilds the image with the subdir-aware writer, so the tree
/// structure has to survive a defragment round-trip with every nested file
/// intact at its exact location.
/// </summary>
[TestFixture]
public class ExtDefragSubdirectoryTests {

  private static Dictionary<string, byte[]> ExtractAll(MemoryStream ms) {
    ms.Position = 0;
    var r = new FileSystem.Ext.ExtReader(ms);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  [Test, Category("RoundTrip")]
  public void RebuildDefrag_PreservesNestedTree() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("readme.txt", Encoding.ASCII.GetBytes("root readme"));
    w.AddFile("docs/guide.txt", Encoding.ASCII.GetBytes("guide body"));
    w.AddFile("docs/api/reference.txt", Encoding.ASCII.GetBytes("reference body"));
    w.AddFile("src/lib/util.txt", Encoding.ASCII.GetBytes("utility body"));
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var before = ExtractAll(ms);

    // Exercise the generic rebuild path directly with the ext reader+writer
    // delegates (the same ones ExtFormatDescriptor's rebuild fallback uses):
    // it reads every live file at its full path and re-packs through the
    // subdir-aware writer, so the nested tree must survive.
    DefragRebuilder.Rebuild(ms,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart },
      readEntries: stream => {
        var r = new FileSystem.Ext.ExtReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var ew = new FileSystem.Ext.ExtWriter();
        foreach (var (n, d) in files) ew.AddFile(n, d);
        return ew.Build();
      });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "every nested file survives the defrag");
    foreach (var (path, data) in before) {
      Assert.That(after, Contains.Key(path), $"nested file {path} still present at its path");
      Assert.That(after[path], Is.EqualTo(data), $"nested file {path} content intact");
    }

    // The intermediate directories must still exist as directories.
    ms.Position = 0;
    var r = new FileSystem.Ext.ExtReader(ms);
    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name.Replace('\\', '/')).ToHashSet();
    Assert.That(dirs, Contains.Item("docs"), "intermediate directory 'docs' preserved");
    Assert.That(dirs, Contains.Item("docs/api"), "nested directory 'docs/api' preserved");
    Assert.That(dirs, Contains.Item("src/lib"), "nested directory 'src/lib' preserved");
  }
}
