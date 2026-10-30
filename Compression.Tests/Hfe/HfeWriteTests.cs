using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Hfe;

namespace Compression.Tests.Hfe;

[TestFixture]
public class HfeWriteTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-hfew-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private ArchiveInputInfo Input(string name, byte[] data) {
    var path = Path.Combine(this._tmp, Guid.NewGuid().ToString("N"));
    File.WriteAllBytes(path, data);
    return new ArchiveInputInfo(path, name, false);
  }

  private Dictionary<string, byte[]> Extract(byte[] hfe) {
    var dir = Path.Combine(this._tmp, "out-" + Guid.NewGuid().ToString("N"));
    new HfeFormatDescriptor().Extract(new MemoryStream(hfe), dir, null, null);
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), File.ReadAllBytes);
  }

  private static byte[] TrackBlock(int length, int seed) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  [Test]
  public void GivenTracksAndMetadata_WhenCreated_ThenTheHeaderAndTracksAreAsSpecified() {
    using var ms = new MemoryStream();
    var ini = "version=1\nsides=2\ntrack_encoding=ISOIBM_MFM\nbit_rate_kbps=250\nfloppy_rpm=300\ninterface_mode=0x07\nsingle_step=0x00\n"u8.ToArray();
    new HfeFormatDescriptor().Create(ms, [
      this.Input("metadata.ini", ini),
      this.Input("tracks/track000.bin", TrackBlock(1000, 1)),
      this.Input("tracks/track001.bin", TrackBlock(512, 2)),
    ], new FormatCreateOptions());
    var hfe = ms.ToArray();
    Assert.That(hfe.AsSpan(0, 8).SequenceEqual("HXCPICFE"u8), Is.True);
    Assert.That(hfe[9], Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(hfe.AsSpan(12)), Is.EqualTo(250));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(hfe.AsSpan(14)), Is.EqualTo(300));
    Assert.That(hfe[21], Is.EqualTo(0x00));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(hfe.AsSpan(18)), Is.EqualTo(1), "track list at block 1");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(hfe.AsSpan(512)), Is.EqualTo(2), "track 0 at block 2");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(hfe.AsSpan(516)), Is.EqualTo(4), "track 1 after track 0's two blocks");
    Assert.That(hfe.Length, Is.EqualTo(5 * 512));
    var files = this.Extract(hfe);
    Assert.That(files["tracks/track000.bin"], Is.EqualTo(TrackBlock(1000, 1)));
    Assert.That(files["tracks/track001.bin"], Is.EqualTo(TrackBlock(512, 2)));
  }

  [Test]
  public void GivenAnImage_WhenExtractedAndRecreated_ThenItIsByteIdentical() {
    using var first = new MemoryStream();
    new HfeFormatDescriptor().Create(first, [
      this.Input("metadata.ini", "track_encoding=AMIGA_MFM\nwrite_allowed=0x00\n"u8.ToArray()),
      this.Input("tracks/track000.bin", TrackBlock(800, 3)),
    ], new FormatCreateOptions());
    var parts = this.Extract(first.ToArray()).Where(kv => kv.Key != "FULL.hfe").Select(kv => this.Input(kv.Key, kv.Value)).ToList();
    using var second = new MemoryStream();
    new HfeFormatDescriptor().Create(second, parts, new FormatCreateOptions());
    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
  }

  [Test]
  public void GivenAnImage_WhenATrackIsReplaced_ThenOnlyThatTrackChanges() {
    using var ms = new MemoryStream();
    var d = new HfeFormatDescriptor();
    d.Create(ms, [this.Input("tracks/track000.bin", TrackBlock(600, 4)), this.Input("tracks/track001.bin", TrackBlock(600, 5))], new FormatCreateOptions());
    d.Add(ms, [this.Input("tracks/track001.bin", TrackBlock(1500, 6))]);
    var files = this.Extract(ms.ToArray());
    Assert.That(files["tracks/track000.bin"], Is.EqualTo(TrackBlock(600, 4)));
    Assert.That(files["tracks/track001.bin"], Is.EqualTo(TrackBlock(1500, 6)));
  }

  [Test]
  public void GivenAGapInTheTracks_WhenCreated_ThenRefused()
    => Assert.Throws<ArgumentException>(() => new HfeFormatDescriptor().Create(new MemoryStream(),
      [this.Input("tracks/track000.bin", TrackBlock(10, 7)), this.Input("tracks/track002.bin", TrackBlock(10, 8))], new FormatCreateOptions()));

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined()
    => Assert.That(new HfeFormatDescriptor().CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);

  [Test]
  public void GivenTheOnlyTrack_WhenRemoved_ThenRefused() {
    using var ms = new MemoryStream();
    var d = new HfeFormatDescriptor();
    d.Create(ms, [this.Input("tracks/track000.bin", TrackBlock(10, 9))], new FormatCreateOptions());
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["tracks/track000.bin"]));
  }
}

/// <summary>
/// Greaseweazle (public domain) as the oracle: it writes an HFE from a sector image, we rebuild a
/// fresh HFE from the parts we list (tracks + metadata.ini), and Greaseweazle decodes our rebuild
/// back to the identical sectors; replacing one track from another disk shows up as exactly that
/// disk's sectors. The gw executable comes from <c>CWB_GW</c>; skipped when unset unless
/// <c>CWB_REQUIRE_GW=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class HfeExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-hfex-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

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
    var stderr = p.StandardError.ReadToEndAsync();
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    TestContext.Out.WriteLine($"gw {string.Join(' ', args)} -> {p.ExitCode}\n{stdout}{stderr.Result}");
    return p.ExitCode;
  }

  private byte[] Disk(int seed) {
    var d = new byte[737280];
    new Random(seed).NextBytes(d);
    return d;
  }

  private string GwHfe(string gw, byte[] img, string name) {
    var imgPath = Path.Combine(this._tmp, name + ".img");
    File.WriteAllBytes(imgPath, img);
    var hfe = Path.Combine(this._tmp, name + ".hfe");
    Assert.That(Gw(gw, "convert", "--format", "ibm.720", imgPath, hfe), Is.EqualTo(0));
    return hfe;
  }

  private List<ArchiveInputInfo> Parts(string hfe) {
    var dir = Path.Combine(this._tmp, "parts-" + Guid.NewGuid().ToString("N")[..8]);
    using (var fs = File.OpenRead(hfe))
      new HfeFormatDescriptor().Extract(fs, dir, null, null);
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
      .Where(p => !p.EndsWith("FULL.hfe", StringComparison.Ordinal))
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(dir, p).Replace('\\', '/'), false)).ToList();
  }

  private byte[] GwImg(string gw, string hfe) {
    var img = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(hfe) + "-back.img");
    Assert.That(Gw(gw, "convert", "--format", "ibm.720", hfe, img), Is.EqualTo(0));
    return File.ReadAllBytes(img);
  }

  [Test]
  public void GivenAGreaseweazleHfe_WhenWeRebuildItFromItsParts_ThenGreaseweazleDecodesTheSameSectors() {
    var gw = RequireGw();
    var disk = this.Disk(11);
    var ours = Path.Combine(this._tmp, "ours.hfe");
    using (var fs = File.Create(ours))
      new HfeFormatDescriptor().Create(fs, this.Parts(this.GwHfe(gw, disk, "theirs")), new FormatCreateOptions());
    Assert.That(this.GwImg(gw, ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneTrackReplacedFromAnotherDisk_WhenGreaseweazleDecodes_ThenExactlyThatCylinderChanged() {
    var gw = RequireGw();
    var a = this.Disk(12);
    var b = this.Disk(13);
    var hfeA = this.GwHfe(gw, a, "a");
    var track = this.Parts(this.GwHfe(gw, b, "b")).Single(i => i.ArchiveName == "tracks/track007.bin");
    var edited = Path.Combine(this._tmp, "edited.hfe");
    File.Copy(hfeA, edited);
    using (var fs = File.Open(edited, FileMode.Open, FileAccess.ReadWrite))
      new HfeFormatDescriptor().Add(fs, [track]);
    // An HFE track holds both sides of one cylinder: cylinder 7 = 18 sectors from 7 × 18 × 512.
    var expected = (byte[])a.Clone();
    Array.Copy(b, 7 * 18 * 512, expected, 7 * 18 * 512, 18 * 512);
    Assert.That(this.GwImg(gw, edited), Is.EqualTo(expected));
  }
}
