using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.AppleArchive;

namespace Compression.Tests.AppleArchive;

/// <summary>
/// Apple Archive. Vectors were written by macOS 26.6.2's <c>aa archive</c> with every compression it
/// offers; see ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class AppleArchiveTests {
  private const string NotesSha = "0e1a649b0980c9b235b8f1331348a4c7804c5f001a578cc76bcec93d27f6880b";
  private const string DataSha = "435a256d18c66340956205148b66d14106f11640535738517611a4c9d54480d7";
  private const string NoiseSha = "7049256576476e975c3ba92ffbd15f86702edab7c850fa745663d3d773ca0d6f";
  private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AppleArchiveVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Apple Archive vector '{name}' is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static AppleArchiveReader Open(string name) => new(new MemoryStream(Vector(name)));

  [TestCase("aa-raw.aar", "raw")]
  [TestCase("aa-lzfse.aar", "lzfse")]
  [TestCase("aa-lzfse-16k.aar", "lzfse")]
  [TestCase("aa-zlib.aar", "zlib")]
  [TestCase("aa-zlib-16k.aar", "zlib")]
  [TestCase("aa-lzma.aar", "lzma")]
  [TestCase("aa-lz4.aar", "lz4")]
  [TestCase("aa-lzbitmap.aar", "lzbitmap")]
  [Category("ThirdPartyVector")]
  public void Given_AaArchive_When_Read_Then_ItemsAndContentMatchAaList(string vector, string compression) {
    var r = Open(vector);
    Assert.That(r.Compression, Is.EqualTo(compression));
    Assert.That(r.IsTruncated, Is.False);
    Assert.That(r.Entries.Select(e => $"{e.Type} {e.Path}"), Is.EqualTo(new[] {
      "D ", "F empty.txt", "L link-to-notes", "F notes.txt", "D sub", "F sub/data.bin", "F sub/noise.bin",
    }));
    Assert.That(r.Entries.Single(e => e.Type == 'L').LinkTarget, Is.EqualTo("notes.txt"));
    var notes = r.Entries.Single(e => e.Path == "notes.txt");
    Assert.That((notes.Mode, notes.Uid, notes.Gid, notes.LastModified), Is.EqualTo(((int?)0x1A4, (long?)501, (long?)20, (DateTime?)Stamp)));
    Assert.That(Sha(r.Extract(notes)), Is.EqualTo(NotesSha));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.Path == "sub/data.bin"))), Is.EqualTo(DataSha));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.Path == "sub/noise.bin"))), Is.EqualTo(NoiseSha));
    Assert.That(r.Extract(r.Entries.Single(e => e.Path == "empty.txt")), Is.Empty);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_AaArchive_When_ListedByDescriptor_Then_RootOmittedAndKindsNamed() {
    var list = new AppleArchiveFormatDescriptor().List(new MemoryStream(Vector("aa-lzfse.aar")), null);
    Assert.That(list.Select(e => (e.Name, e.Method)), Is.EqualTo(new[] {
      ("empty.txt", "lzfse"), ("link-to-notes", "symlink"), ("notes.txt", "lzfse"), ("sub", "dir"),
      ("sub/data.bin", "lzfse"), ("sub/noise.bin", "lzfse"),
    }));
  }

  internal static byte[] WriteOurs(string compression) {
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"AAR probe line {i % 41}, value {i * 7 % 101}\n")));
    var raw = Enumerable.Range(0, 30000).Select(i => (byte)(i * 7 + i / 256)).ToArray();
    var w = new AppleArchiveWriter();
    w.AddDirectory("docs", Stamp);
    w.AddFile("docs/text.txt", text, Stamp);
    w.AddFile("raw.bin", raw, Stamp);
    w.AddFile("empty.txt", [], Stamp);
    return w.ToArray(compression);
  }

  // ours-*.aar are this writer's output; macOS aa listed and extracted them (aa-ours-listing.txt).
  [TestCase("lzfse")]
  [TestCase("zlib")]
  [Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_BytesEqualTheFixtureAaExtracted(string compression) =>
    Assert.That(WriteOurs(compression), Is.EqualTo(Vector($"ours-{compression}.aar")));

  [Test, Category("ThirdPartyVector")]
  public void Given_OurFixture_When_Read_Then_HashesMatchWhatAaExtracted() {
    var r = Open("ours-lzfse.aar");
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.Path == "docs/text.txt"))), Is.EqualTo("a3344e726cc5073e83faf1b9bf8517f0014244bffbc78bbf4b0e76cc45a7fe15"));
    Assert.That(Sha(r.Extract(r.Entries.Single(e => e.Path == "raw.bin"))), Is.EqualTo("9178419af3a1b79f137c784810917d8481eca5e7888ad34420adb7e0095a88e5"));
  }

  [TestCase("raw")]
  [TestCase("lzfse")]
  [TestCase("zlib")]
  [Category("RoundTrip")]
  public void Given_Files_When_Written_Then_ReadBack(string compression) {
    var w = new AppleArchiveWriter();
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i % 13}\n")));
    w.AddDirectory("docs", Stamp);
    w.AddFile("docs/a.txt", text, Stamp);
    w.AddFile("empty", [], Stamp);
    var r = new AppleArchiveReader(new MemoryStream(w.ToArray(compression)));
    Assert.That(r.Compression, Is.EqualTo(compression));
    Assert.That(r.Entries.Select(e => (e.Type, e.Path)), Is.EqualTo(new[] { ('D', "docs"), ('F', "docs/a.txt"), ('F', "empty") }));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(text));
    Assert.That(r.Entries[1].LastModified, Is.EqualTo(Stamp));
    Assert.That(r.Entries[1].Mode, Is.EqualTo(0x1A4));
  }

  [Test, Category("RoundTrip")]
  public void Given_LargeData_When_WrittenAsLzfse_Then_SplitIntoBlocksAndReadBack() {
    var data = new byte[(4 << 20) + 12345];
    new Random(5).NextBytes(data.AsSpan(0, 100_000));
    var w = new AppleArchiveWriter();
    w.AddFile("big.bin", data, Stamp);
    var r = new AppleArchiveReader(new MemoryStream(w.ToArray("lzfse")));
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Given_AaArchive_When_FileAddedAndOneRemoved_Then_OtherItemsKeepTheirFields() {
    var d = new AppleArchiveFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("aa-zlib.aar"));
    d.Add(ms, [ArchiveInputInfo.InMemory("added.txt", "hello"u8.ToArray())]);
    d.Remove(ms, ["sub/data.bin"]);
    var r = new AppleArchiveReader(ms);
    Assert.That(r.Compression, Is.EqualTo("zlib"));
    Assert.That(r.Entries.Select(e => e.Path), Is.EqualTo(new[] { "", "empty.txt", "link-to-notes", "notes.txt", "sub", "sub/noise.bin", "added.txt" }));
    var notes = r.Entries.Single(e => e.Path == "notes.txt");
    Assert.That(notes.FieldKeys, Is.EqualTo(new[] { "TYP", "PAT", "UID", "GID", "MOD", "FLG", "CTM", "MTM", "DAT" }));
    Assert.That(Sha(r.Extract(notes)), Is.EqualTo(NotesSha));
    Assert.That(r.Extract(r.Entries[^1]), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_Lz4Archive_When_Edited_Then_RefusedAndUntouched() {
    var bytes = Vector("aa-lz4.aar");
    using var ms = new MemoryStream();
    ms.Write(bytes);
    Assert.Throws<NotSupportedException>(() => new AppleArchiveFormatDescriptor().Remove(ms, ["notes.txt"]));
    Assert.That(ms.ToArray(), Is.EqualTo(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoSignature_When_Opened_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => new AppleArchiveReader(new MemoryStream("PK\x03\x04 not apple"u8.ToArray())));

  [Test, Category("EdgeCase")]
  public void Given_TruncatedPlainArchive_When_Opened_Then_EarlierItemsListed() {
    var bytes = Vector("aa-raw.aar");
    var r = new AppleArchiveReader(new MemoryStream(bytes[..^100]));
    Assert.That(r.IsTruncated, Is.True);
    Assert.That(r.Entries.Select(e => e.Path).Last(), Is.EqualTo("sub/data.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownFieldSubtype_When_Opened_Then_ParsingStops() {
    var bytes = Vector("aa-raw.aar");
    bytes[6 + 3] = (byte)'Z'; // first item's TYP subtype
    var r = new AppleArchiveReader(new MemoryStream(bytes));
    Assert.That(r.IsTruncated, Is.True);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptCompressedBlock_When_Opened_Then_InvalidData() {
    var bytes = Vector("aa-lzfse.aar");
    bytes[^40] ^= 0x5A;
    Assert.Catch(() => new AppleArchiveReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_Symlink_When_Extracted_Then_Refused() {
    var r = Open("aa-raw.aar");
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries.Single(e => e.Type == 'L')));
  }
}
