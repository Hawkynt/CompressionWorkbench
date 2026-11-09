using System.Text;
using Compression.Registry;
using FileFormat.CloneCd;

namespace Compression.Tests.CloneCd;

[TestFixture]
public class CloneCdWriteTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-ccdw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Iso(int sectors, int seed) {
    var iso = new byte[sectors * 2048];
    new Random(seed).NextBytes(iso);
    return iso;
  }

  internal static string Create(string dir, byte[] iso, string name = "disc") {
    var input = Path.Combine(dir, name + "-in.iso");
    File.WriteAllBytes(input, iso);
    var ccd = Path.Combine(dir, name + ".ccd");
    using (var fs = File.Create(ccd))
      new CloneCdFormatDescriptor().Create(fs, [new ArchiveInputInfo(input, "image.iso", false)], new FormatCreateOptions());
    return ccd;
  }

  [Test]
  public void GivenAnIso_WhenCreated_ThenTheThreeFilesSitTogether() {
    var ccd = Create(this._tmp, Iso(200, 1));
    Assert.That(File.ReadAllBytes(Path.ChangeExtension(ccd, ".img")), Has.Length.EqualTo(200 * 2352));
    Assert.That(File.ReadAllBytes(Path.ChangeExtension(ccd, ".sub")), Has.Length.EqualTo(200 * 96));
    using var fs = File.OpenRead(ccd);
    Assert.That(new CloneCdFormatDescriptor().List(fs, null).Select(e => e.Name), Is.SupersetOf(new[] { "FULL.ccd", "metadata.ini", "disc.img", "disc.sub" }));
  }

  [Test]
  public void GivenAnIso_WhenCreated_ThenTheControlFileHoldsTheTocAndTrack() {
    var text = File.ReadAllText(Create(this._tmp, Iso(277, 2)));
    Assert.That(text, Does.StartWith("[CloneCD]\r\nVersion=3\r\n"));
    Assert.That(text, Does.Contain("Point=0xa2\r\n").And.Contain("PMin=0\r\nPSec=5\r\nPFrame=52\r\nPLBA=277\r\n"), "lead-out after 277 sectors at 00:05:52");
    Assert.That(text, Does.Contain("Point=0x01\r\n").And.Contain("PMin=0\r\nPSec=2\r\nPFrame=0\r\nPLBA=0\r\n"));
    Assert.That(text, Does.EndWith("[TRACK 1]\r\nMODE=1\r\nINDEX 1=0\r\n"));
  }

  [Test]
  public void GivenAnIso_WhenCreated_ThenEachSectorCarriesItsUserDataAndAddress() {
    var iso = Iso(160, 3);
    var img = File.ReadAllBytes(Path.ChangeExtension(Create(this._tmp, iso), ".img"));
    for (var lba = 0; lba < 160; lba += 53) {
      var raw = img.AsSpan(lba * 2352, 2352);
      Assert.That(raw[15], Is.EqualTo(1));
      Assert.That(raw.Slice(16, 2048).SequenceEqual(iso.AsSpan(lba * 2048, 2048)), Is.True);
    }
    // LBA 0 is 00:02:00, LBA 159 is 00:04:09 (BCD).
    Assert.That(img.AsSpan(12, 3).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x02, 0x00 }));
    Assert.That(img.AsSpan(159 * 2352 + 12, 3).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x04, 0x09 }));
  }

  [TestCase("41010100020000000200", 0xA372)]
  [TestCase("41010000017400000201", 0xAEE0)]
  [TestCase("41010100035100000551", 0x8AF2)]
  [TestCase("41010100000000000200", 0x2832)]
  public void GivenAQFrame_WhenItsCrcIsComputed_ThenItMatchesTheMeasuredDiscs(string q, int stored)
    => Assert.That((ushort)~CloneCdWriter.Crc16(Convert.FromHexString(q)), Is.EqualTo(stored));

  [Test]
  public void GivenADisc_WhenItsSubchannelIsBuilt_ThenPAndQFollowThePauseAndPositionRules() {
    var sub = CloneCdWriter.Subchannel(300);
    bool P(int lba) => sub.AsSpan(lba * 96, 12).IndexOfAnyExcept((byte)0xFF) < 0;
    Assert.That(P(0) && !P(1) && !P(150) && !P(300 - 150) && P(300 - 149) && P(299), Is.True);
    Assert.That(sub.AsSpan(12 + 96 * 75, 12).ToArray()[..10], Is.EqualTo(Convert.FromHexString("41010100010000000300")), "LBA 75: 00:01:00 in the track, 00:03:00 on the disc");
    Assert.That(sub.AsSpan(24, 72).ContainsAnyExcept((byte)0), Is.False, "R-W stay empty");
  }

  [TestCase(0)]
  [TestCase(2047)]
  public void GivenAnIsoThatIsNotWholeSectors_WhenCreated_ThenRejected(int length) {
    var input = Path.Combine(this._tmp, "bad.iso");
    File.WriteAllBytes(input, new byte[length]);
    using var fs = File.Create(Path.Combine(this._tmp, "bad.ccd"));
    Assert.Throws<InvalidDataException>(() => new CloneCdFormatDescriptor().Create(fs, [new ArchiveInputInfo(input, "image.iso", false)], new FormatCreateOptions()));
  }

  [Test]
  public void GivenAStreamThatIsNoFile_WhenCreated_ThenRefusedBecauseTheCompanionsNeedAPlace() {
    var input = Path.Combine(this._tmp, "x.iso");
    File.WriteAllBytes(input, new byte[2048]);
    Assert.Throws<NotSupportedException>(() => new CloneCdFormatDescriptor().Create(new MemoryStream(), [new ArchiveInputInfo(input, "image.iso", false)], new FormatCreateOptions()));
  }

  [Test]
  public void GivenAnythingButOneIso_WhenOffered_ThenDeclined() {
    var d = new CloneCdFormatDescriptor();
    Assert.That(d.CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);
    Assert.That(d.CanAccept(new ArchiveInputInfo("x", "image.iso", false), out _), Is.True);
  }
}

/// <summary>
/// Third-party proof for the CloneCD writer. PowerISO for Linux (<c>CWB_POWERISO</c>) turns the same ISO
/// into raw MODE1/2352 sectors, and our <c>.img</c> must equal them byte for byte. Aaru 5.4.2
/// (<c>CWB_AARU</c>) reads our image set back: it finds one session with one Mode 1 track and a raw
/// subchannel, every sector checksum verifies, and it extracts the ISO 9660 files through our image
/// set unchanged. ccd2iso turns our <c>.img</c> into the input ISO. A real CloneCD dump of a single data track
/// (<c>CWB_CLONECD_SUB</c>, the <c>.sub</c> of "PSX - Fighting Force 3-24" on archive.org) has the
/// subchannel our writer produces in every sector the drive read cleanly. Skipped when a tool is
/// missing, unless <c>CWB_REQUIRE_CLONECD=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class CloneCdWriteExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-ccdx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static string Need(string variable) {
    var value = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrEmpty(value) && FsInteropToolbox.WslAvailable) return value;
    var hint = $"{variable} is not set.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_CLONECD") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static (int Code, string Out) Wsl(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  /// <summary>An ISO 9660 image made by genisoimage, so the data is a real file system.</summary>
  private byte[] MakeIso() {
    var src = Path.Combine(this._tmp, "src");
    Directory.CreateDirectory(src);
    File.WriteAllText(Path.Combine(src, "numbers.txt"), string.Join('\n', Enumerable.Range(1, 20000)));
    var random = new byte[300_000];
    new Random(9).NextBytes(random);
    File.WriteAllBytes(Path.Combine(src, "random.bin"), random);
    var iso = Path.Combine(this._tmp, "in.iso");
    Assert.That(Wsl($"genisoimage -quiet -V CWBCCD -o {FsInteropToolbox.WinToWsl(iso)} {FsInteropToolbox.WinToWsl(src)}").Code, Is.EqualTo(0));
    return File.ReadAllBytes(iso);
  }

  [Test]
  public void GivenAnIso_WhenPowerIsoMakesRawSectors_ThenOurImgIsTheSame() {
    var poweriso = Need("CWB_POWERISO");
    var iso = this.MakeIso();
    var ccd = CloneCdWriteTests.Create(this._tmp, iso, "ours");
    var bin = Path.Combine(this._tmp, "p.bin");
    Wsl($"cd {FsInteropToolbox.WinToWsl(this._tmp)} && '{poweriso}' convert in.iso -o p.bin -ot bin");
    Assert.That(File.ReadAllBytes(Path.ChangeExtension(ccd, ".img")), Is.EqualTo(File.ReadAllBytes(bin)));
  }

  [Test]
  public void GivenOurImageSet_WhenAaruAndCcd2isoReadIt_ThenTheyGetTheDataBack() {
    var aaru = Need("CWB_AARU");
    var iso = this.MakeIso();
    var ccd = CloneCdWriteTests.Create(this._tmp, iso, "ours");
    // Aaru finds the .img and .sub relative to the working directory, so it runs inside the image's folder.
    var at = $"cd {FsInteropToolbox.WinToWsl(this._tmp)} && '{aaru}'";

    var info = Wsl($"{at} image info ours.ccd").Out;
    Assert.That(info, Does.Contain("CloneCD version 3").And.Contain("CdMode1").And.Contain("First track number: 1"));
    Assert.That(Wsl($"{at} image verify ours.ccd").Out, Does.Contain("All sector checksums are correct"));

    // Aaru mounts the ISO 9660 file system through our image set and extracts the files we put in.
    Wsl($"{at} filesystem extract ours.ccd aaru-files");
    var extracted = Path.Combine(this._tmp, "aaru-files", "ISO9660", "CWBCCD");
    Assert.That(File.ReadAllBytes(Path.Combine(extracted, "NUMBERS.TXT")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._tmp, "src", "numbers.txt"))));
    Assert.That(File.ReadAllBytes(Path.Combine(extracted, "RANDOM.BIN")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._tmp, "src", "random.bin"))));

    var back2 = Path.Combine(this._tmp, "ccd2iso.iso");
    Wsl($"ccd2iso {FsInteropToolbox.WinToWsl(Path.ChangeExtension(ccd, ".img"))} {FsInteropToolbox.WinToWsl(back2)}");
    Assert.That(File.ReadAllBytes(back2), Is.EqualTo(iso));
  }

  [Test]
  public void GivenARealDataTrackDump_WhenWeBuildItsSubchannel_ThenEveryCleanSectorMatches() {
    var path = Need("CWB_CLONECD_SUB");
    var real = File.ReadAllBytes(path);
    var sectors = real.Length / 96;
    var ours = CloneCdWriter.Subchannel(sectors);
    var compared = 0;
    for (var lba = 0; lba < sectors; ++lba) {
      var r = real.AsSpan(lba * 96, 96);
      var q = r.Slice(12, 12);
      var crc = (ushort)~CloneCdWriter.Crc16(q[..10]);
      var p = r[..12];
      // Skip what the drive misread: a Q frame failing its CRC, or a P run that is neither all 0 nor all 1.
      if (q[10] != crc >> 8 || q[11] != (byte)crc || (p.ContainsAnyExcept((byte)0) && p.ContainsAnyExcept((byte)0xFF))) continue;
      ++compared;
      Assert.That(r[..24].ToArray(), Is.EqualTo(ours.AsSpan(lba * 96, 24).ToArray()), $"sector {lba}");
    }
    Assert.That(compared, Is.GreaterThan(sectors - 5));
  }
}
