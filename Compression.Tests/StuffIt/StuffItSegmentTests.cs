using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// Segmented StuffIt sets: no set StuffIt wrote was available, so ours are the samples and The
/// Unarchiver's unar is the oracle — it must extract them byte for byte — while our reader must
/// read them back from any part and refuse a missing one by name.
/// </summary>
[TestFixture]
public class StuffItSegmentTests {
  private string _dir = null!;

  [SetUp]
  public void Setup() {
    this._dir = Path.Combine(Path.GetTempPath(), $"cwb_sitseg_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static readonly Dictionary<string, byte[]> Files = new() {
    ["hello.txt"] = "hello segments\n"u8.ToArray(),
    ["docs/noise.bin"] = Noise(9000, 1),
    ["docs/fox.txt"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox.\n", 50))),
  };

  private static byte[] Noise(int n, int seed) {
    var b = new byte[n];
    new Random(seed).NextBytes(b);
    return b;
  }

  /// <summary>Creates a set through the descriptor at <paramref name="target"/>; returns the part paths in order.</summary>
  private List<string> CreateSet(string target, string container, string partSize, string? password = null) {
    var path = Path.Combine(this._dir, target);
    using (var f = File.Create(path))
      new StuffItFormatDescriptor().Create(f, [.. Files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], new FormatCreateOptions {
        Password = password,
        FormatSpecific = new Dictionary<string, string> {
          [FormatOptionKeys.TargetCompatibility] = container,
          [FormatOptionKeys.VolumeSize] = partSize,
          ["Compression"] = "Store", // keep the archive large enough for several parts
        },
      });
    var setBase = StuffItSegments.SetBase(path);
    var parts = new List<string> { path };
    for (var n = 2; File.Exists(StuffItSegments.PartPath(setBase, n)); ++n) parts.Add(StuffItSegments.PartPath(setBase, n));
    return parts;
  }

  private static void AssertFiles(string partPath, string? password = null) {
    using var stream = File.OpenRead(partPath);
    using var r = new StuffItReader(stream, password);
    foreach (var (name, data) in Files)
      Assert.That(r.Extract(r.Entries.Single(e => e.FileName == name)), Is.EqualTo(data), name);
  }

  [TestCase("Classic")]
  [TestCase("StuffIt5")]
  [Category("RoundTrip")]
  public void GivenSegmentSize_WhenCreated_ThenPartsHaveThatSizeAndTheSetReadsBackFromAnyPart(string container) {
    var parts = this.CreateSet("set.sit", container, "2k");

    Assert.That(parts, Has.Count.GreaterThan(3));
    Assert.That(parts.Select(Path.GetFileName), Is.EqualTo(new[] { "set.sit" }.Concat(Enumerable.Range(2, parts.Count - 1).Select(n => $"set.sit.{n}"))));
    Assert.That(parts.SkipLast(1).Select(p => new FileInfo(p).Length), Is.All.EqualTo(2048));
    AssertFiles(parts[0]);
    AssertFiles(parts[^1]); // opening a later part finds the others
    AssertFiles(parts[1]);
  }

  [Test, Category("HappyPath")]
  public void GivenSegmentedSet_ThenEveryPartCarriesTheHeader() {
    var parts = this.CreateSet("hdr.sit", "Classic", "4096");
    var archiveSize = 0L;
    for (var i = 0; i < parts.Count; ++i) {
      var head = File.ReadAllBytes(parts[i]).AsSpan(0, 100);
      Assert.That(head[..3].ToArray(), Is.EqualTo(new byte[] { 0xB0, 0x56, 0x00 }));
      Assert.That(head[3], Is.EqualTo(i + 1), "part number");
      Assert.That(StuffItSegments.FileName(head), Is.EqualTo("hdr.sit"));
      Assert.That(System.Text.Encoding.ASCII.GetString(head.Slice(68, 8)), Is.EqualTo("SIT!SIT!"));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(head[86..]), Is.Zero, "no resource fork");
      archiveSize = BinaryPrimitives.ReadUInt32BigEndian(head[90..]);
      Assert.That(head[94..].ToArray(), Is.All.Zero);
    }
    Assert.That(parts.Sum(p => new FileInfo(p).Length - 100), Is.EqualTo(archiveSize));
  }

  [Test, Category("HappyPath")]
  public void GivenTargetNamedAsPartOne_WhenCreated_ThenLaterPartsFollowItsName() {
    var parts = this.CreateSet("one.sit.1", "Classic", "3000");
    Assert.That(parts.Skip(1).Select(Path.GetFileName), Is.All.StartsWith("one.sit."));
    Assert.That(StuffItSegments.FileName(File.ReadAllBytes(parts[0])), Is.EqualTo("one.sit"));
    AssertFiles(parts[^1]);
  }

  [Test, Category("RoundTrip")]
  public void GivenPasswordAndStuffIt5Container_WhenSegmented_ThenTheSetDecryptsAndAWrongPasswordIsRefused() {
    var parts = this.CreateSet("enc.sit", "StuffIt5", "2500", "secret");
    AssertFiles(parts[0], "secret");
    Assert.That(() => AssertFiles(parts[^1], "wrong"), Throws.InstanceOf<ArchivePasswordException>());
  }

  /// <summary>
  /// A classic archive's key check lives in its resource fork; unar does not carry that fork into
  /// the archive it opens inside a set, so such a set is not written.
  /// </summary>
  [Test, Category("Exception")]
  public void GivenPasswordAndClassicContainer_WhenSegmented_ThenRefused()
    => Assert.That(() => this.CreateSet("encc.sit", "Classic", "2500", "secret"), Throws.InstanceOf<NotSupportedException>());

  /// <summary>A set whose file has a resource fork (as a classic encrypted archive does) joins into a MacBinary file the reader decrypts.</summary>
  [Test, Category("RoundTrip")]
  public void GivenAFileWithAResourceFork_WhenSegmentedAndRead_ThenBothForksJoin() {
    using var macBinary = new MemoryStream();
    new StuffItFormatDescriptor().Create(macBinary, [.. Files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))],
      new FormatCreateOptions { Password = "secret", FormatSpecific = new Dictionary<string, string> { ["Compression"] = "Store" } });
    var first = Path.Combine(this._dir, "rsrc.sit");
    using (var f = File.Create(first))
      StuffItSegments.Write(macBinary.ToArray(), "rsrc.sit", 3000, f, n => File.Create(StuffItSegments.PartPath(first, n)));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(first).AsSpan(86)), Is.GreaterThan(0u), "resource fork");
    AssertFiles(StuffItSegments.PartPath(first, 2), "secret");
  }

  [Test, Category("Exception")]
  public void GivenAMissingPart_WhenRead_ThenItIsNamed() {
    var parts = this.CreateSet("gap.sit", "StuffIt5", "2k");
    File.Delete(parts[1]);
    var ex = Assert.Throws<FileNotFoundException>(() => AssertFiles(parts[0]));
    Assert.That(ex!.Message, Does.Contain("Part 2").And.Contain("gap.sit.2"));
  }

  [Test, Category("Exception")]
  public void GivenAPartFromAnotherSet_WhenRead_ThenItIsNotTaken() {
    var a = this.CreateSet("a.sit", "Classic", "2k");
    var b = this.CreateSet("ab.sit", "StuffIt5", "2k"); // shares the prefix "a" but holds another file
    File.Delete(a[1]);
    Assert.Throws<FileNotFoundException>(() => AssertFiles(a[0]));
    AssertFiles(b[0]);
  }

  [TestCase("100")]
  [TestCase("120")] // 255 parts of 20 bytes would not hold the archive
  [Category("Exception")]
  public void GivenSegmentSizeTooSmall_WhenCreated_ThenRefused(string size)
    => Assert.That(() => this.CreateSet("small.sit", "Classic", size), Throws.InstanceOf<ArgumentException>());

  [Test, Category("Exception")]
  public void GivenSegmentedSet_WhenModified_ThenRefused() {
    var parts = this.CreateSet("mod.sit", "Classic", "2k");
    using var f = new FileStream(parts[0], FileMode.Open, FileAccess.ReadWrite);
    Assert.That(() => new StuffItFormatDescriptor().Remove(f, ["hello.txt"]), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Exception")]
  public void GivenSegmentSizeAndAMemoryStream_WhenCreated_ThenRefused()
    => Assert.That(() => new StuffItFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { [FormatOptionKeys.VolumeSize] = "1k" } }), Throws.ArgumentException);

  // ── unar as the oracle ────────────────────────────────────────────────────────

  [TestCase("Classic", null)]
  [TestCase("StuffIt5", null)]
  [TestCase("StuffIt5", "secret")]
  [Category("ExternalInterop")]
  public void GivenOurSegmentedSet_WhenUnarExtractsIt_ThenEveryFileMatches(string container, string? password) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var parts = this.CreateSet("unar.sit", container, "2k", password);
    var outDir = Path.Combine(this._dir, "out");
    var pass = password is null ? "" : $"-p '{password}' ";
    var run = FsInteropToolbox.RunWsl($"unar -q -f -k skip {pass}-o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(parts[0])}'");
    Assert.That(run.ExitCode, Is.Zero, $"unar failed:\n{run.StdOut}\n{run.StdErr}");
    var extracted = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
    TestContext.Out.WriteLine(string.Join("\n", extracted.Select(f => Path.GetRelativePath(outDir, f))));
    foreach (var (name, data) in Files) {
      var match = extracted.SingleOrDefault(f => f.Replace('\\', '/').EndsWith("/" + name, StringComparison.Ordinal));
      Assert.That(match, Is.Not.Null, $"unar did not write {name}");
      Assert.That(File.ReadAllBytes(match!), Is.EqualTo(data), name);
    }
  }
}
