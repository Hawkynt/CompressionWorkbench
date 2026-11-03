using System.Reflection;
using System.Security.Cryptography;
using FileFormat.DxArchive;

namespace Compression.Tests.DxArchive;

/// <summary>
/// DxLib archives the archivers DxLib ships made from known files (ReferenceVectors/README.md):
/// version 8 (DxaEncode 1.10) with the default password, a password, no encryption, no
/// compression, whole-file and partial Huffman coding and maximum compression, and version 6
/// (DxaEncode 1.06) with the default password and a password, must each extract to the files.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public sealed class DxArchiveTests {
  private sealed record Row(string Path, long Size, string Sha256);

  private static Stream Open(string name) =>
    Assembly.GetExecutingAssembly().GetManifestResourceStream($"DxArchiveVectors.{name}")
    ?? throw new FileNotFoundException($"Embedded DXA vector '{name}' is missing.");

  private static byte[] Load(string name) {
    using var s = Open(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly Lazy<Row[]> Manifest = new(() => System.Text.Encoding.UTF8.GetString(Load("manifest.tsv"))
    .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t')).Select(f => new Row(f[0], long.Parse(f[1]), f[2])).ToArray());

  private static void AssertHoldsTheFiles(DxArchiveReader reader) {
    var got = reader.Entries.ToDictionary(e => e.Path, e => reader.Extract(e));
    Assert.Multiple(() => {
      Assert.That(got.Keys, Is.EquivalentTo(Manifest.Value.Select(r => r.Path)));
      foreach (var row in Manifest.Value)
        if (got.TryGetValue(row.Path, out var data))
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), row.Path);
    });
  }

  [TestCase("v8-default.dxa", null)]
  [TestCase("v8-key.dxa", "Secret")]
  [TestCase("v8-nokey.dxa", null)]
  [TestCase("v8-stored.dxa", null)]
  [TestCase("v8-huffall.dxa", null)]
  [TestCase("v8-huff1k.dxa", null)]
  [TestCase("v8-maxpress.dxa", null)]
  public void Given_AVersion8Archive_When_Extracted_Then_ItHoldsTheFiles(string name, string? password) {
    var reader = DxArchiveReader.Open(Load(name), password);
    Assert.That(reader.Version, Is.EqualTo(8));
    AssertHoldsTheFiles(reader);
  }

  [TestCase("v6-default.dxa", null)]
  [TestCase("v6-key.dxa", null)]
  [TestCase("v6-key.dxa", "Secret")]
  public void Given_AVersion6Archive_When_Extracted_Then_ItHoldsTheFiles(string name, string? password) {
    var reader = DxArchiveReader.Open(Load(name), password);
    Assert.That(reader.Version, Is.EqualTo(6));
    AssertHoldsTheFiles(reader);
  }

  [Test]
  public void Given_APasswordedVersion8Archive_When_OpenedWithoutIt_Then_TheDescriptorReportsInsteadOfThrowing() {
    Assert.Throws<InvalidDataException>(() => DxArchiveReader.Open(Load("v8-key.dxa")));
    var listed = new DxArchiveFormatDescriptor().List(new MemoryStream(Load("v8-key.dxa")), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dxa", "metadata.ini" }));
    var withKey = new DxArchiveFormatDescriptor().List(new MemoryStream(Load("v8-key.dxa")), "Secret");
    Assert.That(withKey.Select(e => e.Name), Is.EquivalentTo(Manifest.Value.Select(r => r.Path)));
  }

  [Test]
  public void Given_AWrongPassword_When_Opened_Then_ItIsRefused()
    => Assert.Throws<InvalidDataException>(() => DxArchiveReader.Open(Load("v8-default.dxa"), "wrong"));

  [Test]
  public void Given_SomethingElse_When_Opened_Then_ItIsRefused() {
    Assert.Throws<InvalidDataException>(() => DxArchiveReader.Open(new byte[100]));
    byte[] v7 = [0x44, 0x58, 0x07, 0x00, .. new byte[60]];
    Assert.Throws<NotSupportedException>(() => DxArchiveReader.Open(v7));
  }
}
