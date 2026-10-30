using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Moof;

namespace Compression.Tests.Moof;

[TestFixture]
public class MoofTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-moof-" + Guid.NewGuid().ToString("N"));
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

  internal static Dictionary<string, byte[]> Extract(string dir, byte[] moof) {
    var outDir = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N"));
    new MoofFormatDescriptor().Extract(new MemoryStream(moof), outDir, null, null);
    return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(outDir, p).Replace('\\', '/'), File.ReadAllBytes);
  }

  private static byte[] Bits(int length, int seed) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  [Test]
  public void GivenTracks_WhenCreated_ThenTheLayoutIsTheReferenceOne() {
    using var ms = new MemoryStream();
    new MoofFormatDescriptor().Create(ms, [
      Input(this._tmp, "tracks/track000.bin", Bits(9000, 1)),
      Input(this._tmp, "tracks/track001.bin", Bits(9000, 2)),
      Input(this._tmp, "metadata.ini", "track_001_bits=71000\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var moof = ms.ToArray();
    Assert.That(moof.AsSpan(0, 4).SequenceEqual("MOOF"u8), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(moof.AsSpan(8)), Is.EqualTo(MoofImage.Crc32(moof.AsSpan(12))));
    Assert.That(moof.AsSpan(12, 4).SequenceEqual("INFO"u8) && moof.AsSpan(80, 4).SequenceEqual("TMAP"u8) && moof.AsSpan(248, 4).SequenceEqual("TRKS"u8), Is.True);
    Assert.That(moof[88 + 1], Is.EqualTo(1), "track 0 side 1 maps to TRK 1");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(moof.AsSpan(256)), Is.EqualTo(3));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(moof.AsSpan(256 + 12)), Is.EqualTo(71000u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(moof.AsSpan(20 + 38)), Is.EqualTo(18), "largest track = 18 blocks");
    var files = Extract(this._tmp, moof);
    Assert.That(files["tracks/track001.bin"], Is.EqualTo(Bits(9000, 2).AsSpan(0, 8875).ToArray()));
  }

  [Test]
  public void GivenAMoof_WhenItsPartsAreRecreated_ThenTheImageIsByteIdentical() {
    using var first = new MemoryStream();
    new MoofFormatDescriptor().Create(first, [
      Input(this._tmp, "tracks/track000.bin", Bits(5000, 3)),
      Input(this._tmp, "meta.ini", "[Meta]\ntitle=System Disk\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var parts = Extract(this._tmp, first.ToArray()).Select(kv => Input(this._tmp, kv.Key, kv.Value)).ToList();
    using var second = new MemoryStream();
    new MoofFormatDescriptor().Create(second, parts, new FormatCreateOptions());
    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
  }

  [Test]
  public void GivenAFluxMap_WhenWritten_ThenItHasItsOwnBlockAndInfoPointsAtIt() {
    var flux = MoofImage.EmptyMap();
    flux[0] = 0;
    using var ms = new MemoryStream();
    new MoofFormatDescriptor().Create(ms, [
      Input(this._tmp, "flux.bin", flux),
      Input(this._tmp, "tracks/track000.bin", Bits(1000, 4)),
    ], new FormatCreateOptions());
    var moof = ms.ToArray();
    var fluxBlock = BinaryPrimitives.ReadUInt16LittleEndian(moof.AsSpan(20 + 40));
    Assert.That(fluxBlock, Is.EqualTo(5), "block 3 + 2 blocks of flux bytes");
    Assert.That(moof.AsSpan(fluxBlock * 512, 4).SequenceEqual("FLUX"u8), Is.True);
    var image = MoofImage.Read(moof);
    Assert.That(image.Tracks[0].BitCount, Is.EqualTo(1000u), "a flux track's count is in bytes");
    Assert.That(image.Tracks[0].Data, Is.EqualTo(Bits(1000, 4)));
  }

  [Test]
  public void GivenAnArbitraryFile_WhenOffered_ThenItIsDeclined()
    => Assert.That(new MoofFormatDescriptor().CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);

  [Test]
  public void GivenATruncatedTrack_WhenRead_ThenInvalidDataAndListFallsBack() {
    using var ms = new MemoryStream();
    new MoofFormatDescriptor().Create(ms, [Input(this._tmp, "tracks/track000.bin", Bits(5000, 5))], new FormatCreateOptions());
    var moof = ms.ToArray()[..^600];
    Assert.Throws<InvalidDataException>(() => MoofImage.Read(moof));
    Assert.That(new MoofFormatDescriptor().List(new MemoryStream(moof), null).Single().Name, Is.EqualTo("FULL.moof"));
  }

  [Test]
  public void GivenNoSignature_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => MoofImage.Read(new byte[64]));
}

/// <summary>
/// MAME floptool as the oracle: it turns an 800K Apple GCR sector image into MOOF, we rebuild a
/// fresh MOOF from the parts we list, and floptool decodes our rebuild to the identical sectors;
/// replacing one track from another disk shows up as exactly that track's sectors. Skipped when
/// floptool is absent unless <c>CWB_REQUIRE_FLOPTOOL=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class MoofExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-moofx-" + Guid.NewGuid().ToString("N"));
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
    var d = new byte[819200];
    new Random(seed).NextBytes(d);
    return d;
  }

  private string ToMoof(byte[] img, string name) {
    var path = Path.Combine(this._tmp, name + ".img");
    File.WriteAllBytes(path, img);
    var moof = Path.Combine(this._tmp, name + ".moof");
    Assert.That(Floptool($"flopconvert apple_gcr moof {FsInteropToolbox.WinToWsl(path)} {FsInteropToolbox.WinToWsl(moof)}"), Is.EqualTo(0));
    return moof;
  }

  private byte[] ToImg(string moof) {
    var img = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(moof) + "-back.img");
    Assert.That(Floptool($"flopconvert moof apple_gcr {FsInteropToolbox.WinToWsl(moof)} {FsInteropToolbox.WinToWsl(img)}"), Is.EqualTo(0));
    return File.ReadAllBytes(img);
  }

  [Test]
  public void GivenAFloptoolMoof_WhenWeRebuildItFromItsParts_ThenFloptoolDecodesTheSameSectors() {
    RequireFloptool();
    var disk = this.Disk(31);
    var parts = MoofTests.Extract(this._tmp, File.ReadAllBytes(this.ToMoof(disk, "theirs")))
      .Select(kv => MoofTests.Input(this._tmp, kv.Key, kv.Value)).ToList();
    var ours = Path.Combine(this._tmp, "ours.moof");
    using (var fs = File.Create(ours))
      new MoofFormatDescriptor().Create(fs, parts, new FormatCreateOptions());
    Assert.That(this.ToImg(ours), Is.EqualTo(disk));
  }

  [Test]
  public void GivenOneTrackReplacedFromAnotherDisk_WhenFloptoolDecodes_ThenExactlyThatTrackChanged() {
    RequireFloptool();
    var a = this.Disk(32);
    var b = this.Disk(33);
    var moofA = this.ToMoof(a, "a");
    var partsB = MoofTests.Extract(this._tmp, File.ReadAllBytes(this.ToMoof(b, "b")));
    // TRK index 0 = cylinder 0, side 0: the first 12 sectors (zone 0) of the 800K image.
    var bitLine = System.Text.Encoding.UTF8.GetString(partsB["metadata.ini"]).Split('\n').Single(l => l.StartsWith("track_000_bits=", StringComparison.Ordinal));
    var edited = Path.Combine(this._tmp, "edited.moof");
    File.Copy(moofA, edited);
    using (var fs = File.Open(edited, FileMode.Open, FileAccess.ReadWrite))
      new MoofFormatDescriptor().Add(fs, [
        MoofTests.Input(this._tmp, "tracks/track000.bin", partsB["tracks/track000.bin"]),
        MoofTests.Input(this._tmp, "metadata.ini", System.Text.Encoding.UTF8.GetBytes(bitLine + "\n")),
      ]);
    var expected = (byte[])a.Clone();
    Array.Copy(b, 0, expected, 0, 12 * 512);
    Assert.That(this.ToImg(edited), Is.EqualTo(expected));
  }
}
