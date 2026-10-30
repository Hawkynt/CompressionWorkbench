using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP method 14 (LZMA) against third parties: libarchive's archives, with and without the end
/// marker, decode as 7-Zip decodes them, and 7-Zip extracts the LZMA members we write.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class ZipLzmaReferenceVectorTests {
  [TestCase("test_read_format_zip_lzma.zipx", "vimrc", 912, "b16e85e457397ab2043a7ee0a3c84307c6b4eac157fd0b721694761f25b3ed5b")]
  [TestCase("test_read_format_zip_lzma_multi.zipx", "smartd.conf", 6699, "f0539b3e232908d24e41a4d852410a988adef8c77dd4b0215bc2a2122abd63e1")]
  [TestCase("test_read_format_zip_lzma_multi.zipx", "ts.conf", 852, "7f808972c5918da45470f50c0350808950e87123fd0804f5815dbd90eb7149b9")]
  [TestCase("test_read_format_zip_lzma_multi.zipx", "vimrc", 912, "b16e85e457397ab2043a7ee0a3c84307c6b4eac157fd0b721694761f25b3ed5b")]
  [TestCase("test_read_format_zip_lzma_stream_end.zipx", "vimrc", 912, "b16e85e457397ab2043a7ee0a3c84307c6b4eac157fd0b721694761f25b3ed5b")]
  public void Given_ALibarchiveLzmaZip_When_AMemberIsExtracted_Then_ItMatches7Zip(string archive, string member, int size, string sha256) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipLibarchiveVectors." + archive)!;
    using var reader = new ZipReader(stream);
    var entry = reader.Entries.Single(e => e.FileName == member);
    Assert.That(entry.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Lzma));

    var data = reader.ExtractEntry(entry);

    Assert.That(data, Has.Length.EqualTo(size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
  }

  [TestCase(100_000)]
  [TestCase(5_000)]
  [Category("ExternalInterop")]
  public void Given_OurLzmaMembers_When_7ZipExtractsThem_Then_TheBytesMatch(int size) {
    FsInteropToolbox.Require7z();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-zip-lzma-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var data = new byte[size];
      for (var i = 0; i < size; ++i)
        data[i] = (byte)(i % 251 < 120 ? 'a' + i % 7 : i * 13);

      var archive = Path.Combine(dir, "ours.zip");
      using (var file = File.Create(archive))
      using (var writer = new ZipWriter(file)) {
        writer.AddEntry("auto.bin", data, ZipCompressionMethod.Lzma);
        writer.Finish();
      }

      var outDir = Path.Combine(dir, "out");
      var (stdout, stderr, code) = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{archive}\"");
      Assert.That(code, Is.Zero, stdout + stderr);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "auto.bin")), Is.EqualTo(data));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
