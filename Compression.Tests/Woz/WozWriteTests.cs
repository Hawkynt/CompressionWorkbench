using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Woz;

namespace Compression.Tests.Woz;

[TestFixture]
public class WozWriteTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-wozw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  internal static ArchiveInputInfo Input(string dir, string name, byte[] data) {
    var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
    File.WriteAllBytes(path, data);
    return new ArchiveInputInfo(path, name, false);
  }

  internal static Dictionary<string, byte[]> Extract(string dir, byte[] woz) {
    var outDir = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N"));
    new WozFormatDescriptor().Extract(new MemoryStream(woz), outDir, null, null);
    return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(outDir, p).Replace('\\', '/'), File.ReadAllBytes);
  }

  private static byte[] Bits(int length, int seed) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  [Test]
  public void GivenTracks_WhenCreated_ThenTheWoz2LayoutAndCrcAreAsSpecified() {
    using var ms = new MemoryStream();
    new WozFormatDescriptor().Create(ms, [
      Input(this._tmp, "tracks/track000.bin", Bits(6400, 1)),
      Input(this._tmp, "tracks/track001.bin", Bits(700, 2)),
      Input(this._tmp, "metadata.ini", "track_001_bits=5555\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var woz = ms.ToArray();
    Assert.That(woz.AsSpan(0, 4).SequenceEqual("WOZ2"u8), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(woz.AsSpan(8)), Is.EqualTo(WozWriter.Crc32(woz.AsSpan(12))));
    Assert.That(woz.AsSpan(12, 4).SequenceEqual("INFO"u8) && woz.AsSpan(80, 4).SequenceEqual("TMAP"u8) && woz.AsSpan(248, 4).SequenceEqual("TRKS"u8), Is.True);
    Assert.That(woz[88 + 0], Is.EqualTo(0), "quarter-track 0 maps to TRK 0");
    Assert.That(woz[88 + 4], Is.EqualTo(1), "quarter-track 4 maps to TRK 1");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(woz.AsSpan(256)), Is.EqualTo(3), "track 0 data at block 3");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(woz.AsSpan(256 + 8 + 4)), Is.EqualTo(5555u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(woz.AsSpan(12 + 8 + 44)), Is.EqualTo(13), "largest track = 13 blocks");

    var files = Extract(this._tmp, woz);
    Assert.That(files["tracks/track000.bin"], Is.EqualTo(Bits(6400, 1)));
    Assert.That(files["tracks/track001.bin"], Is.EqualTo(Bits(700, 2).AsSpan(0, 695).ToArray()));
    Assert.That(System.Text.Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("track_001_bits=5555"));
  }

  [Test]
  public void GivenAWoz_WhenItsPartsAreRecreated_ThenTheImageIsByteIdentical() {
    using var first = new MemoryStream();
    new WozFormatDescriptor().Create(first, [
      Input(this._tmp, "tracks/track000.bin", Bits(6000, 3)),
      Input(this._tmp, "meta.ini", "[Meta]\ntitle=Test Disk\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var parts = Extract(this._tmp, first.ToArray()).Where(kv => kv.Key != "FULL.woz").Select(kv => Input(this._tmp, kv.Key, kv.Value)).ToList();
    using var second = new MemoryStream();
    new WozFormatDescriptor().Create(second, parts, new FormatCreateOptions());
    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
  }

  [Test]
  public void GivenAnImage_WhenATrackIsRemoved_ThenItsTmapSlotsAreCleared() {
    using var ms = new MemoryStream();
    var d = new WozFormatDescriptor();
    d.Create(ms, [Input(this._tmp, "tracks/track000.bin", Bits(100, 4)), Input(this._tmp, "tracks/track001.bin", Bits(100, 5))], new FormatCreateOptions());
    d.Remove(ms, ["tracks/track001.bin"]);
    var woz = ms.ToArray();
    Assert.That(woz[88 + 4], Is.EqualTo(0xFF));
    Assert.That(Extract(this._tmp, woz).ContainsKey("tracks/track001.bin"), Is.False);
  }

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined()
    => Assert.That(new WozFormatDescriptor().CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);

  [Test]
  public void GivenABitCountLargerThanTheTrack_WhenCreated_ThenRefused()
    => Assert.Throws<ArgumentException>(() => new WozFormatDescriptor().Create(new MemoryStream(), [
      Input(this._tmp, "tracks/track000.bin", Bits(10, 6)), Input(this._tmp, "metadata.ini", "track_000_bits=81\n"u8.ToArray())], new FormatCreateOptions()));
}

/// <summary>
/// MAME floptool as the oracle: it turns a DOS-order 140K image into a WOZ, we rebuild a fresh
/// WOZ 2 from the parts we list, and floptool decodes our rebuild to the identical sectors;
/// replacing one track from another disk shows up as exactly that track's sectors. Skipped when
/// floptool is absent unless <c>CWB_REQUIRE_FLOPTOOL=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class WozExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-wozx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireFloptool() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("floptool")) return;
    const string hint = "floptool not installed: `sudo apt-get install -y mame-tools`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_FLOPTOOL") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static int Floptool(string args) {
    var r = FsInteropToolbox.RunWsl("floptool " + args);
    TestContext.Out.WriteLine($"floptool {args} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r.ExitCode;
  }

  private byte[] Disk(int seed) {
    var d = new byte[143360];
    new Random(seed).NextBytes(d);
    return d;
  }

  private string ToWoz(byte[] dsk, string name) {
    var dskPath = Path.Combine(this._tmp, name + ".dsk");
    File.WriteAllBytes(dskPath, dsk);
    var woz = Path.Combine(this._tmp, name + ".woz");
    Assert.That(Floptool($"flopconvert a2_16sect_dos woz {FsInteropToolbox.WinToWsl(dskPath)} {FsInteropToolbox.WinToWsl(woz)}"), Is.EqualTo(0));
    return woz;
  }

  private byte[] ToDsk(string woz) {
    var dsk = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(woz) + "-back.dsk");
    Assert.That(Floptool($"flopconvert woz a2_16sect_dos {FsInteropToolbox.WinToWsl(woz)} {FsInteropToolbox.WinToWsl(dsk)}"), Is.EqualTo(0));
    return File.ReadAllBytes(dsk);
  }

  private List<ArchiveInputInfo> Parts(string woz)
    => WozWriteTests.Extract(this._tmp, File.ReadAllBytes(woz)).Where(kv => kv.Key != "FULL.woz")
      .Select(kv => WozWriteTests.Input(this._tmp, kv.Key, kv.Value)).ToList();

  [Test]
  public void GivenAFloptoolWoz_WhenWeRebuildItFromItsParts_ThenFloptoolDecodesTheSameSectors() {
    RequireFloptool();
    var disk = this.Disk(21);
    var ours = Path.Combine(this._tmp, "ours.woz");
    using (var fs = File.Create(ours))
      new WozFormatDescriptor().Create(fs, this.Parts(this.ToWoz(disk, "theirs")), new FormatCreateOptions());
    Assert.That(this.ToDsk(ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneTrackReplacedFromAnotherDisk_WhenFloptoolDecodes_ThenExactlyThatTrackChanged() {
    RequireFloptool();
    var a = this.Disk(22);
    var b = this.Disk(23);
    var wozA = this.ToWoz(a, "a");
    // TRK index 9 is whole track 9 (the TMAP floptool writes is the standard one); 16 × 256 bytes per track.
    var partsB = WozWriteTests.Extract(this._tmp, File.ReadAllBytes(this.ToWoz(b, "b")));
    var track = WozWriteTests.Input(this._tmp, "tracks/track009.bin", partsB["tracks/track009.bin"]);
    var bitLine = System.Text.Encoding.UTF8.GetString(partsB["metadata.ini"]).Split('\n').Single(l => l.StartsWith("track_009_bits=", StringComparison.Ordinal));
    var bits = WozWriteTests.Input(this._tmp, "metadata.ini", System.Text.Encoding.UTF8.GetBytes(bitLine + "\n"));
    var edited = Path.Combine(this._tmp, "edited.woz");
    File.Copy(wozA, edited);
    using (var fs = File.Open(edited, FileMode.Open, FileAccess.ReadWrite))
      new WozFormatDescriptor().Add(fs, [track, bits]);
    var expected = (byte[])a.Clone();
    Array.Copy(b, 9 * 4096, expected, 9 * 4096, 4096);
    Assert.That(this.ToDsk(edited), Is.EqualTo(expected));
  }
}
