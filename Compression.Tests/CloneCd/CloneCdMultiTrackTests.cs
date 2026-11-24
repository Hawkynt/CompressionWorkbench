using System.Text;
using Compression.Registry;
using FileFormat.CloneCd;

namespace Compression.Tests.CloneCd;

/// <summary>
/// Multi-session discs with audio, Mode 1 and Mode 2 tracks: the layout rules of ECMA-130 clause 20 (pre-gaps and
/// post-gaps between tracks of different kinds), the P and Q channels of clause 22, the session gap, and reading the
/// tracks back from the image set.
/// </summary>
[TestFixture]
public class CloneCdMultiTrackTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = Directory.CreateTempSubdirectory("cwb-ccdm-").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  internal static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  /// <summary>Valid 2,336-byte Mode 2 sectors alternating Form 1 and Form 2, with their EDC/ECC in place.</summary>
  internal static byte[] Mode2Sectors(int count, int seed) {
    var result = new byte[count * 2336];
    var raw = new byte[2352];
    for (var i = 0; i < count; ++i) {
      var payload = Random(2336, seed + i);
      byte subMode = i % 2 == 0 ? (byte)0x08 : (byte)0x20; // data (Form 1) / Form 2
      payload[0] = payload[4] = 1;
      payload[1] = payload[5] = 0;
      payload[2] = payload[6] = subMode;
      payload[3] = payload[7] = 0;
      CloneCdWriter.WriteSector(raw, CloneCdTrackKind.Mode2, 1000, payload);
      raw.AsSpan(16).CopyTo(result.AsSpan(i * 2336));
    }
    return result;
  }

  internal static byte[] Wave(byte[] pcm) => CloneCdLayout.Wave(pcm);

  private string Create(params (string Name, byte[] Data)[] tracks) {
    var ccd = Path.Combine(this._tmp, "disc.ccd");
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

  private static byte[] Sub(string ccd, int sector) => File.ReadAllBytes(Path.ChangeExtension(ccd, ".sub")).AsSpan(sector * 96, 96).ToArray();
  private static bool P(byte[] sub) => sub.AsSpan(0, 12).IndexOfAnyExcept((byte)0xFF) < 0;
  private static string Q(byte[] sub) => Convert.ToHexString(sub, 12, 10);

  [Test, Category("HappyPath")]
  public void GivenDataAudioAndMode2Tracks_WhenCreatedAndRead_ThenEveryTrackComesBack() {
    var iso = Random(300 * 2048, 1);
    var pcm = Random(320 * 2352, 2);
    var mode2 = Mode2Sectors(310, 3);
    var ccd = this.Create(("data.iso", iso), ("music.wav", Wave(pcm)), ("xa.mode2", mode2));

    var tracks = ReadBack(ccd);
    Assert.That(tracks.Keys, Is.EquivalentTo(new[] { "track-01.iso", "track-02.wav", "track-03.mode2" }));
    // The data track ends with its 150-sector post-gap before the audio track (zero user data).
    Assert.That(tracks["track-01.iso"].AsSpan(0, iso.Length).SequenceEqual(iso), Is.True);
    Assert.That(tracks["track-01.iso"], Has.Length.EqualTo(iso.Length + 150 * 2048));
    Assert.That(tracks["track-01.iso"].AsSpan(iso.Length).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(CloneCdLayout.PcmFromWave(tracks["track-02.wav"]), Is.EqualTo(pcm));
    Assert.That(tracks["track-03.mode2"], Is.EqualTo(mode2));
  }

  [Test]
  public void GivenDataThenAudioThenData_WhenLaidOut_ThenTheGapsFollowEcma130() {
    var ccd = this.Create(("a.iso", Random(300 * 2048, 4)), ("b.wav", Wave(Random(300 * 2352, 5))), ("c.mode2", Mode2Sectors(300, 6)));
    var text = File.ReadAllText(ccd);
    // Track 1: 300 + 150 post-gap; track 2: 150 pause, 300 audio; track 3: 75 coded as audio + 150 as Mode 2.
    Assert.That(text, Does.Contain("[TRACK 2]\r\nMODE=0\r\nINDEX 0=450\r\nINDEX 1=600\r\n"));
    Assert.That(text, Does.Contain("[TRACK 3]\r\nMODE=2\r\nINDEX 0=900\r\nINDEX 1=1125\r\n"));
    Assert.That(text, Does.Contain("Point=0xa0\r\nADR=0x01\r\nControl=0x04\r\nTrackNo=0\r\nAMin=0\r\nASec=0\r\nAFrame=0\r\nALBA=-150\r\nZero=0\r\nPMin=1\r\nPSec=32\r\n"), "A0: first track 1, CD-ROM XA disc");
    Assert.That(new FileInfo(Path.ChangeExtension(ccd, ".img")).Length, Is.EqualTo(1425L * 2352));

    var img = File.ReadAllBytes(Path.ChangeExtension(ccd, ".img"));
    Assert.Multiple(() => {
      Assert.That(img[449 * 2352 + 15], Is.EqualTo(1), "the post-gap is Mode 1");
      Assert.That(img.AsSpan(450 * 2352, 2352).ContainsAnyExcept((byte)0), Is.False, "the audio pause is silence");
      Assert.That(img.AsSpan(974 * 2352, 2352).ContainsAnyExcept((byte)0), Is.False, "the first pre-gap interval is coded as the audio track");
      Assert.That(img[975 * 2352 + 15], Is.EqualTo(2), "the second interval is coded as the Mode 2 track");

      // Q: index 00 counts down to zero in the last pause section, index 01 starts at zero.
      Assert.That(Q(Sub(ccd, 450)), Is.EqualTo("01020000017400000800"), "track 2 pause starts at 00:01:74, absolute 00:08:00");
      Assert.That(Q(Sub(ccd, 599)), Is.EqualTo("01020000000000000974"), "the last pause section is at zero");
      Assert.That(Q(Sub(ccd, 600)), Is.EqualTo("01020100000000001000"), "track 2 index 01 at 00:00:00, absolute 00:10:00");
      Assert.That(Q(Sub(ccd, 900))[..2], Is.EqualTo("01"), "the first interval carries the audio control field");
      Assert.That(Q(Sub(ccd, 975))[..8], Is.EqualTo("41030000"), "the second interval carries the data control field");
      // P: a flag ends on the first user-data section of every track and of the lead-out.
      Assert.That(!P(Sub(ccd, 449)) && P(Sub(ccd, 450)) && P(Sub(ccd, 600)) && !P(Sub(ccd, 601)), Is.True);
      Assert.That(P(Sub(ccd, 900)) && P(Sub(ccd, 1125)) && !P(Sub(ccd, 1126)), Is.True);
      Assert.That(P(Sub(ccd, 1424 - 148)) && !P(Sub(ccd, 1424 - 149)), Is.True);
    });
  }

  [Test]
  public void GivenTwoSessions_WhenCreated_ThenTheSecondStartsAfterTheSessionGapAndReadsBack() {
    var pcm = Random(400 * 2352, 7);
    var iso = Random(257 * 2048, 8);
    var ccd = this.Create(("audio.wav", Wave(pcm)), ("session-02/data.iso", iso));
    var text = File.ReadAllText(ccd);

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("Sessions=2\r\n"));
      Assert.That(text, Does.Contain("[TRACK 2]\r\nMODE=1\r\nINDEX 1=11800\r\n"), "400 + 6750 lead-out + 4500 lead-in + 150 pre-gap");
      Assert.That(text, Does.Contain("Point=0xb0\r\nADR=0x05\r\nControl=0x00\r\nTrackNo=0\r\nAMin=2\r\nASec=37\r\nAFrame=25\r\nALBA=11650\r\n"));
      Assert.That(new FileInfo(Path.ChangeExtension(ccd, ".img")).Length, Is.EqualTo(657L * 2352), "the session gap and the second pre-gap are not stored");
      var tracks = ReadBack(ccd);
      Assert.That(CloneCdLayout.PcmFromWave(tracks["track-01.wav"]), Is.EqualTo(pcm));
      Assert.That(tracks["session-02/track-02.iso"], Is.EqualTo(iso));
      Assert.That(Q(Sub(ccd, 400)), Is.EqualTo("41020100000000023925"), "track 2 index 01 at absolute 02:39:25");
    });
  }

  [Test]
  public void GivenASingleIso_WhenBuiltThroughEitherEntry_ThenTheImageIsTheSame() {
    var iso = Random(200 * 2048, 9);
    var a = CloneCdWriter.FromIso(iso);
    var b = CloneCdWriter.Build([[new CloneCdTrackSource(CloneCdTrackKind.Mode1, iso)]]);
    Assert.That(a.Img, Is.EqualTo(b.Img));
    Assert.That(a.Sub, Is.EqualTo(b.Sub));
    Assert.That(Encoding.ASCII.GetString(a.Ccd), Does.Not.Contain("INDEX 0").And.Not.Contain("0xb0"));
  }

  [Test]
  public void GivenAControlFileWithoutTrackSections_WhenRead_ThenKindsComeFromControlAndSectorMode() {
    var iso = Random(300 * 2048, 10);
    var pcm = Random(300 * 2352, 11);
    var ccd = this.Create(("a.iso", iso), ("b.wav", Wave(pcm)));
    var text = File.ReadAllText(ccd);
    File.WriteAllText(ccd, text[..text.IndexOf("[TRACK 1]", StringComparison.Ordinal)]);

    var tracks = ReadBack(ccd);
    // Without INDEX 0 the data track runs to the audio track's point: its post-gap stays, the silent pause is left out.
    Assert.That(tracks["track-01.iso"], Has.Length.EqualTo((300 + 150) * 2048));
    Assert.That(CloneCdLayout.PcmFromWave(tracks["track-02.wav"]), Is.EqualTo(pcm));
  }

  [Test]
  public void GivenAudioNotInWholeSectors_WhenCreated_ThenItIsPaddedWithSilence() {
    var pcm = Random(1000 * 4 + 4, 12);
    var tracks = ReadBack(this.Create(("odd.wav", Wave(pcm))));
    var back = CloneCdLayout.PcmFromWave(tracks["track-01.wav"]);
    Assert.That(back, Has.Length.EqualTo(2 * 2352));
    Assert.That(back.AsSpan(0, pcm.Length).SequenceEqual(pcm) && !back.AsSpan(pcm.Length).ContainsAnyExcept((byte)0), Is.True);
  }

  // ── refusals and malformed input ─────────────────────────────────────────────────────────────

  [Test]
  public void GivenAWaveThatIsNotCdAudio_WhenCreated_ThenRefused() {
    var wav = Wave(new byte[2352]);
    BitConverter.GetBytes(48000).CopyTo(wav, 24);
    Assert.Throws<NotSupportedException>(() => this.Create(("x.wav", wav)));
    Assert.Throws<InvalidDataException>(() => this.Create(("y.wav", "not a wave"u8.ToArray())));
  }

  [Test]
  public void GivenMode2DataNotInWholeSectors_WhenCreated_ThenRefused()
    => Assert.Throws<InvalidDataException>(() => this.Create(("x.mode2", new byte[2335])));

  [Test]
  public void GivenSessionsWithAGap_WhenCreated_ThenRefused()
    => Assert.Throws<ArgumentException>(() => this.Create(("a.iso", new byte[2048]), ("session-03/b.iso", new byte[2048])));

  [Test]
  public void GivenAnInputThatIsNoTrack_WhenOffered_ThenDeclined() {
    var d = new CloneCdFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.CanAccept(new ArchiveInputInfo("x", "readme.txt", false), out _), Is.False);
      Assert.That(d.CanAccept(new ArchiveInputInfo("x", "a/b/c.iso", false), out _), Is.False);
      Assert.That(d.CanAccept(new ArchiveInputInfo("x", "session-02/c.wav", false), out _), Is.True);
      Assert.That(d.CanAccept(new ArchiveInputInfo("x", "x.mode2", false), out _), Is.True);
    });
  }

  [Test]
  public void GivenAnImgShorterThanTheControlFile_WhenListed_ThenNoTrackIsShownAndNothingThrows() {
    var ccd = this.Create(("a.iso", Random(300 * 2048, 13)));
    var img = Path.ChangeExtension(ccd, ".img");
    File.WriteAllBytes(img, File.ReadAllBytes(img)[..(100 * 2352)]);
    Assert.That(ReadBack(ccd), Is.Empty);
  }

  [Test]
  public void GivenScrambledDataTracks_WhenListed_ThenNoTrackIsShown() {
    var ccd = this.Create(("a.iso", Random(300 * 2048, 14)));
    File.WriteAllText(ccd, File.ReadAllText(ccd).Replace("DataTracksScrambled=0", "DataTracksScrambled=1", StringComparison.Ordinal));
    Assert.That(ReadBack(ccd), Is.Empty);
  }

  [Test]
  public void GivenAControlFileWithoutLeadOut_WhenParsed_ThenThereIsNoLayout() {
    var text = Encoding.ASCII.GetString(CloneCdWriter.FromIso(new byte[2048 * 20]).Ccd).Replace("Point=0xa2", "Point=0xa9", StringComparison.Ordinal);
    Assert.That(CloneCdLayout.TryParse(text), Is.Null);
    Assert.That(CloneCdLayout.TryParse("[CloneCD]\nVersion=3\n[Entry 0]\nPoint=zz\n"), Is.Null);
  }
}
