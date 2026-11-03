using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Windows Overlay Filter ("system compression") files on an NTFS volume, compared with
/// what the Linux kernel's ntfs3 driver read back from the same volume (see
/// <c>ReferenceVectors/README.md</c>).
/// </summary>
/// <remarks>
/// The reader used to return the sparse unnamed <c>$DATA</c> of such a file — zeros of
/// the right length — instead of decoding <c>WofCompressedData</c>.
/// </remarks>
[TestFixture]
public sealed class NtfsWofReferenceTests {

  private static MemoryStream Image() {
    using var resource = typeof(NtfsWofReferenceTests).Assembly.GetManifestResourceStream("NtfsVectors.ntfs-wof.img.gz")
      ?? throw new FileNotFoundException("embedded reference vector ntfs-wof.img.gz is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, (string Sha256, long Size)> Manifest() {
    using var resource = typeof(NtfsWofReferenceTests).Assembly.GetManifestResourceStream("NtfsVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line) {
      var parts = line.Split(' ', 3);
      if (parts.Length == 3 && parts[0].Length == 64)
        files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
    return files;
  }

  [TestCase("plain.bin", Description = "the uncompressed source, for reference")]
  [TestCase("xpress4k.bin")]
  [TestCase("xpress8k.bin")]
  [TestCase("xpress16k.bin")]
  [TestCase("lzx.bin")]
  [Category("HappyPath")]
  public void GivenAWofCompressedFile_WhenExtracted_ThenItMatchesWhatNtfs3Read(string name) {
    var expected = Manifest()[name];
    using var image = Image();
    using var reader = new NtfsReader(image);
    var entry = reader.Entries.Single(e => e.Name == name);
    var bytes = reader.Extract(entry);
    Assert.That((Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength), Is.EqualTo(expected));
  }

  [Test, Category("Exception")]
  public void GivenAWimBackedReparsePoint_WhenParsed_ThenItIsRefused() {
    var data = new byte[16];
    BitConverter.TryWriteBytes(data.AsSpan(0), 1u); // WOF_EXTERNAL_INFO.Version
    BitConverter.TryWriteBytes(data.AsSpan(4), 1u); // WOF_PROVIDER_WIM
    var (algorithm, refusal) = NtfsWof.ParseReparseData(data);
    Assert.Multiple(() => {
      Assert.That(algorithm, Is.Null);
      Assert.That(refusal, Does.Contain("WIM"));
    });
  }

  [Test, Category("Exception")]
  public void GivenAnUnknownAlgorithm_WhenParsed_ThenItIsRefused() {
    var data = new byte[16];
    BitConverter.TryWriteBytes(data.AsSpan(0), 1u);
    BitConverter.TryWriteBytes(data.AsSpan(4), 2u); // WOF_PROVIDER_FILE
    BitConverter.TryWriteBytes(data.AsSpan(8), 1u);
    BitConverter.TryWriteBytes(data.AsSpan(12), 9u);
    Assert.That(NtfsWof.ParseReparseData(data).Algorithm, Is.Null);
  }

  [Test, Category("Exception")]
  public void GivenAChunkTableLongerThanTheStream_WhenDecoded_ThenItIsRejected() =>
    Assert.Throws<InvalidDataException>(() => NtfsWof.Decode(new byte[8], 3 * 4096, 0));

  [Test, Category("Boundary")]
  public void GivenStoredChunks_WhenDecoded_ThenTheyAreCopiedVerbatim() {
    // Two 4 KiB chunks stored raw: a one-entry table (offset of chunk 1) then the bytes.
    var payload = new byte[4 + 8192];
    BitConverter.TryWriteBytes(payload.AsSpan(0), 4096u);
    for (var i = 0; i < 8192; i++) payload[4 + i] = (byte)(i * 31);
    Assert.That(NtfsWof.Decode(payload, 8192, 0), Is.EqualTo(payload[4..]));
  }
}
