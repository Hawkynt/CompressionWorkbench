#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

[TestFixture]
public class NtfsRemoverTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new NtfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void RemovedNonResidentFileContentIsZeroedFromImage() {
    // Large payload forces non-resident $DATA (cluster-allocated). Marker must
    // disappear from every cluster the file occupied.
    var marker = System.Text.Encoding.ASCII.GetBytes("HELLOSECRET12345");
    var payload = new byte[4096];
    marker.CopyTo(payload, 0);
    System.Text.Encoding.ASCII.GetBytes("TAILMARKER_XYZZY").CopyTo(payload, payload.Length - 16);
    var image = BuildImageWith(("bigfile.bin", payload));

    Assert.That(FindMarker(image, marker), Is.True, "precondition: marker should be in image");
    NtfsRemover.Remove(image, "bigfile.bin");
    Assert.That(FindMarker(image, marker), Is.False,
      "non-resident marker must no longer be recoverable from the image after secure remove");
  }

  [Test]
  public void RemovedResidentFileContentIsZeroedFromImage() {
    // Small payload stays resident inside the MFT record. Zeroing the record
    // must wipe it.
    var marker = System.Text.Encoding.ASCII.GetBytes("RESIDENT_SECRET!");
    var image = BuildImageWith(("small.txt", marker));

    Assert.That(FindMarker(image, marker), Is.True, "precondition: marker should be in image");
    NtfsRemover.Remove(image, "small.txt");
    Assert.That(FindMarker(image, marker), Is.False,
      "resident marker must no longer be recoverable from the image after secure remove");
  }

  [Test]
  public void RemovedFileMftRecordIsZeroed() {
    var image = BuildImageWith(("gone.bin", new byte[100]));
    // Filename is UTF-16-LE encoded inside the MFT record's $FILE_NAME.
    var nameUtf16 = System.Text.Encoding.Unicode.GetBytes("gone.bin");
    Assert.That(FindMarker(image, nameUtf16), Is.True, "precondition: UTF-16 filename should be in image");
    NtfsRemover.Remove(image, "gone.bin");
    Assert.That(FindMarker(image, nameUtf16), Is.False,
      "filename must be fully wiped from the MFT, not just marked unused");
  }

  [Test]
  public void RemovedFileIsNotReadableAfterwards() {
    var image = BuildImageWith(("a.bin", new byte[] { 1, 2, 3 }), ("b.bin", new byte[] { 4, 5, 6 }));
    NtfsRemover.Remove(image, "a.bin");

    using var ms = new MemoryStream(image);
    var reader = new NtfsReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "a.bin"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "b.bin"), Is.True);
  }

  [Test]
  public void SurvivingFileStillReadableAfterRemoval() {
    var keepPayload = new byte[4096];
    new Random(42).NextBytes(keepPayload);
    var image = BuildImageWith(
      ("delete.bin", System.Text.Encoding.ASCII.GetBytes("TO_BE_DELETED_" + new string('X', 100))),
      ("keep.bin", keepPayload));

    NtfsRemover.Remove(image, "delete.bin");

    using var ms = new MemoryStream(image);
    var reader = new NtfsReader(ms);
    var keep = reader.Entries.FirstOrDefault(e => e.Name == "keep.bin");
    Assert.That(keep, Is.Not.Null, "surviving file must still be listable");
    var extracted = reader.Extract(keep!);
    Assert.That(extracted, Is.EqualTo(keepPayload), "surviving file content must be intact");
  }

  [Test]
  public void RemovingNonexistentThrows() {
    var image = BuildImageWith(("a.txt", [1]));
    Assert.Throws<FileNotFoundException>(() => NtfsRemover.Remove(image, "missing.txt"));
  }

  [Test]
  public void RemoveIsCaseInsensitive() {
    var image = BuildImageWith(("Report.Txt", new byte[] { 9, 9, 9 }));
    NtfsRemover.Remove(image, "REPORT.TXT");
    using var ms = new MemoryStream(image);
    var reader = new NtfsReader(ms);
    Assert.That(reader.Entries.Any(e =>
      string.Equals(e.Name, "Report.Txt", StringComparison.OrdinalIgnoreCase)), Is.False);
  }

  [Test]
  public void DescriptorAsModifiable_RemoveWorks() {
    var marker = System.Text.Encoding.ASCII.GetBytes("FORENSICS_BYTES!");
    var buf = BuildImageWith(("secret.txt", marker));
    using var ms = new MemoryStream();
    ms.Write(buf);
    ms.SetLength(buf.Length);

    var desc = new NtfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
    ((IArchiveModifiable)desc).Remove(ms, ["secret.txt"]);

    Assert.That(FindMarker(ms.ToArray(), marker), Is.False);
  }

  [Test]
  public void DescriptorAsModifiable_AddCombinesFiles() {
    var initial = BuildImageWith(("first.txt", new byte[] { 0xAA }));
    using var ms = new MemoryStream();
    ms.Write(initial);
    ms.SetLength(initial.Length);

    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, [0xBB]);
      ((IArchiveModifiable)new NtfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmpFile, "second.txt", false)]);

      ms.Position = 0;
      var reader = new NtfsReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "first.txt"), Is.True);
      Assert.That(reader.Entries.Any(e => e.Name == "second.txt"), Is.True);
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
