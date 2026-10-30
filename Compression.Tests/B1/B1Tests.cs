using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.B1;

namespace Compression.Tests.B1;

/// <summary>
/// B1 archives against b1-pack (the format's Apache-2.0 reference implementation): archives its CLI
/// wrote read here, and ours read there. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class B1Tests {
  private const string NotesSha = "db617d87bfdd8874584d5a7a4b3fa8cb740660e177a8c558d423d17aa119a790";
  private const string TinySha = "8950abfda7b727630760dd35bcf5c3daa7631aff223a90f7728c0d2521dde10c";
  private const string NoiseSha = "3777f85afaf9aa256f15fc76002ac271f8ecee69c876fcd295792d7e83f3412d";
  private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

  // The oracle inputs were stamped with `touch -t 202405060708.10` in CEST, i.e. 05:08:10 UTC.
  private static readonly DateTime OracleStamp = new(2024, 5, 6, 5, 8, 10, DateTimeKind.Utc);

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"B1Vectors.{name}")
      ?? throw new FileNotFoundException($"Embedded B1 vector '{name}' is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("b1-default.b1", null, false)]
  [TestCase("b1-classic.b1", null, true)]
  [TestCase("b1-maximum.b1", null, true)]
  [TestCase("b1-aes.b1", "secret", false)]
  [Category("ThirdPartyVector")]
  public void Given_B1PackArchive_When_Read_Then_TreeTimesAndContentMatch(string vector, string? password, bool compressed) {
    var r = new B1Reader(new MemoryStream(Vector(vector)), password);
    Assert.That(r.IsEncrypted, Is.EqualTo(password != null));
    Assert.That(r.IsCompressed, Is.EqualTo(compressed));
    Assert.That(r.Entries.Select(e => (e.FileName, e.IsDirectory)), Is.EquivalentTo(new[] {
      ("docs", true), ("docs/notes.txt", false), ("docs/sub", true), ("docs/sub/tiny.txt", false), ("empty.txt", false), ("noise.bin", false),
    }));
    var notes = r.Entries.Single(e => e.FileName == "docs/notes.txt");
    Assert.That(notes.LastModified, Is.EqualTo(OracleStamp));
    Assert.That(Sha(r.Extract(notes)), Is.EqualTo(NotesSha));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.FileName == "docs/sub/tiny.txt"))), Is.EqualTo(TinySha));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.FileName == "noise.bin"))), Is.EqualTo(NoiseSha));
    Assert.That(r.Extract(r.Entries.Single(e => e.FileName == "empty.txt")), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_EncryptedArchive_When_NoPassword_Then_Refused() =>
    Assert.Throws<InvalidOperationException>(() => new B1Reader(new MemoryStream(Vector("b1-aes.b1"))));

  [Test, Category("EdgeCase")]
  public void Given_EncryptedArchive_When_WrongPassword_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => new B1Reader(new MemoryStream(Vector("b1-aes.b1")), "wrong"));

  [Test, Category("EdgeCase")]
  public void Given_FirstVolumeOfMultiVolumeArchive_When_Read_Then_NotSupported() =>
    Assert.Throws<NotSupportedException>(() => new B1Reader(new MemoryStream(Vector("b1-multi.part01.b1"))));

  [Test, Category("EdgeCase")]
  public void Given_FlippedBlockByte_When_Read_Then_AdlerCheckFails() {
    var bytes = Vector("b1-classic.b1");
    bytes[bytes.Length / 2] ^= 0x10;
    Assert.Throws<InvalidDataException>(() => new B1Reader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotB1_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => new B1Reader(new MemoryStream("PK\x03\x04 nothing"u8.ToArray())));

  internal static byte[] WriteOurs(bool compress, string? password) {
    var w = new B1Writer();
    w.AddFolder("docs", Stamp);
    w.AddFile("docs/report.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Our B1 payload, été.\n", 300))), Stamp);
    w.AddFile("docs/deep/inner.bin", [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 13))], Stamp);
    w.AddFile("empty.txt", [], Stamp);
    return w.ToArray(compress, password);
  }

  [TestCase(true, null)]
  [TestCase(false, null)]
  [TestCase(true, "pw")]
  [Category("RoundTrip")]
  public void Given_Files_When_Written_Then_ReadBack(bool compress, string? password) {
    var r = new B1Reader(new MemoryStream(WriteOurs(compress, password)), password);
    Assert.That(r.IsCompressed, Is.EqualTo(compress));
    Assert.That(r.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "docs", "docs/report.txt", "docs/deep", "docs/deep/inner.bin", "empty.txt" }));
    Assert.That(r.Extract(r.Entries.Single(e => e.FileName == "docs/deep/inner.bin")), Is.EqualTo(Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray()));
    Assert.That(r.Entries.Single(e => e.FileName == "empty.txt").LastModified, Is.EqualTo(Stamp));
  }

  [Test, Category("RoundTrip")]
  public void Given_LargeFile_When_WrittenAsLzma_Then_SpansSeveralBlocksAndReadsBack() {
    var data = new byte[3 << 20];
    new Random(1).NextBytes(data);
    var w = new B1Writer();
    w.AddFile("big.bin", data);
    var r = new B1Reader(new MemoryStream(w.ToArray()));
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Given_B1PackArchive_When_FileAddedAndFolderRemoved_Then_RestKept() {
    var d = new B1FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("b1-classic.b1"));
    d.Add(ms, [ArchiveInputInfo.InMemory("added.txt", "added\n"u8.ToArray())]);
    d.Remove(ms, ["docs/sub"]);
    var r = new B1Reader(ms);
    Assert.That(r.IsCompressed, Is.True);
    Assert.That(r.Entries.Select(e => e.FileName), Is.EquivalentTo(new[] { "docs", "docs/notes.txt", "empty.txt", "noise.bin", "added.txt" }));
    var notes = r.Entries.Single(e => e.FileName == "docs/notes.txt");
    Assert.That(notes.LastModified, Is.EqualTo(OracleStamp));
    Assert.That(Sha(r.Extract(notes)), Is.EqualTo(NotesSha));
  }

  [Test, Category("EdgeCase")]
  public void Given_EncryptedArchive_When_Edited_Then_RefusedAndUntouched() {
    var bytes = Vector("b1-aes.b1");
    using var ms = new MemoryStream();
    ms.Write(bytes);
    Assert.Catch(() => new B1FormatDescriptor().Remove(ms, ["noise.bin"]));
    Assert.That(ms.ToArray(), Is.EqualTo(bytes));
  }

  // ours-*.b1 were written by this writer; the b1-pack CLI listed and extracted them (b1pack-listing.txt).
  [TestCase("ours-lzma.b1", null)]
  [TestCase("ours-store.b1", null)]
  [TestCase("ours-aes.b1", "pw")]
  [TestCase("ours-edited.b1", null)]
  [Category("ThirdPartyVector")]
  public void Given_ArchiveB1PackExtracted_When_Read_Then_Valid(string vector, string? password) {
    var r = new B1Reader(new MemoryStream(Vector(vector)), password);
    foreach (var e in r.Entries)
      Assert.That(r.Extract(e), Has.Length.EqualTo(e.Size), e.FileName);
  }
}
