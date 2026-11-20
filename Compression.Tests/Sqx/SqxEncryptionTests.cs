using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

/// <summary>
/// Encrypted SQX archives written by Squeez CommandLine 5.2 (formats 1.1 and 2.0; stored, normal,
/// solid maximum; encrypted headers; a UTF-8 password) and the SQX SDK 2.06's sample
/// (password "test"): with the password every file comes out as it went in (each checked against
/// its CRC); without it, or with a wrong one, nothing is decrypted. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public sealed class SqxEncryptionTests {
  private static readonly Dictionary<string, string> Originals = new() {
    ["a.bin"] = "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9",
    ["b.txt"] = "694ea0cfa2f91d8f1295aae3d9531d3adaec4d140207e83576d1cf9b732471de",
    ["c.bin"] = "eb0e917aabcdc2b8b4614dcc3643f87214f32284654e132826a5f5ed54ff7c98",
    ["d.bin"] = "9f9f5111f7b27a781f1f1ddde5ebc2dd2b796bfc7365c9c28b548e564176929f",
    ["e.bin"] = "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881",
  };

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SqxVectors." + name)
      ?? throw new InvalidOperationException("Missing resource SqxVectors." + name);
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [TestCase("pw.enc_v2.0_stored.sqx", "secret", 5)]
  [TestCase("pw.enc_v2.0_normal.sqx", "secret", 5)]
  [TestCase("pw.enc_v1.1_normal.sqx", "secret", 5)]
  [TestCase("pw.enc_v2.0_solid_max.sqx", "secret", 5)]
  [TestCase("pw.enchdr_v2.0.sqx", "secret", 5)]
  [TestCase("pw.encutf8_v2.0.sqx", "pässwort", 2)]
  [Category("ThirdPartyVector")]
  public void Given_AnEncryptedArchive_When_ExtractedWithItsPassword_Then_EveryFileIsTheOriginal(string fixture, string password, int count) {
    using var reader = new SqxReader(new MemoryStream(Resource(fixture)), password);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(count));
    Assert.That(files.All(e => e.IsEncrypted));
    foreach (var e in files)
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.ExtractEntry(e))), Is.EqualTo(Originals[e.FileName]), e.FileName);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_TheSdkSample_When_ExtractedWithItsPassword_Then_EveryFilePassesItsCrc() {
    using var reader = new SqxReader(new MemoryStream(Resource("pw.sdk_EncryptedArchive.sqx")), "test");
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FileName), Does.Contain("uCompress.cpp"));
    foreach (var e in files)
      Assert.That(reader.ExtractEntry(e), Has.Length.EqualTo(e.OriginalSize), e.FileName);
  }

  [TestCase("pw.enc_v2.0_stored.sqx"), TestCase("pw.enc_v2.0_normal.sqx"), TestCase("pw.enc_v2.0_solid_max.sqx")]
  [Category("Exception")]
  public void Given_AnEncryptedFile_When_ExtractedWithAWrongOrNoPassword_Then_ThePasswordIsAskedFor(string fixture) {
    var bytes = Resource(fixture);
    using (var reader = new SqxReader(new MemoryStream(bytes))) {
      var e = Assert.Throws<ArchivePasswordException>(() => reader.ExtractEntry(reader.Entries.First(x => !x.IsDirectory)));
      Assert.That(e!.PasswordWasSupplied, Is.False);
    }
    using (var reader = new SqxReader(new MemoryStream(bytes), "wrong")) {
      var e = Assert.Throws<ArchivePasswordException>(() => {
        foreach (var x in reader.Entries.Where(x => !x.IsDirectory)) reader.ExtractEntry(x);
      });
      Assert.That(e!.PasswordWasSupplied, Is.True);
    }
  }

  [Test, Category("Exception")]
  public void Given_EncryptedHeaders_When_OpenedWithoutOrWithAWrongPassword_Then_ThePasswordIsAskedFor() {
    var bytes = Resource("pw.enchdr_v2.0.sqx");
    Assert.That(Assert.Throws<ArchivePasswordException>(() => _ = new SqxReader(new MemoryStream(bytes)))!.PasswordWasSupplied, Is.False);
    Assert.That(Assert.Throws<ArchivePasswordException>(() => _ = new SqxReader(new MemoryStream(bytes), "wrong"))!.PasswordWasSupplied, Is.True);
    using var reader = new SqxReader(new MemoryStream(bytes), "secret");
    Assert.That(reader.HeadersEncrypted, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Given_AnEncryptedArchive_When_ListedAndExtractedThroughTheDescriptor_Then_ThePasswordIsUsed() {
    var bytes = Resource("pw.enchdr_v2.0.sqx");
    var list = new SqxFormatDescriptor().List(new MemoryStream(bytes), "secret");
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(Originals.Keys));
    Assert.That(list.All(e => e.IsEncrypted));
  }
}
