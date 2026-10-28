using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.CompactPro;

namespace Compression.Tests.CompactPro;

/// <summary>
/// Compact Pro against The Unarchiver. Fixtures: real archives from the Garbo Mac archive (Compact Pro
/// 1.x output) and archives our writer produced; <c>unar-listing.txt</c> is unar's extraction of each
/// (resource forks as visible "name.rsrc"). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class CompactProTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CompactProVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Compact Pro vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<string[]> Listing(string vector)
    => Encoding.UTF8.GetString(Vector("unar-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .Where(p => p[0] == vector)
      .ToList();

  internal static readonly DateTime FixedTime = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

  /// <summary>Bytes that exercise every RLE escape path: runs, 0x81, 0x81 0x82, 0x81 0x81 and a trailing 0x81.</summary>
  internal static readonly byte[] Tricky = [
    .. Enumerable.Repeat((byte)'A', 300), 0x81, 0x82, 0x81, 0x81, 0x41, 0x81, 0x82, 0x82, 0x82, 0x82, 0x82,
    .. Enumerable.Range(0, 256).Select(i => (byte)i), 0x81, 0x00, 0x81, 0x01, .. Enumerable.Repeat((byte)0x81, 7), 0x81,
  ];

  internal static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 200).Select(i => $"Compact Pro line {i}\r")));

  internal static byte[] OursArchive(string name) {
    var w = new CompactProWriter();
    switch (name) {
      case "ours-flat.cpt":
        w.AddFile("tricky.bin", Tricky, modified: FixedTime);
        w.AddFile("readme.txt", Text, resource: Tricky[..400], type: 0x54455854, creator: 0x74747874, modified: FixedTime);
        w.AddFile("empty", [], modified: FixedTime);
        break;
      case "ours-folders.cpt":
        w.AddFolder("Empty Folder");
        w.AddFile("Docs/Inner/deep.txt", Text, modified: FixedTime);
        w.AddFile("Docs/top.bin", Tricky, modified: FixedTime);
        w.AddFile("root.txt", Text[..100], modified: FixedTime);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(name));
    }
    return w.ToArray();
  }

  [TestCase("trashcht.cpt")]
  [TestCase("simsonit.cpt")]
  [TestCase("dgsmrmcr.cpt")]
  [TestCase("ours-flat.cpt")]
  [TestCase("ours-folders.cpt")]
  [Category("ThirdPartyVector")]
  public void Given_Archive_When_Extracted_Then_MatchesUnar(string vector) {
    var expected = Listing(vector).Where(p => p[2] != "0").ToDictionary(p => p[1], p => (Size: long.Parse(p[2]), Sha: p[3]));
    var d = new CompactProFormatDescriptor();
    var files = d.List(new MemoryStream(Vector(vector)), null).Where(e => !e.IsDirectory).ToList();
    // compared on non-empty forks: unar materialises an empty data fork, we list it with size 0
    Assert.That(files.Where(e => e.OriginalSize > 0).Select(e => e.Name), Is.EquivalentTo(expected.Keys));
    foreach (var e in files.Where(f => f.OriginalSize > 0)) {
      var bytes = d.ExtractEntryToMemory(new MemoryStream(Vector(vector)), e.Name, null);
      Assert.That(bytes.LongLength, Is.EqualTo(expected[e.Name].Size), e.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(expected[e.Name].Sha), e.Name);
    }
  }

  [TestCase("ours-flat.cpt")]
  [TestCase("ours-folders.cpt")]
  [Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_ByteIdenticalToWhatUnarRead(string vector)
    => Assert.That(OursArchive(vector), Is.EqualTo(Vector(vector)));

  [Test, Category("RoundTrip")]
  public void Given_TrickyBytes_When_RleRoundTripped_Then_Identical() {
    foreach (var len in new[] { 0, 1, 2, 3, Tricky.Length }) {
      var data = Tricky[..len];
      var encoded = CompactProCodec.EncodeRle(data);
      Assert.That(CompactProCodec.DecodeFork(encoded, data.Length, lzh: false), Is.EqualTo(data), $"length {len}");
    }
    byte[] lone = [0x81];
    Assert.That(CompactProCodec.DecodeFork(CompactProCodec.EncodeRle(lone), 1, lzh: false), Is.EqualTo(lone));
  }

  [Test, Category("RoundTrip")]
  public void Given_Descriptor_When_CreatedWithResourceFork_Then_Paired() {
    var d = new CompactProFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("app", Text),
      ArchiveInputInfo.InMemory("app.rsrc", Tricky),
      ArchiveInputInfo.InMemory("only.rsrc", Text),
    ], new FormatCreateOptions());
    var list = d.List(new MemoryStream(ms.ToArray()), null).Select(e => e.Name).ToList();
    Assert.That(list, Is.EquivalentTo(new[] { "app", "app.rsrc", "only.rsrc" }));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(ms.ToArray()), "app.rsrc", null), Is.EqualTo(Tricky));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMarker_When_Read_Then_InvalidData() {
    var bytes = Vector("ours-flat.cpt");
    bytes[0] = 2;
    Assert.Throws<InvalidDataException>(() => _ = new CompactProReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptDirectory_When_Read_Then_CrcMismatch() {
    var bytes = Vector("ours-flat.cpt");
    bytes[^3] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => _ = new CompactProReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_CorruptForkData_When_Extracted_Then_InvalidData() {
    var bytes = Vector("ours-flat.cpt");
    bytes[20] ^= 0x55; // inside tricky.bin's data fork (data starts at offset 8)
    var r = new CompactProReader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.ExtractForks(r.Entries.First(e => e.FileName == "tricky.bin")));
  }

  [Test, Category("EdgeCase")]
  public void Given_DirectoryOffsetPastEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("ours-flat.cpt");
    bytes[4] = 0x7F;
    Assert.Throws<InvalidDataException>(() => _ = new CompactProReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_TooShort_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new CompactProReader(new MemoryStream([1, 1, 0])));

  [Test, Category("EdgeCase")]
  public void Given_OverlongName_When_Written_Then_Rejected()
    => Assert.Throws<ArgumentException>(() => new CompactProWriter().AddFile(new string('x', 64), []));
}
