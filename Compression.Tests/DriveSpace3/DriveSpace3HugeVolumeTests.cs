using System.Text;
using Compression.Registry;
using Compression.Registry.Cvf;
using Compression.Tests.Support;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Large genuine DriveSpace 3 volumes — well past the old fixed ~512-cluster
/// limit — must round-trip through our reader and stay driver-mountable, with
/// the geometry (inner FAT16 + MDFAT) sized dynamically to the cluster count.
/// </summary>
[TestFixture]
[Category("Slow")]
public class DriveSpace3HugeVolumeTests {

  // Many files spanning multiple 32 KB clusters → well over 512 clusters total.
  private static (string Name, byte[] Data)[] BigSet(int files, int bytesEach, int seed) {
    var rnd = new Random(seed);
    var set = new (string, byte[])[files];
    for (var i = 0; i < files; i++) {
      var d = new byte[bytesEach];
      // semi-compressible: random head + zero tail, so clusters mix compressed/stored.
      rnd.NextBytes(d.AsSpan(0, bytesEach / 4));
      set[i] = ($"FILE{i:D4}.BIN", d);
    }
    return set;
  }

  [Test, Category("Huge")]
  public void Large_Volume_RoundTrips_ViaReader() {
    // 200 files × ~96 KB = ~600 clusters (past the old 512 cap); ~19 MB image.
    var files = BigSet(200, 96 * 1024, 1);
    var w = new GenuineDvr3Writer { CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 1 };
    foreach (var (n, d) in files) w.AddFile(n, d);
    var image = w.Build();

    using var r = new GenuineDvr3Reader(new MemoryStream(image));
    Assert.That(r.Entries, Has.Count.EqualTo(files.Length));
    foreach (var (name, data) in files)
      Assert.That(r.Extract(r.Entries.First(e => e.Name == name)), Is.EqualTo(data), $"{name} round-trip");
  }

  [Test, Category("Huge")]
  public void Large_Volume_FullReadWrite_AddRemove_ViaDescriptor() {
    // Full r/w (not WORM) at scale: create a ~600-cluster genuine volume, then
    // add and remove files through the descriptor (rebuild path) and verify.
    var files = BigSet(200, 96 * 1024, 5);
    var d = new DriveSpace3FormatDescriptor();
    var opts = new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compatibility"] = "Genuine" } };
    using var ms = new MemoryStream();
    d.Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToList(), opts);

    var extra = new byte[50000]; new Random(99).NextBytes(extra);
    d.Add(ms, [ArchiveInputInfo.InMemory("EXTRA.BIN", extra)]);
    d.Remove(ms, [files[100].Name]);

    // Read the modified large volume back through the genuine reader.
    using var r = new GenuineDvr3Reader(new MemoryStream(ms.ToArray()));
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName.ContainsKey(files[100].Name), Is.False, "removed file gone");
    Assert.That(byName["EXTRA.BIN"], Is.EqualTo(extra), "added file intact");
    Assert.That(byName[files[0].Name], Is.EqualTo(files[0].Data), "first file intact");
    Assert.That(byName[files[199].Name], Is.EqualTo(files[199].Data), "last file intact");
    Assert.That(byName, Has.Count.EqualTo(200), "200 files after add + remove");
  }

  [Test, Category("DriverProof")]
  public void Large_Volume_IsReadByRealDmsdosDriver() {
    var build = DmsdosCache.EnsureTools();
    if (build is null) Assert.Ignore("dmsdos unavailable.");

    var files = BigSet(120, 64 * 1024, 2);   // ~240 clusters, mixed
    var w = new GenuineDvr3Writer { CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 1 };
    foreach (var (n, d) in files) w.AddFile(n, d);
    var image = w.Build();

    var cvf = Path.Combine(Path.GetTempPath(), $"cwb-dvr3huge-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(cvf, image);
      var (_, det) = DmsdosCache.RunTool(DmsdosCache.CvfTest(build!), $"\"{cvf}\" -v");
      Assert.That(Encoding.ASCII.GetString(det), Does.Contain("drivespace 3 CVF"));
      // sample a handful across the volume
      foreach (var idx in new[] { 0, 37, 80, 119 }) {
        var (name, data) = files[idx];
        var (exit, raw) = DmsdosCache.RunTool(DmsdosCache.DcRead(build!), $"\"{cvf}\" /{name} raw");
        Assert.That(exit, Is.EqualTo(0), $"dcread {name}");
        var payload = DmsdosCache.PayloadAfterDiagnostics(raw);
        Assert.That(payload.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data), $"{name} byte-exact");
      }
    } finally { try { File.Delete(cvf); } catch { } }
  }
}
