using System.Buffers.Binary;
using System.Security.Cryptography;
using FileFormat.Dcs;
using FileFormat.Xpk;

namespace Compression.Tests.Dcs;

[TestFixture]
public class DcsTests {
  // The cwbtest floppy the X-Mash vectors come from (see ReferenceVectors/README.md).
  private const string FloppySha256 = "59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9";

  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Dcs", "ReferenceVectors", name);

  private static byte[] Sha(byte[] data) => SHA256.HashData(data);

  // ── Archives written by DCS 0.10 ────────────────────────────────────────────

  [Test, Category("RealFiles")]
  [TestCase("nuke.dcs", new[] { "NUKE" })]
  [TestCase("rlen.dcs", new[] { "RLEN" })]
  [TestCase("multi.dcs", new[] { "RLEN", "NUKE" })]
  public void ProducerArchive_RebuildsTheFloppyExactly(string file, string[] packers) {
    var r = new DcsReader(File.OpenRead(VectorPath(file)));
    Assert.That(r.Packers, Is.EquivalentTo(packers));
    Assert.That((r.LowCylinder, r.HighCylinder, r.EncryptionPasses), Is.EqualTo((0, 79, 0)));
    Assert.That(Convert.ToHexStringLower(Sha(r.ExtractDisk())), Is.EqualTo(FloppySha256));
  }

  [Test, Category("RealFiles")]
  public void ProducerArchive_CylinderRange_LandsInPlace_RestIsZero() {
    var r = new DcsReader(File.OpenRead(VectorPath("part.dcs")));
    Assert.That((r.LowCylinder, r.HighCylinder), Is.EqualTo((38, 41)));
    var image = r.ExtractDisk();
    var floppy = new DcsReader(File.OpenRead(VectorPath("nuke.dcs"))).ExtractDisk();
    var from = 38 * DcsReader.CylinderSize;
    var to = 42 * DcsReader.CylinderSize;
    Assert.That(image.AsSpan(from, to - from).SequenceEqual(floppy.AsSpan(from, to - from)), Is.True);
    Assert.That(image.AsSpan(0, from).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(image.AsSpan(to).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test, Category("RealFiles")]
  public void ProducerArchive_VolumeFiles_MatchTheSource() {
    var image = new DcsReader(File.OpenRead(VectorPath("multi.dcs"))).ExtractDisk();
    var expected = XMash.XMashTests.TestFloppyFiles();
    using var adf = new FileSystem.Adf.AdfReader(new MemoryStream(image));
    Assert.That(adf.Entries.Select(e => e.FullPath), Is.EquivalentTo(expected.Keys));
    foreach (var entry in adf.Entries)
      Assert.That(adf.Extract(entry), Is.EqualTo(expected[entry.FullPath]), entry.FullPath);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsAndExtractsTheImage() {
    var d = new DcsFormatDescriptor();
    using var fs = File.OpenRead(VectorPath("nuke.dcs"));
    var entry = d.List(fs, null).Single();
    Assert.That((entry.Name, entry.OriginalSize, entry.Method), Is.EqualTo(("disk.adf", 901120L, "XPK NUKE")));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-dcs-" + Guid.NewGuid().ToString("N"));
    try {
      fs.Position = 0;
      d.Extract(fs, dir, null, null);
      Assert.That(Convert.ToHexStringLower(Sha(File.ReadAllBytes(Path.Combine(dir, "disk.adf")))), Is.EqualTo(FloppySha256));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Magic_IsDetectedAsDcs() {
    var head = File.ReadAllBytes(VectorPath("part.dcs"))[..64];
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(head).ToString(), Is.EqualTo("Dcs"));
  }

  // ── Synthetic: record layout edges ──────────────────────────────────────────

  private static byte[] Record(int cylinder, byte[] xpk) {
    var b = new byte[DcsReader.RecordHeaderSize + xpk.Length];
    "TR"u8.CopyTo(b);
    b[3] = (byte)cylinder;
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)xpk.Length);
    xpk.CopyTo(b, DcsReader.RecordHeaderSize);
    return b;
  }

  private static byte[] Cylinder(int seed) {
    var c = new byte[DcsReader.CylinderSize];
    for (var i = 0; i < c.Length; ++i) c[i] = (byte)((i / 64 + seed) * 7);
    return c;
  }

  private static readonly byte[] Header = [.. "DCS"u8, 0, 0, 0];

  [Test, Category("RoundTrip")]
  public void OurLastCylinder_LandsAtTheEnd() {
    var cylinder = Cylinder(3);
    byte[] archive = [.. Header, .. Record(79, XpkStream.Encode(cylinder, "RLEN"))];
    var image = new DcsReader(new MemoryStream(archive)).ExtractDisk();
    Assert.That(image.AsSpan(79 * DcsReader.CylinderSize).SequenceEqual(cylinder), Is.True);
  }

  [Test, Category("Exception")]
  public void WrongUnpackedSize_Throws() {
    byte[] archive = [.. Header, .. Record(0, XpkStream.Encode(new byte[DcsReader.CylinderSize - 512], "NONE"))];
    Assert.Throws<InvalidDataException>(() => new DcsReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Boundary")]
  [TestCase(80)]
  [TestCase(255)]
  public void CylinderPastTheDisk_Throws(int cylinder) {
    byte[] archive = [.. Header, .. Record(cylinder, XpkStream.Encode(new byte[16], "NONE"))];
    Assert.Throws<InvalidDataException>(() => _ = new DcsReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void CylinderGap_Throws() {
    var x = XpkStream.Encode(Cylinder(0), "RLEN");
    byte[] archive = [.. Header, .. Record(4, x), .. Record(6, x)];
    Assert.Throws<InvalidDataException>(() => _ = new DcsReader(new MemoryStream(archive)));
  }

  [Test, Category("Boundary")]
  public void HeaderOnly_Throws() {
    byte[] archive = [.. Header, .. "TR"u8];
    Assert.Throws<InvalidDataException>(() => _ = new DcsReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void EncryptedArchive_IsListedAsEncrypted_AndRefused() {
    var data = File.ReadAllBytes(VectorPath("part.dcs"));
    data[5] = 1;
    var r = new DcsReader(new MemoryStream(data));
    Assert.Throws<NotSupportedException>(() => r.ExtractDisk());
    Assert.That(new DcsFormatDescriptor().List(new MemoryStream(data), null).Single().IsEncrypted, Is.True);
  }

  [Test, Category("Exception")]
  public void TruncatedRecord_Throws_ButListFallsBack() {
    var data = File.ReadAllBytes(VectorPath("part.dcs"))[..^10];
    Assert.Throws<InvalidDataException>(() => _ = new DcsReader(new MemoryStream(data)));
    Assert.That(new DcsFormatDescriptor().List(new MemoryStream(data), null).Single().Name, Is.EqualTo("FULL.dcs"));
  }
}
