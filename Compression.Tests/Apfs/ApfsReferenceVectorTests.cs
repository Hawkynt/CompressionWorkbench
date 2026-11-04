using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// keramics' APFS volume, written by macOS itself (see <c>ReferenceVectors/README.md</c>):
/// transparently compressed files and a multi-extent file read back to the bytes the
/// source files held.
/// </summary>
/// <remarks>
/// The reader used to keep one FILE_EXTENT per inode (the last one it met) and knew
/// nothing of decmpfs: a compressed file listed as empty and extracted as nothing.
/// </remarks>
[TestFixture]
public sealed class ApfsReferenceVectorTests {

  private static ApfsReader Open() {
    using var resource = typeof(ApfsReferenceVectorTests).Assembly.GetManifestResourceStream("ApfsVectors.apfs.raw.gz")
      ?? throw new FileNotFoundException("embedded reference vector apfs.raw.gz is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return new ApfsReader(image);
  }

  private const string ShortSha = "a78f2707f267bc43180b87ad4f4834996e9adb55d03e6cb84b888c58ecc1cc5f";
  private const string LicenseSha = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30";

  [TestCase("testdir1/compressed1", 19, ShortSha, Description = "decmpfs 3: zlib inline")]
  [TestCase("testdir1/compressed2", 11358, LicenseSha, Description = "decmpfs 4: zlib in the resource fork")]
  [TestCase("testdir1/compressed3", 19, ShortSha, Description = "decmpfs 7: LZVN inline")]
  [TestCase("testdir1/compressed4", 11358, LicenseSha, Description = "decmpfs 8: LZVN in the resource fork")]
  [TestCase("testdir1/compressed5", 19, ShortSha, Description = "decmpfs 11: LZFSE inline")]
  [TestCase("testdir1/compressed6", 11358, LicenseSha, Description = "decmpfs 12: LZFSE in the resource fork")]
  [TestCase("testdir1/TestFile2", 11358, LicenseSha, Description = "the same text, uncompressed")]
  [Category("HappyPath")]
  public void GivenAMacOsWrittenVolume_WhenACompressedFileIsExtracted_ThenItMatchesItsSource(string path, long size, string sha) {
    using var reader = Open();
    var entry = reader.Entries.SingleOrDefault(e => e.Name == path)!;
    Assert.That(entry, Is.Not.Null, "listed: " + string.Join(", ", reader.Entries.Select(e => e.Name)));
    Assert.That(entry.Size, Is.EqualTo(size), "a compressed file lists its uncompressed length");
    var bytes = reader.Extract(entry);
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(sha));
    using var streamed = new MemoryStream();
    reader.ExtractTo(entry, streamed);
    Assert.That(streamed.ToArray(), Is.EqualTo(bytes));
  }

  [Test, Category("HappyPath")]
  public void GivenAMacOsWrittenVolume_WhenEveryFileIsExtracted_ThenEachHasItsListedLength() {
    using var reader = Open();
    Assert.Multiple(() => {
      foreach (var entry in reader.Entries.Where(e => !e.IsDirectory && !e.IsSymlink))
        Assert.That(reader.Extract(entry).LongLength, Is.EqualTo(entry.Size), entry.Name);
    });
  }
}
