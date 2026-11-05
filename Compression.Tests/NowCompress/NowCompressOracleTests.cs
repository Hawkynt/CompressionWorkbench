using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileFormat.NowCompress;

namespace Compression.Tests.NowCompress;

[TestFixture]
public class NowCompressOracleTests {
  private static byte[] Vector(string name) {
    using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream($"NowCompressVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Now Compress vector '{name}' is missing.");
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_AllHighMacRomanFilenameBytes_When_Read_Then_TheirMappingIsPreserved() {
    using var input = new MemoryStream(Vector("macroman-names.now"));
    var entries = NowCompressArchive.Read(input);
    var expected = JsonSerializer.Deserialize<string[]>(Vector("macroman-names.json"))!;

    Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(expected));
    Assert.That(entries.All(entry => entry.IsDirectory), Is.True);
  }

  private static IEnumerable<TestCaseData> CapturedCases() {
    using var facts = JsonDocument.Parse(Vector("native-oracle-facts.json"));
    foreach (var sample in facts.RootElement.GetProperty("samples").EnumerateArray())
      yield return new TestCaseData(
        sample.GetProperty("archiveSha256").GetString()!,
        sample.GetProperty("archiveSize").GetInt64(),
        sample.GetProperty("fileCount").GetInt32(),
        sample.GetProperty("forkPairsSha256").GetString()!)
        .SetName($"NowCompressCapturedOracle_{sample.GetProperty("fileName").GetString()}");
  }

  [TestCaseSource(nameof(CapturedCases)), Category("ThirdPartyVector")]
  public void Given_ACapturedArchive_When_Read_Then_AllForkPairsMatchTheNativeDecoder(
    string archiveSha256, long archiveSize, int fileCount, string forkPairsSha256) {
    var folder = Environment.GetEnvironmentVariable("CWB_NOWCOMPRESS_SAMPLES");
    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
      Assert.Ignore("Set CWB_NOWCOMPRESS_SAMPLES to the pinned public samples; see ReferenceVectors/README.md.");
    var path = Directory.EnumerateFiles(folder!)
      .FirstOrDefault(file => new FileInfo(file).Length == archiveSize && Sha(File.ReadAllBytes(file)) == archiveSha256);
    if (path is null)
      Assert.Ignore($"Captured sample {archiveSha256} is not present.");
    var bytes = File.ReadAllBytes(path!);
    var offset = NowCompressArchive.IsNowCompress(bytes) ? 0 : 128;
    using var input = new MemoryStream(bytes, offset, bytes.Length - offset);
    var files = NowCompressArchive.Read(input).Where(entry => !entry.IsDirectory).ToArray();
    var pairs = files.Select(entry => FormattableString.Invariant(
        $"{entry.Data.Length}:{Sha(entry.Data)}:{entry.Resource.Length}:{Sha(entry.Resource)}"))
      .Order(StringComparer.Ordinal);
    var fingerprint = Sha(Encoding.ASCII.GetBytes(string.Join("\n", pairs)));

    Assert.That(files, Has.Length.EqualTo(fileCount));
    Assert.That(fingerprint, Is.EqualTo(forkPairsSha256));
  }

  private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
