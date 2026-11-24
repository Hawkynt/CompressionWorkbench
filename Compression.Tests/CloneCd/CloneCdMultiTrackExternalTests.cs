using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileFormat.CloneCd;

namespace Compression.Tests.CloneCd;

/// <summary>
/// Third-party proof for multi-session, audio and Mode 2 CloneCD sets. Aaru 5.4.2 (<c>CWB_AARU</c>, the path of
/// <c>aaru</c> in WSL) reads our sets — sessions, track types and every sector — and writes CloneCD sets from cue
/// sheets that we must read; bchunk splits our raw image into the ISO and the WAV we put in; and the Mode 2 sectors
/// vcdimager writes for a Video CD (Form 1 and Form 2) are rebuilt byte for byte by our sector writer. Skipped when a
/// tool is missing, unless <c>CWB_REQUIRE_CLONECD=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public partial class CloneCdMultiTrackExternalTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = Directory.CreateTempSubdirectory("cwb-ccdmx-").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void Skip(string hint) {
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_CLONECD") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static string Aaru() {
    var value = Environment.GetEnvironmentVariable("CWB_AARU");
    if (string.IsNullOrEmpty(value) || !FsInteropToolbox.WslAvailable) Skip("CWB_AARU is not set.");
    return value!;
  }

  private static void Need(params string[] tools) {
    if (!FsInteropToolbox.WslAvailable) Skip("WSL is not available.");
    foreach (var tool in tools)
      if (!FsInteropToolbox.WslHasTool(tool)) Skip($"'{tool}' is not installed in WSL.");
  }

  private static (int Code, string Out) Wsl(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static string W(string path) => FsInteropToolbox.WinToWsl(path);

  private string CreateSet(string name, params (string Name, byte[] Data)[] tracks) {
    var ccd = Path.Combine(this._tmp, name + ".ccd");
    using var fs = File.Create(ccd);
    new CloneCdFormatDescriptor().Create(fs, [.. tracks.Select(t => ArchiveInputInfo.InMemory(t.Name, t.Data))], new FormatCreateOptions());
    return ccd;
  }

  private static Dictionary<string, byte[]> ReadBack(string ccd) {
    var d = new CloneCdFormatDescriptor();
    using var fs = File.OpenRead(ccd);
    return d.List(fs, null).Where(e => e.Name.Contains("track-", StringComparison.Ordinal))
      .ToDictionary(e => e.Name, e => { using var s = d.OpenEntry(fs, e.Name, null); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); });
  }

  [GeneratedRegex(@"^[0-9A-F]{6}\s+((?:[0-9A-F]{2}\s)+)", RegexOptions.Multiline)]
  private static partial Regex HexLine();

  /// <summary>The bytes of one long sector as <c>aaru image print -r</c> dumps them.</summary>
  private byte[] AaruSector(string aaru, string ccd, int lba) {
    var dump = Wsl($"cd {W(this._tmp)} && '{aaru}' image print -s {lba} -l 1 -r {Path.GetFileName(ccd)}").Out;
    return [.. HexLine().Matches(dump).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
      .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
  }

  [Test]
  public void GivenOurTwoSessionMixedSet_WhenAaruReadsIt_ThenItSeesTheSessionsTracksAndSectors() {
    var aaru = Aaru();
    var iso = CloneCdMultiTrackTests.Random(300 * 2048, 21);
    var pcm = CloneCdMultiTrackTests.Random(320 * 2352, 22);
    var mode2 = CloneCdMultiTrackTests.Mode2Sectors(310, 23);
    var ccd = this.CreateSet("ms", ("data.iso", iso), ("music.wav", CloneCdMultiTrackTests.Wave(pcm)), ("session-02/xa.mode2", mode2));
    var at = $"cd {W(this._tmp)} && '{aaru}'";

    var info = Wsl($"{at} image info ms.ccd").Out;
    Assert.That(info, Does.Contain("CloneCD version 3"));
    Assert.That(Regex.IsMatch(info, @"^2\s+3\s+3\s+", RegexOptions.Multiline), Is.True, "session 2 holds track 3");
    Assert.That(Regex.IsMatch(info, @"^1\s+CdMode1\s", RegexOptions.Multiline), Is.True, "track 1 is Mode 1");
    Assert.That(Regex.IsMatch(info, @"^2\s+Audio\s", RegexOptions.Multiline), Is.True, "track 2 is audio");
    Assert.That(Regex.IsMatch(info, @"^3\s+CdMode2", RegexOptions.Multiline), Is.True, "track 3 is Mode 2");
    Assert.That(Wsl($"{at} image verify ms.ccd").Out, Does.Contain("All sector checksums are correct"));

    // Aaru finds each track's first user sector where we wrote it, the second session's included.
    var img = File.ReadAllBytes(Path.ChangeExtension(ccd, ".img"));
    var layout = CloneCdLayout.TryParse(File.ReadAllText(ccd))!;
    foreach (var track in layout.Tracks)
      Assert.That(this.AaruSector(aaru, ccd, track.Index1), Is.EqualTo(img.AsSpan((int)track.ImgSector * 2352, 2352).ToArray()), $"track {track.Number}");
  }

  [Test]
  public void GivenOurMixedSet_WhenBchunkSplitsIt_ThenItGetsTheIsoAndTheAudioBack() {
    Need("bchunk");
    var iso = CloneCdMultiTrackTests.Random(300 * 2048, 24);
    var pcm = CloneCdMultiTrackTests.Random(300 * 2352, 25);
    var ccd = this.CreateSet("mix", ("data.iso", iso), ("music.wav", CloneCdMultiTrackTests.Wave(pcm)));
    // Track 2's pause runs from 450 to 600 (index 00 and 01 in MSF without the 2-second offset).
    File.WriteAllText(Path.Combine(this._tmp, "mix.cue"),
      "FILE \"mix.img\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 00 00:06:00\n    INDEX 01 00:08:00\n");
    Assert.That(Wsl($"cd {W(this._tmp)} && bchunk -w mix.img mix.cue out").Code, Is.Zero);

    var ours = ReadBack(ccd);
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmp, "out01.iso")), Is.EqualTo(ours["track-01.iso"]));
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmp, "out01.iso")).AsSpan(0, iso.Length).SequenceEqual(iso), Is.True);
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmp, "out02.wav"))[44..], Is.EqualTo(pcm));
  }

  /// <summary>A Video CD vcdimager made from an MPEG-1 clip ffmpeg encoded: Mode 2 Form 1 and Form 2 sectors.</summary>
  private string MakeVideoCd() {
    Need("ffmpeg", "vcdimager");
    var r = Wsl($"cd {W(this._tmp)} && ffmpeg -loglevel error -f lavfi -i testsrc=duration=3:size=352x288:rate=25 " +
      "-f lavfi -i sine=duration=3 -target pal-vcd clip.mpg && vcdimager -t vcd2 -c vcd.cue -b vcd.bin clip.mpg");
    Assert.That(r.Code, Is.Zero, r.Out);
    return Path.Combine(this._tmp, "vcd.bin");
  }

  [Test]
  public void GivenAVideoCdFromVcdimager_WhenWeRebuildEveryMode2Sector_ThenItIsByteIdentical() {
    var bin = File.ReadAllBytes(this.MakeVideoCd());
    var raw = new byte[2352];
    int form1 = 0, form2 = 0;
    for (var s = 0; s < bin.Length / 2352; ++s) {
      var sector = bin.AsSpan(s * 2352, 2352);
      if (sector[15] != 2) continue;
      var lba = Bcd(sector[12]) * 4500 + Bcd(sector[13]) * 75 + Bcd(sector[14]) - 150;
      CloneCdWriter.WriteSector(raw, CloneCdTrackKind.Mode2, lba, sector[16..]);
      Assert.That(raw, Is.EqualTo(sector.ToArray()), $"sector {s}");
      if ((sector[18] & 0x20) != 0) ++form2; else ++form1;
    }
    Assert.That(form1, Is.GreaterThan(100));
    Assert.That(form2, Is.GreaterThan(100));
  }

  private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

  [Test]
  public void GivenAVideoCdAaruConvertedToCloneCd_WhenWeReadIt_ThenTheMode2TracksAreVcdimagersSectors() {
    var aaru = Aaru();
    var bin = File.ReadAllBytes(this.MakeVideoCd());
    Assert.That(Wsl($"cd {W(this._tmp)} && '{aaru}' image convert vcd.cue aaru.ccd").Code, Is.Zero);

    var tracks = ReadBack(Path.Combine(this._tmp, "aaru.ccd"));
    using var img = File.OpenRead(Path.Combine(this._tmp, "aaru.img"));
    var layout = CloneCdLayout.TryParse(File.ReadAllText(Path.Combine(this._tmp, "aaru.ccd")), img)!;
    Assert.That(tracks.Keys, Is.EquivalentTo(layout.Tracks.Select(t => t.EntryName)));
    Assert.That(tracks.Keys, Has.All.EndsWith(".mode2"));
    foreach (var track in layout.Tracks) {
      var expected = new List<byte>();
      for (var s = track.Index1; s < track.End; ++s) expected.AddRange(bin.AsSpan(s * 2352 + 16, 2336).ToArray());
      Assert.That(tracks[track.EntryName], Is.EqualTo(expected.ToArray()), track.EntryName);
    }
  }

  [Test]
  public void GivenAMixedModeCueAaruConvertedToCloneCd_WhenWeReadIt_ThenTheIsoAndTheAudioComeBack() {
    var aaru = Aaru();
    var poweriso = Environment.GetEnvironmentVariable("CWB_POWERISO");
    if (string.IsNullOrEmpty(poweriso)) Skip("CWB_POWERISO is not set.");
    Need("genisoimage");
    var src = Directory.CreateDirectory(Path.Combine(this._tmp, "src")).FullName;
    File.WriteAllText(Path.Combine(src, "numbers.txt"), string.Join('\n', Enumerable.Range(1, 30000)));
    var audio = CloneCdMultiTrackTests.Random(400 * 2352, 26);
    File.WriteAllBytes(Path.Combine(this._tmp, "audio.bin"), audio);
    File.WriteAllText(Path.Combine(this._tmp, "mix.cue"),
      "FILE \"data.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
      "FILE \"audio.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:02:00\n");
    var r = Wsl($"cd {W(this._tmp)} && genisoimage -quiet -V MIX -o data.iso src && '{poweriso}' convert data.iso -o data.bin -ot bin >/dev/null; " +
      $"'{aaru}' image convert mix.cue aaru.ccd");
    Assert.That(r.Code, Is.Zero, r.Out);

    var tracks = ReadBack(Path.Combine(this._tmp, "aaru.ccd"));
    Assert.That(tracks["track-01.iso"], Is.EqualTo(File.ReadAllBytes(Path.Combine(this._tmp, "data.iso"))), "the audio pause after the data track is left out");
    Assert.That(CloneCdLayout.PcmFromWave(tracks["track-02.wav"]), Is.EqualTo(audio[(150 * 2352)..]));
  }
}
