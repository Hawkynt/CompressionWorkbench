#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Human68k;

namespace Compression.Tests.Human68k;

/// <summary>
/// End-to-end conversion via
/// <see cref="ArchiveOperations.ConvertArchive(string, string, string?, Compression.Registry.FormatCreateOptions?)"/>:
/// build a Human68k image, convert to TAR, convert back, verify
/// byte-identical contents.
/// </summary>
[TestFixture]
public class Human68kConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_Human68kToTar_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "human68k_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var fileA = "alpha content"u8.ToArray();
      var fileB = new byte[200];
      for (var i = 0; i < fileB.Length; i++) fileB[i] = (byte)(i & 0xFF);

      var w = new Human68kWriter();
      w.SetBytesPerSector(512);
      w.SetSectorsPerCluster(1);
      w.AddFile("A.TXT", fileA);
      w.AddFile("B.BIN", fileB);
      var img = w.Build();

      // Use .2hd extension — .hdf collides with HDF4 and .dim with Gemdos.
      // .2hd is the X68000 HD floppy image extension and uniquely identifies
      // Human68k in our registry.
      var srcPath = Path.Combine(dir, "src.2hd");
      File.WriteAllBytes(srcPath, img);

      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(srcPath, tarPath, "Tar");
      Assert.That(File.Exists(tarPath), Is.True);
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var backPath = Path.Combine(dir, "back.2hd");
      ArchiveOperations.ConvertArchive(tarPath, backPath, "Human68k");
      Assert.That(File.Exists(backPath), Is.True);

      using var fs = File.OpenRead(backPath);
      using var r = new Human68kReader(fs);
      var names = r.Entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("A.TXT"));
      Assert.That(names, Does.Contain("B.BIN"));

      var entryA = r.Entries.Single(e => e.Name == "A.TXT");
      var entryB = r.Entries.Single(e => e.Name == "B.BIN");
      var extractedA = r.Extract(entryA);
      var extractedB = r.Extract(entryB);
      Assert.That(extractedA.AsSpan(0, fileA.Length).ToArray(), Is.EqualTo(fileA),
        "A.TXT must round-trip byte-for-byte.");
      Assert.That(extractedB.AsSpan(0, fileB.Length).ToArray(), Is.EqualTo(fileB),
        "B.BIN must round-trip byte-for-byte.");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
