using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.CdXa;

namespace Compression.Tests.CdXa;

[TestFixture]
public class CdXaTests {
  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "CdXa", "ReferenceVectors", name);

  /// <summary>An MPEG-1 pack: 12-byte pack header, then one PES packet of the given stream filling <paramref name="size"/> bytes.</summary>
  internal static byte[] Pack(byte stream, int size, byte fill) {
    var p = new byte[size];
    p[2] = 1; p[3] = 0xBA; p[4] = 0x21; p[6] = 1; p[8] = 1; p[9] = 0x80; p[11] = 1;
    p[14] = 1; p[15] = stream;
    BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(16), (ushort)(size - 18));
    p.AsSpan(18).Fill(fill);
    return p;
  }

  private static byte[] Mpeg(params byte[][] packs) => [.. packs.SelectMany(p => p)];

  private static readonly CdXaFile.Layout Small = new(StartFrame: 600, FileNumber: 1, LeadSectors: 2, TrailSectors: 3, CdXaFile.RecordEnds.LastSector);

  [Test]
  public void GivenTheRealVcdHead_WhenRead_ThenSectorsAddressesAndPacksAreThere() {
    var file = CdXaFile.Read(File.ReadAllBytes(VectorPath("AVSEQ09-head.dat")));
    Assert.That(file.SectorCount, Is.EqualTo(34));
    Assert.That(file.Fmt, Is.EqualTo(CdXaFile.DefaultFmt(9)), "the Form 2 attributes of a Video CD track");
    Assert.That(CdXaFile.Msf(CdXaFile.FrameOf(file.Data.AsSpan(12, 3))), Is.EqualTo("27:55:16"));
    var track = file.Track();
    Assert.That(track.Length, Is.EqualTo(4 * CdXaFile.UserDataSize), "the 30 empty lead sectors are skipped");
    Assert.That(CdXaFile.SplitPacks(track).Select(p => p.Kind), Is.EqualTo(new[] { CdXaFile.Video, CdXaFile.Audio, CdXaFile.Video, CdXaFile.Audio }));
  }

  [Test]
  public void GivenTheRealVcdHead_WhenItsSectorsAreRebuilt_ThenSubheadersAddressesAndEdcMatch() {
    // The head ends inside the track, so rebuild the cut part only: lead plus the first four packs.
    var file = CdXaFile.Read(File.ReadAllBytes(VectorPath("AVSEQ09-head.dat")));
    var layout = new CdXaFile.Layout(CdXaFile.FrameOf(file.Data.AsSpan(12, 3)), 9, 30, 1, CdXaFile.RecordEnds.LastOfEachStream);
    var rebuilt = CdXaFile.Build(file.Track(), layout);
    // Only the last two packs differ: here they are the last of their streams, so they end a record.
    Assert.That(rebuilt.AsSpan(0, 32 * CdXaFile.SectorSize).SequenceEqual(file.Data.AsSpan(0, 32 * CdXaFile.SectorSize)), Is.True);
    Assert.That(rebuilt[32 * CdXaFile.SectorSize + 18], Is.EqualTo(0x63));
  }

  [Test]
  public void GivenPacks_WhenBuilt_ThenLeadPacksTrailAndFlagsFollowTheVideoCdLayout() {
    var mpeg = Mpeg(Pack(0xE0, 2324, 1), Pack(0xC0, 2304, 2), Pack(0xE0, 100, 3));
    var data = CdXaFile.Build(mpeg, Small);
    Assert.That(data.Length, Is.EqualTo((2 + 3 + 3) * CdXaFile.SectorSize));
    byte Sub(int s, int i) => data[s * CdXaFile.SectorSize + 16 + i];
    Assert.That((Sub(0, 1), Sub(0, 2), Sub(0, 3)), Is.EqualTo(((byte)0, (byte)0x60, (byte)0)));
    Assert.That((Sub(2, 1), Sub(2, 2), Sub(2, 3)), Is.EqualTo(((byte)1, (byte)0x62, (byte)0x0F)));
    Assert.That((Sub(3, 2), Sub(3, 3)), Is.EqualTo(((byte)0x64, (byte)0x7F)));
    Assert.That(Sub(4, 2), Is.EqualTo(0x63), "the last data sector ends the record");
    Assert.That(Sub(7, 2), Is.EqualTo(0xE0), "the last sector ends the file");
    Assert.That(data.AsSpan(4 * CdXaFile.SectorSize + 24 + 100, 2224).ContainsAnyExcept((byte)0), Is.False, "a short pack is zero-padded");
    Assert.That(data.AsSpan(12, 4).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x08, 0x00, 0x02 }), "00:08:00, mode 2");
    Assert.That(data.AsSpan(7 * CdXaFile.SectorSize + 12, 3).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x08, 0x07 }));
  }

  [Test]
  public void GivenEachStreamEndsARecord_WhenBuilt_ThenTheLastVideoAndLastAudioCarryIt() {
    var mpeg = Mpeg(Pack(0xE0, 200, 1), Pack(0xC0, 200, 2), Pack(0xE0, 200, 3), Pack(0xC0, 200, 4));
    var data = CdXaFile.Build(mpeg, Small with { RecordEnds = CdXaFile.RecordEnds.LastOfEachStream });
    Assert.That(Enumerable.Range(2, 4).Select(s => data[s * CdXaFile.SectorSize + 18]), Is.EqualTo(new byte[] { 0x62, 0x64, 0x63, 0x65 }));
  }

  [Test]
  public void GivenAWrittenFile_WhenReadBack_ThenTheLayoutIsDetectedAndTheTrackIsTheZeroPaddedPacks() {
    var mpeg = Mpeg(Pack(0xE0, 2324, 1), Pack(0xC0, 2304, 2));
    var file = new CdXaFile { Fmt = CdXaFile.DefaultFmt(1), Data = CdXaFile.Build(mpeg, Small) };
    using var ms = new MemoryStream();
    file.Write(ms);
    var back = CdXaFile.Read(ms.ToArray());
    Assert.That(back.DetectLayout(), Is.EqualTo(Small));
    Assert.That(back.Track(), Is.EqualTo((byte[])[.. Pack(0xE0, 2324, 1), .. Pack(0xC0, 2304, 2), .. new byte[20]]));
  }

  [Test]
  public void GivenTheDescriptor_WhenTheTrackIsReplaced_ThenTheLayoutIsKept() {
    var ms = new MemoryStream();
    new CdXaFile { Fmt = CdXaFile.DefaultFmt(4), Data = CdXaFile.Build(Mpeg(Pack(0xE0, 300, 1)), Small with { FileNumber = 4 }) }.Write(ms);
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, Mpeg(Pack(0xE0, 400, 7), Pack(0xC0, 400, 8)));
      new CdXaFormatDescriptor().Add(ms, [new ArchiveInputInfo(path, "track.mpg", false)]);
    } finally {
      File.Delete(path);
    }
    var back = CdXaFile.Read(ms.ToArray());
    Assert.That(back.DetectLayout(), Is.EqualTo(Small with { FileNumber = 4 }));
    Assert.That(CdXaFile.SplitPacks(back.Track()).Count, Is.EqualTo(2));
    Assert.Throws<NotSupportedException>(() => new CdXaFormatDescriptor().Remove(ms, ["track.mpg"]));
  }

  [Test]
  public void GivenSectorsOutsideTheVideoCdLayout_WhenEdited_ThenRefusedAndUntouched() {
    var data = CdXaFile.Build(Mpeg(Pack(0xE0, 300, 1)), Small);
    data[2 * CdXaFile.SectorSize + 17] = 5; // another channel: not a layout we can reproduce
    var ms = new MemoryStream();
    new CdXaFile { Fmt = CdXaFile.DefaultFmt(1), Data = data }.Write(ms);
    var before = ms.ToArray();
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, Mpeg(Pack(0xE0, 300, 2)));
      Assert.Throws<NotSupportedException>(() => new CdXaFormatDescriptor().Add(ms, [new ArchiveInputInfo(path, "track.mpg", false)]));
    } finally {
      File.Delete(path);
    }
    Assert.That(ms.ToArray(), Is.EqualTo(before));
    var ini = Encoding.UTF8.GetString(new CdXaFormatDescriptor().List(new MemoryStream(before), null).Count == 2 ? Ini(before) : []);
    Assert.That(ini, Does.Contain("layout=other"));
  }

  private static byte[] Ini(byte[] dat) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-cdxa-" + Guid.NewGuid().ToString("N"));
    try {
      new CdXaFormatDescriptor().Extract(new MemoryStream(dat), dir, null, ["metadata.ini"]);
      return File.ReadAllBytes(Path.Combine(dir, "metadata.ini"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenAPackLargerThanASector_WhenSplit_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => CdXaFile.SplitPacks(Pack(0xE0, 2400, 1)));

  [Test]
  public void GivenAPackWithoutVideoOrAudio_WhenSplit_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => CdXaFile.SplitPacks(Pack(0xBE, 200, 0)));

  [Test]
  public void GivenAnArbitraryFile_WhenOfferedOrCreatedWithoutATrack_ThenDeclined() {
    var d = new CdXaFormatDescriptor();
    Assert.That(d.CanAccept(new ArchiveInputInfo("PROBE.BIN", "PROBE.BIN", false), out _), Is.False);
    Assert.Throws<ArgumentException>(() => d.Create(new MemoryStream(), [], new FormatCreateOptions()));
  }

  [Test]
  public void GivenAPartialSector_WhenRead_ThenInvalidDataAndListFallsBack() {
    var dat = File.ReadAllBytes(VectorPath("AVSEQ09-head.dat"));
    BinaryPrimitives.WriteUInt32LittleEndian(dat.AsSpan(40), 34 * 2352 - 1);
    Assert.Throws<InvalidDataException>(() => CdXaFile.Read(dat));
    Assert.That(new CdXaFormatDescriptor().List(new MemoryStream(dat), null).Single().Name, Is.EqualTo("FULL.dat"));
  }

  [Test]
  public void GivenABrokenSync_WhenRead_ThenInvalidData() {
    var dat = File.ReadAllBytes(VectorPath("AVSEQ09-head.dat"));
    dat[44 + 5 * 2352 + 3] = 0;
    Assert.Throws<InvalidDataException>(() => CdXaFile.Read(dat));
  }

  [TestCase("RIFF\0\0\0\0WAVE")]
  [TestCase("RIFX\0\0\0\0CDXA")]
  public void GivenAnotherRiff_WhenRead_ThenInvalidData(string head)
    => Assert.Throws<InvalidDataException>(() => CdXaFile.Read(Encoding.Latin1.GetBytes(head)));

  [TestCase("00:00:00", 0)]
  [TestCase("00:02:00", 150)]
  [TestCase("99:59:74", 449_999)]
  public void GivenAnAddress_WhenConverted_ThenFramesAndBcdRoundTrip(string msf, int frames) {
    Assert.That(CdXaFile.ParseMsf(msf), Is.EqualTo(frames));
    var bcd = new byte[3];
    CdXaFile.WriteMsf(bcd, frames);
    Assert.That(CdXaFile.FrameOf(bcd), Is.EqualTo(frames));
    Assert.That(CdXaFile.Msf(frames), Is.EqualTo(msf));
  }

  [TestCase("00:60:00")]
  [TestCase("00:00:75")]
  [TestCase("1:2")]
  public void GivenABadAddress_WhenParsed_ThenRejected(string msf)
    => Assert.Throws<ArgumentException>(() => CdXaFile.ParseMsf(msf));
}

/// <summary>
/// GNU VCDImager as the oracle, on a real Video CD track and on a disc it authors itself. For the real
/// <c>AVSEQ09.DAT</c> (see ReferenceVectors/README.md; the file comes from <c>CWB_VCD_DAT</c>), our
/// <c>track.mpg</c> equals what <c>cdxa2mpeg</c> extracts, and creating a file from the parts we list
/// reproduces the original byte for byte. For a clip that ffmpeg encodes and <c>vcdimager</c> burns
/// to a disc image, the file we create from the same MPEG equals the sectors of
/// <c>MPEGAV/AVSEQ01.DAT</c> on that disc, and <c>cdxa2mpeg</c> extracts our track from it. Skipped
/// when the tools are absent, unless <c>CWB_REQUIRE_VCD=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class CdXaExternalConformanceTests {
  private static void RequireTools() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("vcdimager") && FsInteropToolbox.WslHasTool("cdxa2mpeg") && FsInteropToolbox.WslHasTool("ffmpeg")) return;
    const string hint = "VCDImager or ffmpeg not installed: `sudo apt-get install -y vcdimager ffmpeg`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_VCD") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static string Wsl(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.ExitCode, Is.EqualTo(0), command);
    return r.StdOut;
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-cdxax-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private Dictionary<string, string> TakeApart(string dat) {
    var dir = Path.Combine(this._tmp, "parts-" + Guid.NewGuid().ToString("N")[..8]);
    using (var fs = File.OpenRead(dat))
      new CdXaFormatDescriptor().Extract(fs, dir, null, null);
    return Directory.EnumerateFiles(dir).ToDictionary(p => Path.GetFileName(p), p => p);
  }

  private static byte[] Create(params (string Path, string Name)[] parts) {
    using var ms = new MemoryStream();
    new CdXaFormatDescriptor().Create(ms, parts.Select(p => new ArchiveInputInfo(p.Path, p.Name, false)).ToList(), new FormatCreateOptions());
    return ms.ToArray();
  }

  private byte[] Cdxa2Mpeg(string dat) {
    var mpg = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(dat) + "-cdxa2mpeg.mpg");
    Wsl($"cdxa2mpeg {FsInteropToolbox.WinToWsl(dat)} {FsInteropToolbox.WinToWsl(mpg)}");
    return File.ReadAllBytes(mpg);
  }

  [Test]
  public void GivenARealVideoCdTrack_WhenTakenApartAndRecreated_ThenCdxa2mpegAgreesAndTheFileIsReproduced() {
    RequireTools();
    var dat = Environment.GetEnvironmentVariable("CWB_VCD_DAT");
    if (string.IsNullOrEmpty(dat) || !File.Exists(dat)) {
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_VCD") == "1") Assert.Fail("CWB_VCD_DAT does not name AVSEQ09.DAT.");
      Assert.Ignore("CWB_VCD_DAT not set.");
    }
    var parts = this.TakeApart(dat);
    Assert.That(File.ReadAllBytes(parts["track.mpg"]), Is.EqualTo(this.Cdxa2Mpeg(dat)));
    var ini = File.ReadAllText(parts["metadata.ini"]);
    Assert.That(ini, Does.Contain("layout=vcd").And.Contain("lead_sectors=30").And.Contain("trail_sectors=45").And.Contain("record_ends=last_of_each_stream"));
    Assert.That(Create((parts["track.mpg"], "track.mpg"), (parts["metadata.ini"], "metadata.ini")), Is.EqualTo(File.ReadAllBytes(dat)));
  }

  [Test]
  public void GivenAClipVcdimagerBurns_WhenWeCreateItsTrack_ThenOurSectorsAreTheDiscs() {
    RequireTools();
    var clip = Path.Combine(this._tmp, "clip.mpg");
    var cue = Path.Combine(this._tmp, "videocd.cue");
    var bin = Path.Combine(this._tmp, "videocd.bin");
    Wsl($"ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=352x288:rate=25 -f lavfi -i sine=frequency=440:sample_rate=44100 -t 3 -target pal-vcd {FsInteropToolbox.WinToWsl(clip)}");
    Wsl($"cd {FsInteropToolbox.WinToWsl(this._tmp)} && vcdimager -t vcd2 --cue-file=videocd.cue --bin-file=videocd.bin clip.mpg");
    var info = Wsl($"vcd-info --no-banner -i {FsInteropToolbox.WinToWsl(cue)}");
    var m = System.Text.RegularExpressions.Regex.Match(info, @"\[LSN\s+(\d+)\]\s+\d+\s+\(\s*(\d+)\)\s+AVSEQ01\.DAT");
    Assert.That(m.Success, Is.True, "vcd-info lists MPEGAV/AVSEQ01.DAT");
    var lsn = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var sectors = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) / 2048;
    var disc = File.ReadAllBytes(bin).AsSpan(lsn * CdXaFile.SectorSize, sectors * CdXaFile.SectorSize).ToArray();

    var ini = Path.Combine(this._tmp, "metadata.ini");
    File.WriteAllText(ini, $"first_sector_msf={CdXaFile.Msf(lsn + 150)}\n");
    var ours = Create((clip, "track.mpg"), (ini, "metadata.ini"));
    var file = CdXaFile.Read(ours);
    Assert.That(file.Data, Is.EqualTo(disc), "our sectors equal AVSEQ01.DAT on vcdimager's disc");

    var dat = Path.Combine(this._tmp, "AVSEQ01.DAT");
    File.WriteAllBytes(dat, ours);
    Assert.That(this.Cdxa2Mpeg(dat), Is.EqualTo(file.Track()));
  }
}
