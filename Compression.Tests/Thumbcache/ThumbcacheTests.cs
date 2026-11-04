using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Thumbcache;

namespace Compression.Tests.Thumbcache;

/// <summary>
/// Windows Explorer thumbnail caches. Oracle: Fox-IT's dissect.thumbcache, run over the Vista, 7, 8.1,
/// 10 and 11 databases of its own test corpus; its view is pinned in <c>dissect-listing.txt</c>. The
/// corpus itself is not redistributable here (AGPL-3.0), so the full comparison runs only where
/// <c>CWB_THUMBCACHE_CORPUS</c> points at a checkout of it; the two header-only databases are
/// committed. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class ThumbcacheTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ThumbcacheVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded thumbcache vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // ── real files ───────────────────────────────────────────────────────

  [TestCase("windows_7-thumbcache_32-empty.db", 21u)]
  [TestCase("windows_10-thumbcache_1280-empty.db", 32u)]
  [Category("ThirdPartyVector")]
  public void Given_EmptyDatabaseWrittenByWindows_When_Listed_Then_NoEntries(string file, uint version) {
    var data = Vector(file);
    var r = new ThumbcacheReader(data);
    Assert.That(r.Version, Is.EqualTo(version));
    Assert.That(r.Entries, Is.Empty);
    Assert.That(new ThumbcacheFormatDescriptor().List(new MemoryStream(data), null), Is.Empty);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_DissectCorpus_When_EveryDatabaseIsRead_Then_EntriesMatchDissect() {
    var corpus = Environment.GetEnvironmentVariable("CWB_THUMBCACHE_CORPUS");
    if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus))
      Assert.Ignore("Set CWB_THUMBCACHE_CORPUS to dissect.thumbcache's tests/data directory to run the corpus comparison.");

    var lines = Encoding.ASCII.GetString(Vector("dissect-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var files = lines.Where(l => l.StartsWith("# ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
    Assert.That(files, Has.Count.EqualTo(49));
    var compared = 0;
    foreach (var header in files) {
      var rel = header[1];
      var data = File.ReadAllBytes(Path.Combine(corpus, rel.Replace('/', Path.DirectorySeparatorChar)));
      var r = new ThumbcacheReader(data);
      Assert.That($"version={r.Version}", Is.EqualTo(header[2]), rel);
      Assert.That($"type={r.CacheType}", Is.EqualTo(header[3]), rel);
      var expected = lines.Where(l => l.StartsWith(rel + " ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
      Assert.That(r.Entries.Select(e => e.HashHex), Is.EqualTo(expected.Select(l => l[1])), rel);
      for (var i = 0; i < expected.Count; ++i) {
        var e = r.Entries[i];
        var id = Convert.ToHexStringLower(Encoding.Unicode.GetBytes(e.Identifier));
        Assert.That(id.Length == 0 ? "-" : id, Is.EqualTo(expected[i][2]), $"{rel} #{i}");
        Assert.That(e.DataSize, Is.EqualTo(int.Parse(expected[i][3])), $"{rel} #{i}");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data.AsSpan((int)e.DataOffset, e.DataSize))), Is.EqualTo(expected[i][4]), $"{rel} #{i}");
        ++compared;
      }
    }
    Assert.That(compared, Is.EqualTo(lines.Length - files.Count));
  }

  // ── synthetic databases built from the documented layout ────────────

  private static readonly byte[] Bmp = [(byte)'B', (byte)'M', 1, 2, 3, 4, 5];
  private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9];
  private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

  /// <summary>Builds a database: (identifier, data, vista extension) per entry, with 2 bytes of padding each.</summary>
  private static byte[] Build(uint version, params (string Id, byte[] Data, string Ext)[] entries) {
    var fixedSize = version switch { 20 => 0x38, 21 => 0x30, _ => 0x38 };
    using var body = new MemoryStream();
    var hash = 1UL;
    foreach (var (id, data, ext) in entries) {
      var idBytes = Encoding.Unicode.GetBytes(id);
      const int pad = 2;
      var size = fixedSize + idBytes.Length + pad + data.Length;
      var rec = new byte[size];
      "CMMM"u8.CopyTo(rec);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)size);
      BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8), hash++);
      var p = 16;
      if (version == 20) {
        Encoding.Unicode.GetBytes(ext.PadRight(4, '\0')).CopyTo(rec, p);
        p += 8;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(p), (uint)idBytes.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(p + 4), pad);
      BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(p + 8), (uint)data.Length);
      if (version >= 30) {
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(p + 12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(p + 16), 24);
      }
      idBytes.CopyTo(rec, fixedSize);
      data.CopyTo(rec, fixedSize + idBytes.Length + pad);
      body.Write(rec);
    }
    var file = new byte[ThumbcacheReader.HeaderSize + body.Length];
    "CMMM"u8.CopyTo(file);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), version);
    var legacy = version <= 21;
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(legacy ? 12 : 16), ThumbcacheReader.HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(legacy ? 16 : 20), (uint)file.Length);
    if (legacy) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)entries.Length);
    body.ToArray().CopyTo(file, ThumbcacheReader.HeaderSize);
    return file;
  }

  [TestCase(20u)]
  [TestCase(21u)]
  [TestCase(30u)]
  [TestCase(31u)]
  [TestCase(32u)]
  [Category("HappyPath")]
  public void Given_DatabaseOfEachVersion_When_Extracted_Then_ThumbnailsComeBackByteExact(uint version) {
    var db = Build(version, ("aa11", Bmp, "bmp"), ("bb22", Jpeg, "jpg"), ("cc33", Png, "png"));
    var dir = Path.Combine(Path.GetTempPath(), "tc_" + Guid.NewGuid().ToString("N"));
    try {
      new ThumbcacheFormatDescriptor().Extract(new MemoryStream(db), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "aa11.bmp")), Is.EqualTo(Bmp));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "bb22.jpg")), Is.EqualTo(Jpeg));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "cc33.png")), Is.EqualTo(Png));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
    var r = new ThumbcacheReader(db);
    Assert.That(r.Entries.Select(e => (e.Width, e.Height)).Distinct().Single(), Is.EqualTo(version >= 30 ? (32u, 24u) : (0u, 0u)));
  }

  [Test, Category("EdgeCase")]
  public void Given_VistaExtensionField_When_Named_Then_ItWinsOverTheSniffedType() {
    var names = new ThumbcacheFormatDescriptor().List(new MemoryStream(Build(20, ("x", Bmp, "JPG"))), null).Select(e => e.Name);
    Assert.That(names, Is.EqualTo(new[] { "x.jpg" }));
  }

  [Test, Category("EdgeCase")]
  public void Given_RepeatedOrUnsafeIdentifiers_When_Listed_Then_NamesAreUniqueAndPathSafe() {
    var db = Build(32, ("same", Bmp, ""), ("same", Bmp, ""), ("..\\evil/x\u0001", Png, ""), ("", [1, 2], ""));
    var names = new ThumbcacheFormatDescriptor().List(new MemoryStream(db), null).Select(e => e.Name).ToList();
    Assert.That(names[0], Is.EqualTo("same.bmp"));
    Assert.That(names[1], Is.EqualTo("same~2.bmp"));
    Assert.That(names[2], Is.EqualTo("___evil_x_.png"));
    Assert.That(names[3], Is.EqualTo("0400000000000000.bin"));
  }

  [Test, Category("Boundary")]
  public void Given_ZeroLengthThumbnail_When_Listed_Then_EmptyEntry() {
    var r = new ThumbcacheReader(Build(21, ("e", [], "")));
    Assert.That(r.Entries.Single().DataSize, Is.Zero);
  }

  [Test, Category("EdgeCase")]
  public void Given_IndexDatabase_When_Read_Then_RefusedAsIndex() {
    var ex = Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader("IMMM \0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray()));
    Assert.That(ex!.Message, Does.Contain("idx"));
  }

  [TestCase(19u)]
  [TestCase(22u)]
  [TestCase(33u)]
  [Category("Boundary")]
  public void Given_UnknownVersion_When_Read_Then_InvalidData(uint version) {
    var db = Build(32, ("a", Bmp, ""));
    BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(4), version);
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader(db));
  }

  [Test, Category("EdgeCase")]
  public void Given_EntryWithoutSignature_When_Read_Then_InvalidData() {
    var db = Build(32, ("a", Bmp, ""));
    db[ThumbcacheReader.HeaderSize] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader(db));
  }

  [Test, Category("EdgeCase")]
  public void Given_DataSizeLargerThanTheRecord_When_Read_Then_InvalidData() {
    var db = Build(32, ("a", Bmp, ""));
    BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(ThumbcacheReader.HeaderSize + 24), 1000);
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader(db));
  }

  [Test, Category("EdgeCase")]
  public void Given_FreeOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var db = Build(32, ("a", Bmp, ""));
    BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(20), (uint)db.Length + 1);
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader(db));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedHeader_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader("CMMM"u8));
    Assert.Throws<InvalidDataException>(() => _ = new ThumbcacheReader([]));
  }
}
