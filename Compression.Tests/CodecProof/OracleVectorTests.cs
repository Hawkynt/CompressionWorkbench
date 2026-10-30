using System.Reflection;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.CodecProof;

/// <summary>
/// Streams written by third-party encoders: brotli, lz4, libzstd, snap, ncompress, zlib,
/// infback9, libbzip2, liblzma, LZO, pithy, ZX0, salvador, apultra, UCL, blzpack, lzg and
/// lizard. Each must decode, through the building block or format registered under that id,
/// to the sample it was made from. The ledger <c>docs/CODEC-PROOF-AUDIT.md</c> names the
/// producer of every file; <c>CodecProof/OracleVectors/README.md</c> says how they were made.
/// </summary>
[TestFixture]
public sealed class OracleVectorTests {

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OracleVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource OracleVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  public static IEnumerable<TestCaseData> Vectors() {
    var manifest = System.Text.Encoding.UTF8.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], fields[1], fields[2])
        .SetName($"GivenTheStream_{fields[0]}_WhenDecoded_ThenItIsTheSample");
    }
  }

  [TestCaseSource(nameof(Vectors)), Category("HappyPath")]
  public void OracleStreamDecodes(string file, string id, string sample) {
    FormatRegistration.EnsureInitialized();
    var stream = Resource(file);
    var expected = Resource($"sample-{sample}.bin");
    byte[] decoded;
    if (id.StartsWith("fmt.", StringComparison.Ordinal)) {
      var ops = FormatRegistry.GetById(id[4..]) as IStreamFormatOperations
        ?? throw new InvalidOperationException($"No stream format {id[4..]}");
      using var output = new MemoryStream();
      ops.Decompress(new MemoryStream(stream), output);
      decoded = output.ToArray();
    } else {
      var block = BuildingBlockRegistry.GetById(id) ?? throw new InvalidOperationException($"No building block {id}");
      decoded = block.Decompress(stream);
    }
    Assert.That(decoded, Is.EqualTo(expected));
  }
}
