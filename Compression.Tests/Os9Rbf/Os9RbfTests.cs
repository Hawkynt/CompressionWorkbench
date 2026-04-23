using System.Text;
using Compression.Registry;
using FileSystem.Os9Rbf;

namespace Compression.Tests.Os9Rbf;

[TestFixture]
public class Os9RbfTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_SingleFile_ByteExactRoundTrip() {
    var body = Encoding.ASCII.GetBytes("Hello OS-9 World!");
    var image = Os9RbfWriter.Build([("hello.txt", body)], "TESTVOL");
    // 35 tracks × 18 sectors × 2 sides × 256 bytes = 322 560 bytes (315 KB).
    Assert.That(image.Length, Is.EqualTo(322_560));

    var v = Os9RbfReader.Read(image);
    Assert.That(v.VolumeName, Is.EqualTo("TESTVOL"));
    Assert.That(v.Files, Has.Count.EqualTo(1));
    Assert.That(v.Files[0].Name, Is.EqualTo("hello.txt"));

    var extracted = Os9RbfReader.Extract(v, v.Files[0]);
    Assert.That(extracted, Is.EqualTo(body).AsCollection);
    // FD.SIZ must equal the original byte length (no sector padding leaks).
    Assert.That(extracted.Length, Is.EqualTo(body.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_MultiFile_RoundTrips() {
    var files = new (string, byte[])[] {
      ("alpha.txt", Encoding.ASCII.GetBytes("first")),
      ("beta.bin", Enumerable.Range(0, 600).Select(i => (byte)(i & 0xFF)).ToArray()),
      ("gamma", new byte[1]),
    };
    var image = Os9RbfWriter.Build(files);
    var v = Os9RbfReader.Read(image);

    Assert.That(v.Files.Where(f => !f.IsDirectory).Select(f => f.Name),
      Is.EquivalentTo(new[] { "alpha.txt", "beta.bin", "gamma" }));

    foreach (var src in files) {
      var entry = v.Files.Single(f => f.Name == src.Item1);
      var got = Os9RbfReader.Extract(v, entry);
      Assert.That(got, Is.EqualTo(src.Item2).AsCollection, $"file '{src.Item1}' did not round-trip");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var tmp1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName().Replace(".", "") + ".dat");
    var tmp2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName().Replace(".", "") + ".bin");
    File.WriteAllText(tmp1, "alpha-content");
    File.WriteAllText(tmp2, "beta-content");
    try {
      var desc = new Os9RbfFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [
        new ArchiveInputInfo(tmp1, "alpha.dat", false),
        new ArchiveInputInfo(tmp2, "beta.bin", false),
      ], new FormatCreateOptions());
      ms.Position = 0;
      var listed = desc.List(ms, null);
      Assert.That(listed.Where(e => !e.IsDirectory).Select(e => e.Name),
        Is.EquivalentTo(new[] { "alpha.dat", "beta.bin" }));
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsLongFilenames() {
    var desc = new Os9RbfFormatDescriptor();
    var longName = new string('a', 32);
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/x", longName, false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("28"));
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsDirectories() {
    var desc = new Os9RbfFormatDescriptor();
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/x", "subdir", true), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("subdirectories"));
  }

  [Test, Category("EdgeCase")]
  public void Read_UndersizedImage_Throws() {
    Assert.That(() => Os9RbfReader.Read(new byte[100]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_HeaderClaimsTooLarge_Throws() {
    // Build a valid image, then chop it in half so the DD.TOT field exceeds the
    // image length.
    var image = Os9RbfWriter.Build([("a.x", new byte[10])]);
    var half = image.AsSpan(0, image.Length / 2).ToArray();
    Assert.That(() => Os9RbfReader.Read(half), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Build_FillsBitmapAndIdentificationFields() {
    var image = Os9RbfWriter.Build([("test.txt", new byte[5])]);
    // DD.TOT (offset 0, u24 BE) should equal TotalSectors (1260).
    var totalSec = (image[0] << 16) | (image[1] << 8) | image[2];
    Assert.That(totalSec, Is.EqualTo(1260));
    // DD.BIT cluster size = 1.
    Assert.That((image[6] << 8) | image[7], Is.EqualTo(1));
    // Allocation bitmap byte 0 should have bit 7 set (LSN 0 always allocated).
    Assert.That(image[256] & 0x80, Is.Not.Zero);
  }
}
