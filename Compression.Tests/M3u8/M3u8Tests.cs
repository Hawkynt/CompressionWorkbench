using System.Text;
using FileFormat.M3u8;

namespace Compression.Tests.M3u8;

[TestFixture]
public class M3u8Tests {

  private const string MasterPlaylist =
    "#EXTM3U\n" +
    "#EXT-X-VERSION:6\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=640x360,CODECS=\"avc1.42c01e,mp4a.40.2\"\n" +
    "low/index.m3u8\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=2560000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\"\n" +
    "mid/index.m3u8\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=7680000,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\"\n" +
    "high/index.m3u8\n";

  private const string MediaPlaylist =
    "#EXTM3U\n" +
    "#EXT-X-VERSION:3\n" +
    "#EXT-X-TARGETDURATION:10\n" +
    "#EXT-X-MEDIA-SEQUENCE:0\n" +
    "#EXT-X-PLAYLIST-TYPE:VOD\n" +
    "#EXTINF:9.009,\n" +
    "fileSequence0.ts\n" +
    "#EXTINF:9.009,\n" +
    "fileSequence1.ts\n" +
    "#EXTINF:3.003,\n" +
    "fileSequence2.ts\n" +
    "#EXT-X-ENDLIST\n";

  [Test, Category("HappyPath")]
  public void Read_MasterPlaylist_ParsesVariants() {
    var p = M3u8Reader.Read(MasterPlaylist);
    Assert.That(p.IsMaster, Is.True);
    Assert.That(p.Version, Is.EqualTo(6));
    Assert.That(p.Variants, Has.Count.EqualTo(3));
    Assert.That(p.Variants[0].Uri, Is.EqualTo("low/index.m3u8"));
    Assert.That(p.Variants[0].Attributes["BANDWIDTH"], Is.EqualTo("1280000"));
    Assert.That(p.Variants[0].Attributes["RESOLUTION"], Is.EqualTo("640x360"));
    Assert.That(p.Variants[0].Attributes["CODECS"], Is.EqualTo("avc1.42c01e,mp4a.40.2"));
    Assert.That(p.Segments, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Read_MediaPlaylist_ParsesSegmentsAndDirectives() {
    var p = M3u8Reader.Read(MediaPlaylist);
    Assert.That(p.IsMaster, Is.False);
    Assert.That(p.Version, Is.EqualTo(3));
    Assert.That(p.TargetDurationSeconds, Is.EqualTo(10));
    Assert.That(p.MediaSequence, Is.EqualTo(0));
    Assert.That(p.PlaylistType, Is.EqualTo("VOD"));
    Assert.That(p.EndList, Is.True);
    Assert.That(p.Segments, Has.Count.EqualTo(3));
    Assert.That(p.Segments[0].DurationSeconds, Is.EqualTo(9.009).Within(0.0001));
    Assert.That(p.Segments[2].Uri, Is.EqualTo("fileSequence2.ts"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsThreeEntries() {
    var data = Encoding.UTF8.GetBytes(MediaPlaylist);
    using var ms = new MemoryStream(data);
    var entries = new M3u8FormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "metadata.ini", "playlist.txt", "segments.txt" }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesAllThreeFiles() {
    var data = Encoding.UTF8.GetBytes(MasterPlaylist);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new M3u8FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "playlist.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "segments.txt")), Is.True);

      var segLines = File.ReadAllLines(Path.Combine(tmp, "segments.txt"));
      Assert.That(segLines, Has.Length.EqualTo(3));
      Assert.That(segLines[0], Does.Contain("low/index.m3u8"));
      Assert.That(segLines[0], Does.Contain("bandwidth=1280000"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_MissingExtM3uHeader_Throws() {
    Assert.That(() => M3u8Reader.Read("not a playlist\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void ParseAttributes_HandlesQuotedCommas() {
    var result = M3u8Reader.ParseAttributes("BANDWIDTH=1000,CODECS=\"avc1,mp4a\",RESOLUTION=640x360");
    Assert.That(result["BANDWIDTH"], Is.EqualTo("1000"));
    Assert.That(result["CODECS"], Is.EqualTo("avc1,mp4a"));
    Assert.That(result["RESOLUTION"], Is.EqualTo("640x360"));
  }

  [Test, Category("EdgeCase")]
  public void Read_PlaylistWithBlankLines_StillParses() {
    var text = "#EXTM3U\n\n#EXT-X-VERSION:3\n\n#EXTINF:1,\nfoo.ts\n";
    var p = M3u8Reader.Read(text);
    Assert.That(p.Segments, Has.Count.EqualTo(1));
    Assert.That(p.Segments[0].Uri, Is.EqualTo("foo.ts"));
  }
}
