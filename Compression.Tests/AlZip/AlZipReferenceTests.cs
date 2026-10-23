using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.AlZip;

namespace Compression.Tests.AlZip;

/// <summary>
/// ALZip against unalz (alkegi/unalz, MIT). Fixtures: unalz's CC0 test archives (written by ALZip;
/// stored, deflate, ALZ-bzip2, ZipCrypto, CP949 names, directories) and archives our writer produced;
/// <c>unalz-listing.txt</c> is unalz's extraction of each. Every member is also checked against the
/// CRC-32 ALZip stored for it. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class AlZipReferenceTests {

  private const string Password = "test1234";

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AlZipVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ALZip vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<string[]> Listing(string vector)
    => Encoding.UTF8.GetString(Vector("unalz-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .Where(p => p[0] == vector)
      .ToList();

  internal static readonly DateTime FixedTime = new(2024, 5, 6, 7, 8, 10);
  internal static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"ALZip line {i % 17}\r\n")));
  internal static readonly byte[] Noise = [.. Enumerable.Range(0, 3000).Select(i => (byte)((i * 2654435761u) >> 24))];

  internal static byte[] OursArchive(string name) {
    var (store, password) = name switch {
      "ours-deflate.alz" => (false, (string?)null),
      "ours-store.alz" => (true, null),
      "ours-encrypted.alz" => (false, Password),
      _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
    using var ms = new MemoryStream();
    using (var w = new AlZipWriter(ms, leaveOpen: true, password: password, store: store)) {
      w.AddDirectory("docs", FixedTime);
      w.AddFile("docs/text.txt", Text, FixedTime);
      w.AddFile("noise.bin", Noise, FixedTime);
      w.AddFile("empty.txt", [], FixedTime);
      w.AddFile("한글/이름.txt", Text[..100], FixedTime);
    }
    return ms.ToArray();
  }

  [TestCase("bzip2.alz")]
  [TestCase("bzip2_dlz.alz")]
  [TestCase("deflate_pure.alz")]
  [TestCase("deflate_empty.alz")]
  [TestCase("dir_tree.alz")]
  [TestCase("store_old.alz")]
  [TestCase("store_empty.alz")]
  [TestCase("enc_deflate.alz")]
  [TestCase("enc_store.alz")]
  [TestCase("zip20.alz")]
  [TestCase("ours-deflate.alz")]
  [TestCase("ours-store.alz")]
  [TestCase("ours-encrypted.alz")]
  [Category("ThirdPartyVector")]
  public void Given_Archive_When_Extracted_Then_MatchesUnalz(string vector) {
    var expected = Listing(vector).ToDictionary(p => p[1], p => (Size: long.Parse(p[2]), Sha: p[3]));
    using var reader = new AlZipReader(new MemoryStream(Vector(vector)));
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FileName), Is.EquivalentTo(expected.Keys));
    foreach (var e in files) {
      var data = reader.Extract(e, Password);
      Assert.That(data.LongLength, Is.EqualTo(expected[e.FileName].Size), e.FileName);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(expected[e.FileName].Sha), e.FileName);
    }
  }

  [TestCase("ours-deflate.alz")]
  [TestCase("ours-store.alz")]
  [Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_ByteIdenticalToWhatUnalzRead(string vector)
    => Assert.That(OursArchive(vector), Is.EqualTo(Vector(vector)));

  [Test, Category("ThirdPartyVector")]
  public void Given_Bzip2Archive_When_Listed_Then_MethodIsBzip2() {
    using var reader = new AlZipReader(new MemoryStream(Vector("bzip2.alz")));
    Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Method).Distinct(), Is.EqualTo(new[] { 1 }));
  }

  [Test, Category("EdgeCase")]
  public void Given_EncryptedMember_When_WrongOrNoPassword_Then_Refused() {
    using var reader = new AlZipReader(new MemoryStream(Vector("enc_deflate.alz")));
    var e = reader.Entries.First(x => !x.IsDirectory && x.OriginalSize > 0);
    Assert.Throws<InvalidOperationException>(() => reader.Extract(e));
    Assert.Throws<InvalidDataException>(() => reader.Extract(e, "wrong-password"));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptMember_When_Extracted_Then_CrcRefuses() {
    var bytes = Vector("store_old.alz");
    using var probe = new AlZipReader(new MemoryStream(bytes));
    var e = probe.Entries.First(x => !x.IsDirectory && x.OriginalSize > 0);
    bytes[(int)e.DataOffset] ^= 0x20;
    using var reader = new AlZipReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries.First(x => x.FileName == e.FileName)));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotAlz_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new AlZipReader(new MemoryStream("PK\x03\x04not alz"u8.ToArray())));

  [Test, Category("EdgeCase")]
  public void Given_UnknownSignature_When_Read_Then_InvalidData() {
    var bytes = Vector("deflate_pure.alz");
    bytes[8] = (byte)'X'; // first local header signature
    Assert.Throws<InvalidDataException>(() => _ = new AlZipReader(new MemoryStream(bytes)));
  }
}
