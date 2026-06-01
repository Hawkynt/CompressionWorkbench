#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

/// <summary>
/// TI-99/4A is flat by spec — the FDIR is an array of FDR pointers with no
/// directory concept. The "hierarchy" test is therefore a flat round-trip
/// asserting List() returns all files with IsDirectory==false.
/// </summary>
[TestFixture]
public class Ti99HierarchyTests {

  private static byte[] BuildFlatSectorDump() {
    var w = new Ti99Writer();
    w.AddFile("FILE1", Encoding.ASCII.GetBytes("First TI-99 file"));
    w.AddFile("FILE2", Encoding.ASCII.GetBytes("Second TI-99 file"));
    w.AddFile("FILE3", Encoding.ASCII.GetBytes("Third payload"));
    w.AddFile("FILE4", Encoding.ASCII.GetBytes("Fourth"));
    w.AddFile("FILE5", Encoding.ASCII.GetBytes("Fifth file"));
    return w.BuildSectorDump();
  }

  [Test]
  public void SectorDump_List_ReturnsFiveFiles_AllNonDirectory() {
    var d = new Ti99FormatDescriptor();
    using var ms = new MemoryStream(BuildFlatSectorDump());
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(5));
    Assert.That(entries.All(e => !e.IsDirectory), Is.True,
                "TI-99 is flat by spec — every entry must be IsDirectory=false.");
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FILE1"));
    Assert.That(names, Does.Contain("FILE5"));
  }

  [Test]
  public void SectorDump_Extract_WritesAllFilesAtRoot() {
    var d = new Ti99FormatDescriptor();
    using var ms = new MemoryStream(BuildFlatSectorDump());
    var tmp = Path.Combine(Path.GetTempPath(), $"ti99-flat-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      d.Extract(ms, tmp, null, null);
      Assert.That(Directory.GetDirectories(tmp), Is.Empty);
      Assert.That(File.Exists(Path.Combine(tmp, "FILE1")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "FILE5")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test]
  public void TIFiles_SingleFileWrapper_RoundTrips() {
    var w = new Ti99Writer();
    var payload = Encoding.ASCII.GetBytes("Single TIFiles payload");
    w.AddFile("LONELY", payload);
    var img = w.BuildTifiles();

    using var ms = new MemoryStream(img);
    using var r = new Ti99Reader(ms);
    Assert.That(r.IsTifilesWrapper, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("LONELY"));
    var got = r.Extract(r.Entries[0]);
    Assert.That(got.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }
}
