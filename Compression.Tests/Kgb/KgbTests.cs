using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Kgb;

namespace Compression.Tests.Kgb;

/// <summary>
/// KGB Archiver 1.0: the archives Debian's <c>kgb</c> wrote (ReferenceVectors/README.md) must
/// extract to the manifest and be reproduced byte for byte by our encoder; plus round trips,
/// edits and the refusals.
/// </summary>
[TestFixture]
public sealed class KgbTests {
  private sealed record Row(string Archive, string Name, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => {
    using var reader = new StreamReader(Open("manifest.tsv"));
    return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r'))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split('\t'))
      .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))];
  });

  private static Stream Open(string name) =>
    Assembly.GetExecutingAssembly().GetManifestResourceStream($"KgbVectors.{name}")
    ?? throw new FileNotFoundException($"Embedded KGB vector '{name}' is missing from the test assembly.");

  internal static byte[] Load(string name) {
    using var s = Open(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<int> Levels() => Enumerable.Range(0, 7);

  private static List<(string Name, byte[] Data)> Decode(byte[] archive) {
    using var ms = new MemoryStream(archive);
    var kgb = KgbArchive.Read(ms);
    var files = new List<(string, byte[])>();
    kgb.Decode(ms, _ => true, (e, d) => files.Add((e.Name, d)));
    return files;
  }

  internal static byte[] Text(int lines) {
    var sb = new StringBuilder();
    for (var i = 0; i < lines; ++i)
      sb.Append("Line ").Append(i).Append(": the archiver models every bit of this sentence.\r\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Levels))]
  public void ReferenceArchive_ExtractsToManifest(int level) {
    var name = $"kgb-m{level}.kgb";
    var expected = Manifest.Value.Where(r => r.Archive == name).ToArray();
    using var ms = new MemoryStream(Load(name));
    var kgb = KgbArchive.Read(ms);
    Assert.That(kgb.Level, Is.EqualTo(level));
    Assert.That(kgb.Entries.Select(e => (e.Name, e.Size)), Is.EqualTo(expected.Select(r => (r.Name, r.Size))));

    var got = Decode(Load(name));
    Assert.Multiple(() => {
      for (var i = 0; i < expected.Length; ++i)
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(got[i].Data)), Is.EqualTo(expected[i].Sha256), expected[i].Name);
    });
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Levels))]
  public void ReferenceArchive_OurEncoderReproducesItByteForByte(int level) {
    var original = Load($"kgb-m{level}.kgb");
    using var ours = new MemoryStream();
    KgbArchive.Write(ours, level, Decode(original));
    Assert.That(ours.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Descriptor_ListsAndExtractsSelectedEntry() {
    var d = new KgbFormatDescriptor();
    using var ms = new MemoryStream(Load("kgb-m2.kgb"));
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "empty.dat", "random.bin", "sub/code.exe", "table.bin", "text.txt", "wave.raw" }));
    Assert.That(list.All(e => e.Method == "PAQ6 -2"));

    var text = d.ExtractEntryToMemory(ms, "text.txt", null);
    var row = Manifest.Value.Single(r => r.Archive == "kgb-m2.kgb" && r.Name == "text.txt");
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(text)), Is.EqualTo(row.Sha256));
    Assert.That(d.ExtractEntryToMemory(ms, "missing", null), Is.Empty);
  }

  [Test]
  public void Descriptor_ExtractWritesDirectories() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_kgb_{Guid.NewGuid():N}");
    try {
      using var ms = new MemoryStream(Load("kgb-m3.kgb"));
      new KgbFormatDescriptor().Extract(ms, dir, null, ["sub/code.exe"]);
      var row = Manifest.Value.Single(r => r.Archive == "kgb-m3.kgb" && r.Name == "sub/code.exe");
      var data = File.ReadAllBytes(Path.Combine(dir, "sub", "code.exe"));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256));
      Assert.That(File.Exists(Path.Combine(dir, "text.txt")), Is.False);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(4)]
  [TestCase(5)]
  public void RoundTrip_ManyShapes(int level) {
    var rng = new Random(level);
    var random = new byte[5000];
    rng.NextBytes(random);
    List<(string, byte[])> files = [
      ("a.txt", Text(300)), ("zero.bin", new byte[20000]), ("one.bin", [0x42]), ("empty", []),
      ("dir/random.bin", random), ("ünï/cödé.txt", "äöü"u8.ToArray()), ("tab\tname", [1, 2, 3]),
    ];
    using var ms = new MemoryStream();
    KgbArchive.Write(ms, level, files);
    Assert.That(Decode(ms.ToArray()), Is.EqualTo(files));
  }

  [Test]
  public void RoundTrip_EmptyArchive() {
    using var ms = new MemoryStream();
    KgbArchive.Write(ms, 3, []);
    Assert.That(Encoding.ASCII.GetString(ms.ToArray(), 0, 13), Is.EqualTo("KGB_arch -3\r\n"));
    Assert.That(Decode(ms.ToArray()), Is.Empty);
  }

  [Test]
  public void Create_UsesLevelOptionAndDefaultsToThree() {
    var d = new KgbFormatDescriptor();
    ArchiveInputInfo[] inputs = [ArchiveInputInfo.InMemory("x.txt", Text(10))];
    using var def = new MemoryStream();
    d.Create(def, inputs, new FormatCreateOptions());
    Assert.That(KgbArchive.Read(def).Level, Is.EqualTo(3));
    using var six = new MemoryStream();
    d.Create(six, inputs, new FormatCreateOptions { Level = 6 });
    Assert.That(KgbArchive.Read(six).Level, Is.EqualTo(6));
    Assert.Throws<ArgumentOutOfRangeException>(() => d.Create(new MemoryStream(), inputs, new FormatCreateOptions { Level = 10 }));
    Assert.Throws<NotSupportedException>(() => d.Create(new MemoryStream(), inputs, new FormatCreateOptions { Password = "pw" }));
  }

  [Test]
  public void AddAndRemove_KeepTheArchiveLevel() {
    var d = new KgbFormatDescriptor();
    using var ms = new MemoryStream();
    KgbArchive.Write(ms, 1, [("a.txt", Text(5)), ("b.bin", [9, 8, 7])]);
    d.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "new"u8.ToArray()),
      ArchiveInputInfo.InMemory("a.txt", "replaced"u8.ToArray())]);
    d.Remove(ms, ["b.bin"]);
    Assert.That(KgbArchive.Read(ms).Level, Is.EqualTo(1));
    Assert.That(Decode(ms.ToArray()), Is.EqualTo(new List<(string, byte[])> { ("a.txt", "replaced"u8.ToArray()), ("c.txt", "new"u8.ToArray()) }));
  }

  [TestCase("")]
  [TestCase("line\nbreak")]
  [TestCase("cr\rname")]
  [TestCase("../escape")]
  [TestCase("a/..\\b")]
  public void Write_RefusesNamesAHeaderCannotCarry(string name) =>
    Assert.Throws<ArgumentException>(() => KgbArchive.Write(new MemoryStream(), 3, [(name, [1])]));

  [TestCase(-1)]
  [TestCase(10)]
  public void Write_RefusesLevelsOutsideZeroToNine(int level) =>
    Assert.Throws<ArgumentOutOfRangeException>(() => KgbArchive.Write(new MemoryStream(), level, [("a", [1])]));

  [Test]
  public void Read_RefusesOtherData() {
    Assert.Throws<InvalidDataException>(() => KgbArchive.Read(new MemoryStream("PK\x03\x04"u8.ToArray())));
    Assert.Throws<InvalidDataException>(() => KgbArchive.Read(new MemoryStream("KGB_arch -3\r\n5\ta\r\n\x1A"u8.ToArray())));
    Assert.Throws<InvalidDataException>(() => KgbArchive.Read(new MemoryStream("KGB_arch -3\r\n-5\ta\r\n\x1A\f\0"u8.ToArray())));
  }

  [Test]
  public void Read_HeaderWithoutLevelDigitMeansThree() {
    var kgb = KgbArchive.Read(new MemoryStream("KGB_arch\r\n0\tx\r\n\x1A\f\0"u8.ToArray()));
    Assert.That(kgb.Level, Is.EqualTo(3));
    Assert.That(kgb.Entries, Is.EqualTo(new[] { new KgbEntry("x", 0) }));
  }
}
