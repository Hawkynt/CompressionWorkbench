using System.Buffers.Binary;
using System.Security.Cryptography;
using FileFormat.PackDisk;
using FileFormat.Xpk;

namespace Compression.Tests.PackDisk;

[TestFixture]
public class PackDiskTests {
  private const string FloppySha256 = "59c11eab0538310e808ccf254ab4c69d8aef25e6d515685e83676d771e9ff5c9";

  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "PackDisk", "ReferenceVectors", name);

  // ── Archives written by PackDisk 1.1 ────────────────────────────────────────

  [Test, Category("RealFiles")]
  [TestCase("mode1.pdsk", new[] { "RLEN" })]
  [TestCase("mode2.pdsk", new[] { "RLEN", "NUKE" })]
  public void ProducerArchive_RebuildsTheFloppyExactly(string file, string[] packers) {
    var r = new PackDiskReader(File.OpenRead(VectorPath(file)));
    Assert.That(r.VolumeName, Is.EqualTo("CWBTEST"));
    Assert.That(r.Packers, Is.EquivalentTo(packers));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.ExtractDisk())), Is.EqualTo(FloppySha256));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsTheImage_AndDetectsTheMagic() {
    var data = File.ReadAllBytes(VectorPath("mode2.pdsk"));
    var list = new PackDiskFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(list.Single().Name, Is.EqualTo("disk.adf"));
    Assert.That(list[0].OriginalSize, Is.EqualTo(901120));
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(data.AsSpan(0, 64)).ToString(), Is.EqualTo("PackDisk"));
  }

  // ── Synthetic: table and cylinder edges ─────────────────────────────────────

  private static byte[] Build(Func<int, (byte[] Payload, bool Raw)> cylinder) {
    using var ms = new MemoryStream();
    var header = new byte[42 + 80 * 12];
    "DISKPACK"u8.CopyTo(header);
    header[9] = 1;
    "TEST"u8.CopyTo(header.AsSpan(12));
    var payloads = Enumerable.Range(0, 80).Select(cylinder).ToArray();
    var offset = header.Length;
    for (var c = 0; c < 80; ++c) {
      var e = header.AsSpan(42 + c * 12);
      BinaryPrimitives.WriteUInt16BigEndian(e, (ushort)payloads[c].Payload.Length);
      BinaryPrimitives.WriteUInt32BigEndian(e[2..], (uint)offset);
      e[10] = (byte)(payloads[c].Raw ? 1 : 0);
      offset += payloads[c].Payload.Length;
    }
    ms.Write(header);
    foreach (var p in payloads) ms.Write(p.Payload);
    return ms.ToArray();
  }

  private static byte[] Cylinder(int c) => Enumerable.Repeat((byte)(c + 1), PackDiskReader.CylinderSize).ToArray();

  [Test, Category("RoundTrip")]
  public void RawAndXpkCylinders_Rebuild() {
    var archive = Build(c => c % 2 == 0 ? (Cylinder(c), true) : (XpkStream.Encode(Cylinder(c)), false));
    var image = new PackDiskReader(new MemoryStream(archive)).ExtractDisk();
    for (var c = 0; c < 80; ++c)
      Assert.That(image.AsSpan(c * PackDiskReader.CylinderSize, PackDiskReader.CylinderSize).ToArray(), Is.All.EqualTo(c + 1), $"cylinder {c}");
  }

  [Test, Category("Exception")]
  public void XpkCylinder_OfTheWrongSize_Throws() {
    var archive = Build(c => (XpkStream.Encode(new byte[c == 5 ? 512 : PackDiskReader.CylinderSize]), false));
    Assert.Throws<InvalidDataException>(() => new PackDiskReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Boundary")]
  public void RawCylinder_OfTheWrongSize_IsRejected() {
    var archive = Build(c => c == 0 ? (new byte[100], true) : (Cylinder(c), true));
    Assert.Throws<InvalidDataException>(() => _ = new PackDiskReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void EntryPastTheEnd_Throws_ButListFallsBack() {
    var data = File.ReadAllBytes(VectorPath("mode1.pdsk"))[..^10];
    Assert.Throws<InvalidDataException>(() => _ = new PackDiskReader(new MemoryStream(data)));
    Assert.That(new PackDiskFormatDescriptor().List(new MemoryStream(data), null).Single().Name, Is.EqualTo("FULL.pdsk"));
  }

  [Test, Category("Exception")]
  [TestCase(0)]
  [TestCase(2)]
  public void OtherVersion_IsNotPackDisk(int version) {
    var data = File.ReadAllBytes(VectorPath("mode1.pdsk"));
    data[9] = (byte)version;
    Assert.Throws<InvalidDataException>(() => _ = new PackDiskReader(new MemoryStream(data)));
  }
}
