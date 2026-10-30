using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Scp;

namespace Compression.Tests.Scp;

[TestFixture]
public class ScpTests {
  /// <summary>A TRK block with <paramref name="revs"/> revolutions of the given flux intervals.</summary>
  internal static byte[] Track(int number, int revs, params uint[] flux) {
    var cells = new List<byte>();
    foreach (var f in flux) {
      var v = f;
      while (v >= 65536) { cells.Add(0); cells.Add(0); v -= 65536; }
      cells.Add((byte)(v >> 8)); cells.Add((byte)v);
    }
    var tdh = 4 + 12 * revs;
    var blob = new byte[tdh + cells.Count * revs];
    "TRK"u8.CopyTo(blob);
    blob[3] = (byte)number;
    for (var r = 0; r < revs; ++r) {
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4 + 12 * r), 8_000_000);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8 + 12 * r), (uint)(cells.Count / 2));
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12 + 12 * r), (uint)(tdh + r * cells.Count));
      cells.CopyTo(blob, tdh + r * cells.Count);
    }
    return blob;
  }

  private static byte[] Build(params (int Number, byte[] Blob)[] tracks) {
    var image = new ScpImage { Revolutions = 2, Tracks = new(tracks.ToDictionary(t => t.Number, t => t.Blob)) };
    using var ms = new MemoryStream();
    image.Write(ms);
    return ms.ToArray();
  }

  [Test]
  public void GivenTwoTracks_WhenWrittenAndRead_ThenLayoutChecksumAndFluxSurvive() {
    var t0 = Track(0, 2, 160, 240, 320, 70_000);
    var t3 = Track(3, 2, 200, 200);
    var scp = Build((0, t0), (3, t3));
    Assert.That(scp.AsSpan(0, 3).SequenceEqual("SCP"u8), Is.True);
    Assert.That(scp[5], Is.EqualTo(2));
    Assert.That(scp[6], Is.EqualTo(0));
    Assert.That(scp[7], Is.EqualTo(3));
    var sum = 0u;
    foreach (var b in scp.AsSpan(16)) sum += b;
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(scp.AsSpan(12)), Is.EqualTo(sum));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(scp.AsSpan(16 + 4)), Is.Zero, "absent track 1 has offset 0");

    var image = ScpImage.Read(scp);
    Assert.That(image.ChecksumValid, Is.True);
    Assert.That(image.Tracks.Keys, Is.EqualTo(new[] { 0, 3 }));
    Assert.That(image.Tracks[0], Is.EqualTo(t0));
    Assert.That(ScpImage.Flux(image.Tracks[0], 1), Is.EqualTo(new uint[] { 160, 240, 320, 70_000 }));
  }

  [Test]
  public void GivenTheDescriptor_WhenTracksAreReplacedAndRemoved_ThenTheImageFollows() {
    var scp = Build((0, Track(0, 2, 100)), (1, Track(1, 2, 200)));
    using var ms = new MemoryStream();
    ms.Write(scp);
    var d = new ScpFormatDescriptor();
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, Track(1, 2, 300, 400));
      d.Add(ms, [new ArchiveInputInfo(path, "tracks/track_001.bin", false)]);
      d.Remove(ms, ["tracks/track_000.bin"]);
    } finally {
      File.Delete(path);
    }
    var image = ScpImage.Read(ms.ToArray());
    Assert.That(image.Tracks.Keys, Is.EqualTo(new[] { 1 }));
    Assert.That(ScpImage.Flux(image.Tracks[1], 0), Is.EqualTo(new uint[] { 300, 400 }));
    Assert.That(d.List(new MemoryStream(ms.ToArray()), null).Select(e => e.Name), Does.Contain("tracks/track_001.bin"));
  }

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined() {
    var d = new ScpFormatDescriptor();
    Assert.That(d.CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);
    Assert.That(d.CanAccept(new ArchiveInputInfo("x", "tracks/track_007.bin", false), out _), Is.True);
  }

  [Test]
  public void GivenAWrongTrackNumberInItsHeader_WhenRead_ThenInvalidData() {
    var scp = Build((2, Track(2, 2, 100)));
    var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(scp.AsSpan(16 + 8));
    scp[off + 3] = 9;
    Assert.Throws<InvalidDataException>(() => ScpImage.Read(scp));
  }

  [Test]
  public void GivenTruncatedFlux_WhenRead_ThenInvalidDataAndListFallsBack() {
    var scp = Build((0, Track(0, 2, 100, 200, 300)))[..^3];
    Assert.Throws<InvalidDataException>(() => ScpImage.Read(scp));
    Assert.That(new ScpFormatDescriptor().List(new MemoryStream(scp), null).Single().Name, Is.EqualTo("FULL.scp"));
  }

  [Test]
  public void GivenABadChecksum_WhenRead_ThenItIsReportedNotFatal() {
    var scp = Build((0, Track(0, 2, 100)));
    scp[^1] ^= 1;
    Assert.That(ScpImage.Read(scp).ChecksumValid, Is.False);
  }

  [Test]
  public void GivenNoSignature_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => ScpImage.Read(new byte[0x300]));
}

/// <summary>
/// Greaseweazle (Keir Fraser, public domain) as the oracle: it writes an SCP from a sector image,
/// we take it apart into tracks and rebuild a fresh SCP from them, and Greaseweazle decodes our
/// rebuild back to the identical sector image. Editing (replacing one track with the same track of
/// another disk) has to show up as exactly that disk's sectors. The gw executable comes from
/// <c>CWB_GW</c>; skipped when unset unless <c>CWB_REQUIRE_GW=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ScpExternalConformanceTests {
  private static string RequireGw() {
    var gw = Environment.GetEnvironmentVariable("CWB_GW");
    if (!string.IsNullOrEmpty(gw)) return gw;
    const string hint = "Greaseweazle not configured: set CWB_GW to the gw executable (https://github.com/keirf/greaseweazle).";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_GW") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static int Gw(string gw, params string[] args) {
    var psi = new ProcessStartInfo(gw) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    // Greaseweazle reports progress on stderr: drain both pipes concurrently so neither fills up.
    var stderr = p.StandardError.ReadToEndAsync();
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    TestContext.Out.WriteLine($"gw {string.Join(' ', args)} -> {p.ExitCode}\n{stdout}{stderr.Result}");
    return p.ExitCode;
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-scpx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private byte[] Disk(int seed) {
    var d = new byte[737280];
    new Random(seed).NextBytes(d);
    return d;
  }

  private string GwScp(string gw, byte[] img, string name) {
    var imgPath = Path.Combine(this._tmp, name + ".img");
    File.WriteAllBytes(imgPath, img);
    var scp = Path.Combine(this._tmp, name + ".scp");
    Assert.That(Gw(gw, "convert", "--format", "ibm.720", imgPath, scp), Is.EqualTo(0));
    return scp;
  }

  private List<ArchiveInputInfo> TakeApart(string scp) {
    var dir = Path.Combine(this._tmp, "parts-" + Guid.NewGuid().ToString("N")[..8]);
    using (var fs = File.OpenRead(scp))
      new ScpFormatDescriptor().Extract(fs, dir, null, null);
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
      .Where(p => !p.EndsWith("footer.bin", StringComparison.Ordinal))
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(dir, p).Replace('\\', '/'), false)).ToList();
  }

  private byte[] GwImg(string gw, string scp) {
    var img = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(scp) + "-back.img");
    Assert.That(Gw(gw, "convert", "--format", "ibm.720", scp, img), Is.EqualTo(0));
    return File.ReadAllBytes(img);
  }

  [Test]
  public void GivenAGreaseweazleScp_WhenWeRebuildItFromItsTracks_ThenGreaseweazleDecodesTheSameSectors() {
    var gw = RequireGw();
    var disk = this.Disk(1);
    var theirs = this.GwScp(gw, disk, "theirs");
    var ours = Path.Combine(this._tmp, "ours.scp");
    using (var fs = File.Create(ours))
      new ScpFormatDescriptor().Create(fs, this.TakeApart(theirs), new FormatCreateOptions());
    Assert.That(this.GwImg(gw, ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneTrackReplacedFromAnotherDisk_WhenGreaseweazleDecodes_ThenExactlyThatTrackChanged() {
    var gw = RequireGw();
    var a = this.Disk(2);
    var b = this.Disk(3);
    var scpA = this.GwScp(gw, a, "a");
    var scpB = this.GwScp(gw, b, "b");
    var track5 = this.TakeApart(scpB).Single(i => i.ArchiveName == "tracks/track_005.bin");
    var edited = Path.Combine(this._tmp, "edited.scp");
    File.Copy(scpA, edited);
    using (var fs = File.Open(edited, FileMode.Open, FileAccess.ReadWrite))
      new ScpFormatDescriptor().Add(fs, [track5]);

    // ibm.720: 9 sectors × 512 bytes per track; track 5 = cylinder 2, head 1.
    var expected = (byte[])a.Clone();
    Array.Copy(b, 5 * 9 * 512, expected, 5 * 9 * 512, 9 * 512);
    Assert.That(this.GwImg(gw, edited), Is.EqualTo(expected));
  }
}
