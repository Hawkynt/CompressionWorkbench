using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileFormat.XMash;
using FileFormat.Xpk;

namespace Compression.Tests.XMash;

[TestFixture]
public class XMashTests {
  private const string FloppySha256 = "59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9";

  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "XMash", "ReferenceVectors", name);

  /// <summary>The files on the floppy the reference vectors were made from (see ReferenceVectors/README.md).</summary>
  internal static Dictionary<string, byte[]> TestFloppyFiles() {
    var sb = new StringBuilder();
    for (var i = 0; i < 300; ++i) sb.Append($"Line {i:D3}: the quick brown fox jumps over the lazy dog.\n");
    var table = new byte[8192];
    for (var i = 0; i < table.Length; ++i) table[i] = (byte)(i % 37 * 7);
    var rnd = new byte[3000];
    var x = 0x12345678u;
    for (var i = 0; i < rnd.Length; ++i) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; rnd[i] = (byte)x; }
    return new() {
      ["readme.txt"] = Encoding.ASCII.GetBytes(sb.ToString()),
      ["table.bin"] = table,
      ["noise.bin"] = rnd,
      ["zeros.bin"] = new byte[5000],
    };
  }

  /// <summary>The whole floppy, as the full-disk vectors rebuild it (its SHA-256 is pinned).</summary>
  private static byte[] TestFloppy() {
    var image = new XMashReader(File.OpenRead(VectorPath("full-nuke.xms"))).ExtractDisk();
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(image)), Is.EqualTo(FloppySha256));
    return image;
  }

  // ── Archives written by X-Mash 1.2 ──────────────────────────────────────────

  [Test, Category("RealFiles")]
  [TestCase("full-nuke.xms", "NUKE")]
  [TestCase("full-sqsh.xms", "SQSH")]
  [TestCase("banner-rlen.xms", "RLEN")]
  public void ProducerArchive_RebuildsTheFloppyExactly(string file, string packer) {
    var r = new XMashReader(File.OpenRead(VectorPath(file)));
    Assert.That(r.Packers, Is.EqualTo(new[] { packer }));
    Assert.That((r.FirstTrack, r.EndTrack), Is.EqualTo((0, 160)));
    var image = r.ExtractDisk();
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(image)), Is.EqualTo(FloppySha256));
  }

  [Test, Category("RealFiles")]
  public void ProducerArchive_Banner_IsDecoded() {
    var r = new XMashReader(File.OpenRead(VectorPath("banner-rlen.xms")));
    // X-Mash stores the text with its terminating NUL.
    Assert.That(r.Banners.Single(), Is.EqualTo((byte[])[.. "CWB test banner\nsecond line\n"u8, 0]));
    Assert.That(r.Descriptions, Is.Empty);
  }

  [Test, Category("RealFiles")]
  public void ProducerArchive_CylinderRange_LandsInPlace_RestIsZero() {
    var r = new XMashReader(File.OpenRead(VectorPath("part-nuke.xms")));
    Assert.That((r.FirstTrack, r.EndTrack), Is.EqualTo((76, 84)));
    var image = r.ExtractDisk();
    var floppy = TestFloppy();
    var from = 76 * XMashReader.TrackSize;
    var to = 84 * XMashReader.TrackSize;
    Assert.That(image.AsSpan(from, to - from).SequenceEqual(floppy.AsSpan(from, to - from)), Is.True);
    Assert.That(image.AsSpan(0, from).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(image.AsSpan(to).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test, Category("RealFiles")]
  public void ProducerArchive_VolumeFiles_MatchTheSource() {
    var image = new XMashReader(File.OpenRead(VectorPath("full-sqsh.xms"))).ExtractDisk();
    var expected = TestFloppyFiles();
    using var adf = new FileSystem.Adf.AdfReader(new MemoryStream(image));
    Assert.That(adf.Entries.Select(e => e.FullPath), Is.EquivalentTo(expected.Keys));
    foreach (var entry in adf.Entries)
      Assert.That(adf.Extract(entry), Is.EqualTo(expected[entry.FullPath]), entry.FullPath);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsImageAndBanner_AndExtracts() {
    var d = new XMashFormatDescriptor();
    using var fs = File.OpenRead(VectorPath("banner-rlen.xms"));
    var list = d.List(fs, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "disk.adf", "banner.txt" }));
    Assert.That(list[0].OriginalSize, Is.EqualTo(901120));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-xmash-" + Guid.NewGuid().ToString("N"));
    try {
      fs.Position = 0;
      d.Extract(fs, dir, null, ["banner.txt"]);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "banner.txt")), Has.Length.EqualTo(29));
      Assert.That(File.Exists(Path.Combine(dir, "disk.adf")), Is.False);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Magic_IsDetectedAsXMash() {
    var head = File.ReadAllBytes(VectorPath("part-nuke.xms"))[..64];
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(head).ToString(), Is.EqualTo("XMash"));
  }

  // ── Synthetic: block layout edges ───────────────────────────────────────────

  private static byte[] Block(char type, int first, int count, byte[] payload) {
    var b = new byte[7 + payload.Length];
    b[0] = (byte)type;
    b[1] = (byte)first;
    b[2] = (byte)count;
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(3), (uint)payload.Length);
    payload.CopyTo(b, 7);
    return b;
  }

  [Test, Category("RoundTrip")]
  public void OurXpkTrackBlock_RoundTrips() {
    var track = new byte[XMashReader.TrackSize * 2];
    for (var i = 0; i < track.Length; ++i) track[i] = (byte)(i / 100);
    byte[] archive = [.. "MSH"u8, .. Block('D', 158, 2, XpkStream.Encode(track))];
    var image = new XMashReader(new MemoryStream(archive)).ExtractDisk();
    Assert.That(image.AsSpan(158 * XMashReader.TrackSize).SequenceEqual(track), Is.True);
  }

  [Test, Category("Exception")]
  public void WrongUnpackedSize_Throws() {
    byte[] archive = [.. "MSH"u8, .. Block('D', 0, 2, XpkStream.Encode(new byte[XMashReader.TrackSize]))];
    Assert.Throws<InvalidDataException>(() => new XMashReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Boundary")]
  [TestCase(159, 2)]
  [TestCase(0, 0)]
  public void TrackRangeOutsideTheDisk_Throws(int first, int count) {
    byte[] archive = [.. "MSH"u8, .. Block('D', first, count, XpkStream.Encode(new byte[16]))];
    Assert.Throws<InvalidDataException>(() => _ = new XMashReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void TruncatedBlock_Throws_ButListFallsBack() {
    var data = File.ReadAllBytes(VectorPath("part-nuke.xms"))[..^10];
    Assert.Throws<InvalidDataException>(() => _ = new XMashReader(new MemoryStream(data)));
    Assert.That(new XMashFormatDescriptor().List(new MemoryStream(data), null).Single().Name, Is.EqualTo("FULL.xms"));
  }

  [Test, Category("Exception")]
  public void UnknownBlockType_Throws() {
    byte[] archive = [.. "MSH"u8, .. Block('D', 0, 1, XpkStream.Encode(new byte[XMashReader.TrackSize])), .. Block('Z', 0, 0, [1, 2])];
    Assert.Throws<InvalidDataException>(() => _ = new XMashReader(new MemoryStream(archive)));
  }
}
