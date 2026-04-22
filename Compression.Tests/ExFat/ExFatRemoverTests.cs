#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

[TestFixture]
public class ExFatRemoverTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new ExFatWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void RemovedFileContentIsZeroedFromImage() {
    // Marker bytes distinct enough to locate in a ~8MB raw image.
    var marker = System.Text.Encoding.ASCII.GetBytes("HELLOSECRET12345");
    var image = BuildImageWith(("TEST.TXT", marker));

    Assert.That(FindMarker(image, marker), Is.True, "precondition: marker should be in image");
    ExFatRemover.Remove(image, "TEST.TXT");
    Assert.That(FindMarker(image, marker), Is.False,
      "marker bytes must no longer be recoverable from the image after secure remove");
  }

  [Test]
  public void RemovedFileDirEntryIsZeroed() {
    var image = BuildImageWith(("GONE.BIN", new byte[100]));
    ExFatRemover.Remove(image, "GONE.BIN");
    // The name is stored as UTF-16LE in 0xC1 entries — search for the UTF-16 form too.
    var nameBytes16 = System.Text.Encoding.Unicode.GetBytes("GONE.BIN");
    Assert.That(FindMarker(image, nameBytes16), Is.False,
      "filename must be fully wiped from directory (UTF-16 form)");
  }

  [Test]
  public void RemovedFileIsNotReadableAfterwards() {
    var image = BuildImageWith(
      ("A.BIN", new byte[] { 1, 2, 3 }),
      ("B.BIN", new byte[] { 4, 5, 6 }));
    ExFatRemover.Remove(image, "A.BIN");

    using var ms = new MemoryStream(image);
    var reader = new ExFatReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "A.BIN"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "B.BIN"), Is.True);
  }

  [Test]
  public void RemovingNonexistentThrows() {
    var image = BuildImageWith(("A.TXT", [1]));
    Assert.Throws<FileNotFoundException>(() => ExFatRemover.Remove(image, "MISSING.TXT"));
  }

  [Test]
  public void RemainingFileIsStillReadable() {
    var keep = "KEEP_ME_PLEASE"u8.ToArray();
    var image = BuildImageWith(
      ("TRASH.TXT", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
      ("KEEP.TXT", keep));
    ExFatRemover.Remove(image, "TRASH.TXT");

    using var ms = new MemoryStream(image);
    var reader = new ExFatReader(ms);
    var kept = reader.Entries.FirstOrDefault(e => e.Name == "KEEP.TXT");
    Assert.That(kept, Is.Not.Null);
    Assert.That(reader.Extract(kept!), Is.EqualTo(keep));
  }

  [Test]
  public void DescriptorAsModifiable_RemoveWorks() {
    var marker = System.Text.Encoding.ASCII.GetBytes("FORENSICS_BYTES");
    var buf = BuildImageWith(("SECRET.TXT", marker));
    using var ms = new MemoryStream();
    ms.Write(buf);
    ms.SetLength(buf.Length);

    var desc = new ExFatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
    ((IArchiveModifiable)desc).Remove(ms, ["SECRET.TXT"]);

    Assert.That(FindMarker(ms.ToArray(), marker), Is.False);
  }

  [Test]
  public void DescriptorAsModifiable_AddCombinesFiles() {
    var initial = BuildImageWith(("FIRST.TXT", new byte[] { 0xAA }));
    using var ms = new MemoryStream();
    ms.Write(initial);
    ms.SetLength(initial.Length);

    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, [0xBB]);
      ((IArchiveModifiable)new ExFatFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmpFile, "SECOND.TXT", false)]);

      ms.Position = 0;
      var reader = new ExFatReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "FIRST.TXT"), Is.True);
      Assert.That(reader.Entries.Any(e => e.Name == "SECOND.TXT"), Is.True);
    } finally {
      File.Delete(tmpFile);
    }
  }

  private static bool FindMarker(byte[] image, byte[] marker) {
    for (var i = 0; i <= image.Length - marker.Length; ++i) {
      var match = true;
      for (var j = 0; j < marker.Length; ++j) {
        if (image[i + j] != marker[j]) { match = false; break; }
      }
      if (match) return true;
    }
    return false;
  }
}
