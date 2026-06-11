#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Mpo;

/// <summary>
/// Behaviour of <see cref="MpoFormatDescriptor"/> as a per-picture pseudo-archive:
/// FULL.mpo + metadata.ini + one JPEG per embedded picture. Uses a synthetic MPO
/// = two concatenated minimal JPEG streams (SOI..EOI).
/// </summary>
[TestFixture]
public class MpoPseudoArchiveTests {

  private static byte[] BuildJpeg(byte marker) =>
    // SOI, a tiny APP0-ish filler byte run, EOI. The filler byte differs per view
    // so the two pictures are distinguishable on extract.
    [0xFF, 0xD8, marker, marker, marker, 0xFF, 0xD9];

  private static byte[] BuildMpo() {
    var a = BuildJpeg(0x11);
    var b = BuildJpeg(0x22);
    var buf = new byte[a.Length + b.Length];
    a.CopyTo(buf, 0);
    b.CopyTo(buf, a.Length);
    return buf;
  }

  [Test]
  public void List_Exposes_Full_Metadata_And_Pictures() {
    var desc = new MpoFormatDescriptor();
    using var s = new MemoryStream(BuildMpo());
    var entries = desc.List(s, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("FULL.mpo"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("pictures/picture_00.jpg"));
      Assert.That(names, Does.Contain("pictures/picture_01.jpg"));
    });
    Assert.That(entries.First(e => e.Name == "pictures/picture_00.jpg").Kind, Is.EqualTo("Frame"));
  }

  [Test]
  public void Extract_Full_ByteIdentical_And_Each_Picture_Is_A_Jpeg() {
    var original = BuildMpo();
    var desc = new MpoFormatDescriptor();
    using var s = new MemoryStream(original);
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_mpo_{Guid.NewGuid():N}");
    try {
      desc.Extract(s, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "FULL.mpo")), Is.EqualTo(original));

      var p0 = File.ReadAllBytes(Path.Combine(outDir, "pictures", "picture_00.jpg"));
      var p1 = File.ReadAllBytes(Path.Combine(outDir, "pictures", "picture_01.jpg"));
      Assert.Multiple(() => {
        Assert.That(p0[0], Is.EqualTo(0xFF)); Assert.That(p0[1], Is.EqualTo(0xD8)); // SOI
        Assert.That(p0[^2], Is.EqualTo(0xFF)); Assert.That(p0[^1], Is.EqualTo(0xD9)); // EOI
        Assert.That(p0, Does.Contain((byte)0x11)); // view-0 filler
        Assert.That(p1, Does.Contain((byte)0x22)); // view-1 filler
      });
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void List_DoesNotThrow_On_Malformed() {
    var desc = new MpoFormatDescriptor();
    using var s = new MemoryStream([0xFF, 0xD8, 0x00, 0x00]); // SOI but no EOI
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = desc.List(s, null));
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.mpo"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
