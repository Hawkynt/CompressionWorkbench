using System.Reflection;
using System.Security.Cryptography;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// 7z archives 7-Zip wrote, from libarchive's test suite (see SevenZip/LibarchiveVectors/README.md):
/// every member decodes to what 7-Zip extracts.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class SevenZipLibarchiveVectorTests {
  private const string Prefix = "SevenZipLibarchiveVectors.";

  private static IEnumerable<TestCaseData> Members() {
    var assembly = Assembly.GetExecutingAssembly();
    foreach (var manifest in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix + "manifest", StringComparison.Ordinal)).Order()) {
      using var reader = new StreamReader(assembly.GetManifestResourceStream(manifest)!);
      for (string? line; (line = reader.ReadLine()) != null;) {
        var columns = line.TrimEnd('\r').Split('\t');
        if (columns.Length == 4)
          yield return new TestCaseData(columns[0], columns[1], long.Parse(columns[2]), columns[3])
            .SetName($"Given_{columns[0]}_When_{columns[1]}IsExtracted_Then_ItMatches7Zip");
      }
    }
  }

  [TestCaseSource(nameof(Members))]
  public void Given_A7ZipArchive_When_AMemberIsExtracted_Then_ItMatches7Zip(string archive, string member, long size, string sha256) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + archive)!;
    using var reader = new SevenZipReader(stream);
    var index = reader.Entries.ToList().FindIndex(e => e.Name.Replace('\\', '/') == member);
    Assert.That(index, Is.GreaterThanOrEqualTo(0), $"{member} not listed");

    var data = reader.Extract(index);

    Assert.That(data.LongLength, Is.EqualTo(size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
  }
}
