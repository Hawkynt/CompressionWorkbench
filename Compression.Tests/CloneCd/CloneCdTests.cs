using System.Text;
using FileFormat.CloneCd;

namespace Compression.Tests.CloneCd;

[TestFixture]
public class CloneCdTests {

  private const string Ccd =
    "[CloneCD]\r\n" +
    "Version=3\r\n" +
    "[Disc]\r\n" +
    "TocEntries=3\r\n" +
    "Sessions=1\r\n" +
    "DataTracksScrambled=0\r\n" +
    "[Session 1]\r\n" +
    "PreGapMode=1\r\n" +
    "[Entry 0]\r\n" +
    "Session=1\r\n" +
    "[TRACK 1]\r\n" +
    "MODE=2\r\n" +
    "INDEX 0=0\r\n" +
    "INDEX 1=0\r\n" +
    "[TRACK 2]\r\n" +
    "MODE=0\r\n" +
    "INDEX 1=12345\r\n";

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new CloneCdFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("CloneCd"));
    Assert.That(d.Extensions, Contains.Item(".ccd"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullAndMetadata() {
    var bytes = Encoding.UTF8.GetBytes(Ccd);
    var d = new CloneCdFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ccd"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(bytes.Length));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndMetadataParsed() {
    var bytes = Encoding.UTF8.GetBytes(Ccd);
    var d = new CloneCdFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ccd_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(bytes);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ccd"));
      Assert.That(full, Is.EqualTo(bytes));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("version=3"));
      Assert.That(meta, Does.Contain("sessions=1"));
      Assert.That(meta, Does.Contain("session_count=1"));
      Assert.That(meta, Does.Contain("track_count=2"));
      Assert.That(meta, Does.Contain("[Track1]"));
      Assert.That(meta, Does.Contain("mode=2"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_DiscoversCoLocatedImg() {
    var dir = Path.Combine(Path.GetTempPath(), "ccd_img_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var outDir = Path.Combine(dir, "out");
    Directory.CreateDirectory(outDir);
    try {
      var ccdPath = Path.Combine(dir, "disc.ccd");
      File.WriteAllText(ccdPath, Ccd);
      var imgData = new byte[] { 9, 8, 7, 6 };
      File.WriteAllBytes(Path.Combine(dir, "disc.img"), imgData);

      var d = new CloneCdFormatDescriptor();
      using var fs = File.OpenRead(ccdPath);
      var entries = d.List(fs, null);
      Assert.That(entries.Any(e => e.Name == "disc.img"), Is.True);

      fs.Position = 0;
      d.Extract(fs, outDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "disc.img"));
      Assert.That(extracted, Is.EqualTo(imgData));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var bytes = Encoding.UTF8.GetBytes("not a ccd file at all\nrandom garbage===\n");
    var d = new CloneCdFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ccd_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(bytes);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ccd"));
      Assert.That(full, Is.EqualTo(bytes));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
