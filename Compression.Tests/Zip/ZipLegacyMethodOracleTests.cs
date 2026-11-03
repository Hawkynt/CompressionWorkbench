using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Zip;
using Compression.Tests.CodecProof;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP Shrink (1), Reduce (2-5) and Implode (6) against third parties: streams PKZIP 1.10 and 0.92
/// wrote, archives hwzip 2.4 wrote, and 7-Zip extracting what our encoders write. See
/// Zip/LegacyMethodVectors/README.md.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class ZipLegacyMethodOracleTests {
  [TestCase("pkzip110-implode-4k-nolit-hamlet256.bin", 256, false, false, "caa3e718b66ef0d475691ae0458a903fe9d6111b53833aac4537f7fc273e5986")]
  [TestCase("pkzip110-implode-8k-lit-hamlet8192.bin", 8192, true, true, "a7ffff49853f9cf9b43cef32305df8e2d4d58fa84f6d9a648c514fc0a6c1424b")]
  public void Given_APkzipImplodeStream_When_Exploded_Then_HamletComesBack(string name, int size, bool literalTree, bool window8k, string sha256)
    => Assert.That(Sha(ImplodeDecoder.Decode(Resource(name), size, literalTree, window8k)), Is.EqualTo(sha256));

  [TestCase("pkzip092-reduce4-hamlet2048.bin", 2048, "fa7f75285d6a4fe7ce8bfd6d57af2c4a6be91e506815a8ccc782d7a1133935e0")]
  [TestCase("pkzip092-reduce4-hamlet2048-zeros1024.bin", 3072, "2ab6c29876a733d1de9641cc6d04fa52e2e954175a5447fa58e545dbb0b2e2a4")]
  public void Given_APkzipReduceStream_When_Expanded_Then_TheInputComesBack(string name, int size, string sha256)
    => Assert.That(Sha(ReduceDecoder.Decode(Resource(name), size, 4)), Is.EqualTo(sha256));

  [Test]
  public void Given_APkzipShrinkStream_When_Unshrunk_Then_TheInputComesBack()
    => Assert.That(ShrinkDecoder.Decode(Resource("pkzip110-shrink-lzwfig5.bin"), 17), Is.EqualTo("ababcbababaaaaaaa"u8.ToArray()));

  [TestCase("hwzip-shrink.zip")]
  [TestCase("hwzip-reduce.zip")]
  [TestCase("hwzip-implode.zip")]
  public void Given_AnHwzipArchive_When_Extracted_Then_EveryMemberMatches(string archive) {
    using var reader = new ZipReader(new MemoryStream(Resource(archive)));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "text.txt")), Is.EqualTo(CodecProofData.Text(60000)));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "mixed.bin")), Is.EqualTo(CodecProofData.Mixed(30000)));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "zeros.bin")), Is.EqualTo(new byte[20000]));
  }

  [TestCase(ZipCompressionMethod.Shrink)]
  [TestCase(ZipCompressionMethod.Implode)]
  [Category("ExternalInterop")]
  public void Given_OurEncoder_When_7ZipExtracts_Then_TheBytesMatch(ZipCompressionMethod method) {
    FsInteropToolbox.Require7z();
    (string Name, byte[] Data)[] payload = [
      ("text.txt", CodecProofData.Text(300_000)),
      ("mixed.bin", CodecProofData.Mixed(120_000)),
      ("zeros.bin", new byte[70_000]),
      ("short.txt", "abababababababab"u8.ToArray()),
    ];
    var dir = Path.Combine(Path.GetTempPath(), "cwb-zip-legacy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var archive = Path.Combine(dir, "ours.zip");
      using (var file = File.Create(archive))
      using (var writer = new ZipWriter(file)) {
        foreach (var (name, data) in payload)
          writer.AddEntry(name, data, method);
        writer.Finish();
      }

      var outDir = Path.Combine(dir, "out");
      var (stdout, stderr, code) = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{archive}\"");
      Assert.That(code, Is.Zero, stdout + stderr);
      foreach (var (name, data) in payload)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(data), name);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipLegacyMethodVectors." + name)
                       ?? throw new AssertionException("missing resource " + name);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
