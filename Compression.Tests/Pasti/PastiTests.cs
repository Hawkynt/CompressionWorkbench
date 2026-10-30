using System.Buffers.Binary;
using System.Security.Cryptography;
using FileFormat.Pasti;

namespace Compression.Tests.Pasti;

[TestFixture]
public class PastiTests {
  internal static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Pasti", "ReferenceVectors", name);

  private static List<PastiTrack> Head() => PastiImage.Read(File.ReadAllBytes(VectorPath("UnionDemo-head.stx"))).Tracks;

  [Test]
  public void GivenTheUnionDemoHead_WhenRead_ThenTrackAndSectorLayoutMatchTheRecords() {
    var tracks = Head();
    Assert.That(tracks, Has.Count.EqualTo(3));
    Assert.That(tracks.Select(t => (t.Cylinder, t.Head)), Is.EqualTo(new[] { (0, 0), (0, 1), (1, 0) }));
    Assert.That(tracks[0].Sectors.Select(s => s.IdNumber), Is.EqualTo(Enumerable.Range(1, 9).Select(i => (byte)i)));
    Assert.That(tracks[0].Sectors, Has.All.Matches<PastiSector>(s => s.IdSize == 2 && s.Data.Length == 512 && s.FdcStatus == 0));
    // The protection: five 1024-byte sectors and one 512-byte sector on every other track.
    Assert.That(tracks[1].Sectors.Select(s => s.Data.Length), Is.EqualTo(new[] { 1024, 1024, 1024, 1024, 1024, 512 }));
  }

  [Test]
  public void GivenTheBootSector_WhenRead_ThenItIsAValidAtariStBootSector() {
    var boot = Head()[0].Sectors[0].Data;
    // The ST boot checksum: the big-endian word sum of a bootable sector is 0x1234.
    var sum = 0;
    for (var i = 0; i < 512; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(boot.AsSpan(i));
    Assert.That(sum & 0xFFFF, Is.EqualTo(0x1234));
  }

  [Test]
  public void GivenAProtectedDisk_WhenListed_ThenNoPlainImageIsInvented() {
    var names = new PastiFormatDescriptor().List(File.OpenRead(VectorPath("UnionDemo-head.stx")), null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("disk.st"));
    Assert.That(names, Does.Contain("tracks/c00_h1/s001.bin").And.Contain("metadata.ini"));
  }

  [Test]
  public void GivenStandardTracksWithoutDescriptors_WhenRead_ThenAPlainImageIsBuilt() {
    using var ms = new MemoryStream();
    ms.Write("RSY\0"u8);
    ms.Write(new byte[] { 3, 0, 1, 0, 0, 0, 2, 2, 0, 0, 0, 0 });
    for (var t = 0; t < 2; ++t) {
      var h = new byte[16];
      BinaryPrimitives.WriteUInt32LittleEndian(h, 16 + 9 * 512);
      BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), 9);
      h[14] = (byte)(t == 0 ? 0x00 : 0x80);
      ms.Write(h);
      for (var s = 0; s < 9; ++s) ms.Write(Enumerable.Repeat((byte)(t * 16 + s), 512).ToArray());
    }
    var tracks = PastiImage.Read(ms.ToArray()).Tracks;
    var st = PastiImage.StandardImage(tracks);
    Assert.That(st, Has.Length.EqualTo(2 * 9 * 512));
    Assert.That(st![9 * 512], Is.EqualTo(16), "side 1 follows side 0 of cylinder 0");
  }

  [Test]
  public void GivenATruncatedRecord_WhenRead_ThenInvalidDataAndListFallsBack() {
    var stx = File.ReadAllBytes(VectorPath("UnionDemo-head.stx"))[..^100];
    Assert.Throws<InvalidDataException>(() => PastiImage.Read(stx));
    Assert.That(new PastiFormatDescriptor().List(new MemoryStream(stx), null).Single().Name, Is.EqualTo("FULL.stx"));
  }

  [Test]
  public void GivenAnotherVersion_WhenRead_ThenNotSupported() {
    var stx = File.ReadAllBytes(VectorPath("UnionDemo-head.stx"));
    stx[4] = 2;
    Assert.Throws<NotSupportedException>(() => PastiImage.Read(stx));
  }

  [Test]
  public void GivenNoSignature_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => PastiImage.Read(new byte[32]));
}

/// <summary>
/// MAME floptool (mame-tools) as the oracle on the complete Union Demo, the sample the Pasti
/// authors publish (pasti.fxatari.com/downloads/UnionDemo.zip, SHA-256 pinned): floptool's own
/// Pasti loader regenerates the disk and its ST writer recovers the standard track 0 side 0
/// sectors, which have to be the bytes we read from the records. Needs floptool and the sample
/// (<c>CWB_UNIONDEMO</c> → UnionDemo.stx); skipped otherwise unless <c>CWB_REQUIRE_FLOPTOOL=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class PastiExternalConformanceTests {
  private const string UnionDemoSha256 = "a6199cd521939eba61bab70b0a0f0d4b1d72043b2eabbbe6f15e6cb1564283e4";

  [Test]
  public void GivenTheUnionDemo_WhenFloptoolRecoversTrackZero_ThenItIsTheSectorsWeRead() {
    var stx = Environment.GetEnvironmentVariable("CWB_UNIONDEMO");
    var ready = FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("floptool") && !string.IsNullOrEmpty(stx) && File.Exists(stx);
    if (!ready) {
      const string hint = "needs floptool (`sudo apt-get install -y mame-tools`) and CWB_UNIONDEMO pointing at UnionDemo.stx.";
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_FLOPTOOL") == "1") Assert.Fail(hint);
      Assert.Ignore(hint);
    }
    var bytes = File.ReadAllBytes(stx!);
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(UnionDemoSha256), "not the published Union Demo");

    var dir = Path.Combine(Path.GetTempPath(), "cwb-pastix-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var src = Path.Combine(dir, "union.stx");
      File.WriteAllBytes(src, bytes);
      var st = Path.Combine(dir, "union.st");
      var r = FsInteropToolbox.RunWsl($"floptool flopconvert pasti st {FsInteropToolbox.WinToWsl(src)} {FsInteropToolbox.WinToWsl(st)}");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.Exists(st), Is.True, "floptool wrote no ST image");
      var theirs = File.ReadAllBytes(st);
      var ours = PastiImage.Read(bytes).Tracks.Single(t => t.Cylinder == 0 && t.Head == 0).Sectors
        .OrderBy(s => s.IdNumber).SelectMany(s => s.Data).ToArray();
      Assert.That(theirs.AsSpan(0, ours.Length).ToArray(), Is.EqualTo(ours));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
