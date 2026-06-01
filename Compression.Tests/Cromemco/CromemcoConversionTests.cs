#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Cromemco;

namespace Compression.Tests.Cromemco;

/// <summary>
/// End-to-end conversion via
/// <see cref="Compression.Lib.ArchiveOperations.ConvertArchive(string, string, string?, Compression.Registry.FormatCreateOptions?)"/>:
/// build a Cromemco RDOS image, convert it to TAR, then convert the TAR
/// back to RDOS — both files must contain the original byte contents.
/// </summary>
[TestFixture]
public class CromemcoConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_CromemcoToTar_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "cromemco_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      // Build a Cromemco image with two files.
      var fileA = "alpha file content"u8.ToArray();
      var fileB = new byte[200];
      for (var i = 0; i < fileB.Length; i++) fileB[i] = (byte)(i & 0xFF);

      var w = new CromemcoWriter();
      w.SetGeometry(77, 26);
      w.AddFile("A.TXT", fileA);
      w.AddFile("B.BIN", fileB);
      var img = w.Build();

      var rdosPath = Path.Combine(dir, "src.rdos");
      File.WriteAllBytes(rdosPath, img);

      // Convert .rdos -> .tar.
      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(rdosPath, tarPath, "Tar");
      Assert.That(File.Exists(tarPath), Is.True);
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      // Extract the TAR back to disk via ConvertArchive into a fresh RDOS.
      var rdosBackPath = Path.Combine(dir, "back.rdos");
      ArchiveOperations.ConvertArchive(tarPath, rdosBackPath, "Cromemco");
      Assert.That(File.Exists(rdosBackPath), Is.True);

      using var fs = File.OpenRead(rdosBackPath);
      using var r = new CromemcoReader(fs);
      var names = r.Entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("A.TXT"));
      Assert.That(names, Does.Contain("B.BIN"));

      var entryA = r.Entries.Single(e => e.Name == "A.TXT");
      var entryB = r.Entries.Single(e => e.Name == "B.BIN");
      var extractedA = r.Extract(entryA);
      var extractedB = r.Extract(entryB);
      // Reader pads to sector boundaries; compare the leading bytes.
      Assert.That(extractedA.AsSpan(0, fileA.Length).ToArray(), Is.EqualTo(fileA),
        "Round-tripped A.TXT must match original byte-for-byte.");
      Assert.That(extractedB.AsSpan(0, fileB.Length).ToArray(), Is.EqualTo(fileB),
        "Round-tripped B.BIN must match original byte-for-byte.");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
