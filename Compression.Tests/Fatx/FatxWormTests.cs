using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Fatx;

namespace Compression.Tests.Fatx;

/// <summary>
/// FATX WORM (Write-Once-Read-Many) round-trip tests: the writer produces a
/// FATX image from a synthetic input set and the reader walks it back,
/// matching names + bytes exactly. Equivalence-class coverage:
/// </summary>
/// <remarks>
/// HappyPath: single file, multiple files, nested directories.<br/>
/// Boundary: empty input, large file spanning multiple clusters,
///   maximal 42-byte name, file aligned to cluster boundary.<br/>
/// Sad: name too long → truncation with ~1 tail, control chars sanitised.
/// </remarks>
[TestFixture]
public class FatxWormTests {

  private static byte[] Build(params (string Name, byte[] Data)[] files) {
    var w = new FatxWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static List<FatxEntry> Read(byte[] image) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    return [..r.Entries];
  }

  private static byte[] Extract(byte[] image, string name) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == name && !e.IsDirectory)
      ?? throw new InvalidOperationException($"FATX round-trip: entry '{name}' not found.");
    return r.Extract(entry);
  }

  // ── Magic + capabilities ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Image_StartsWithFatxMagic() {
    var image = Build(("hello.txt", "Greetings from Xbox!\n"u8.ToArray()));
    Assert.That(image[0], Is.EqualTo((byte)'F'));
    Assert.That(image[1], Is.EqualTo((byte)'A'));
    Assert.That(image[2], Is.EqualTo((byte)'T'));
    Assert.That(image[3], Is.EqualTo((byte)'X'));
    var spc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x08));
    Assert.That(spc > 0, "sectors_per_cluster must be > 0");
    Assert.That((spc & (spc - 1)) == 0, "sectors_per_cluster must be power of two");
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x0C));
    Assert.That(rootCluster, Is.EqualTo(1u), "FATX root lives at cluster 1");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesParseableImage() {
    var d = new FatxFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    var creatable = (IArchiveCreatable)d;

    using var output = new MemoryStream();
    creatable.Create(output, [
      ArchiveInputInfo.InMemory("readme.txt", "WORM\n"u8.ToArray()),
    ], new FormatCreateOptions());

    var bytes = output.ToArray();
    var listed = d.List(new MemoryStream(bytes), null);
    Assert.That(listed, Has.Count.EqualTo(1));
    Assert.That(listed[0].Name, Is.EqualTo("readme.txt"));
    Assert.That(listed[0].OriginalSize, Is.EqualTo(5));

    var extracted = d.ExtractEntryToMemory(new MemoryStream(bytes), "readme.txt", null);
    Assert.That(Encoding.ASCII.GetString(extracted), Is.EqualTo("WORM\n"));
  }

  // ── HappyPath round-trips ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile_PreservesNameAndBytes() {
    var payload = "Greetings from Xbox!\n"u8.ToArray();
    var image = Build(("hello.txt", payload));
    var entries = Read(image);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(entries[0].IsDirectory, Is.False);
    Assert.That(Extract(image, "hello.txt"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles_PreservesAllBytes() {
    var a = Encoding.ASCII.GetBytes("alpha");
    var b = Encoding.ASCII.GetBytes("bravo charlie");
    var c = new byte[300];
    for (var i = 0; i < c.Length; i++) c[i] = (byte)(i & 0xFF);

    var image = Build(("a.txt", a), ("b.dat", b), ("c.bin", c));
    var entries = Read(image);

    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "a.txt", "b.dat", "c.bin" }));
    Assert.That(Extract(image, "a.txt"), Is.EqualTo(a));
    Assert.That(Extract(image, "b.dat"), Is.EqualTo(b));
    Assert.That(Extract(image, "c.bin"), Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_NestedDirectories_BuildsHierarchy() {
    var image = Build(
      ("readme.txt", "root"u8.ToArray()),
      ("games/halo/save.bin", "halo-save"u8.ToArray()),
      ("games/halo/profile.dat", "halo-profile"u8.ToArray()),
      ("games/forza/garage.dat", "forza-garage"u8.ToArray()));

    var entries = Read(image);
    var fileNames = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();

    Assert.That(fileNames, Does.Contain("readme.txt"));
    Assert.That(fileNames, Does.Contain("games/halo/save.bin"));
    Assert.That(fileNames, Does.Contain("games/halo/profile.dat"));
    Assert.That(fileNames, Does.Contain("games/forza/garage.dat"));

    // Directory entries must also be present so future R/W passes can find
    // existing subdirectories without re-deriving them from path parts.
    var dirNames = entries.Where(e => e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(dirNames, Does.Contain("games"));
    Assert.That(dirNames, Does.Contain("games/halo"));
    Assert.That(dirNames, Does.Contain("games/forza"));

    Assert.That(Extract(image, "games/halo/save.bin"),
      Is.EqualTo("halo-save"u8.ToArray()));
    Assert.That(Extract(image, "games/forza/garage.dat"),
      Is.EqualTo("forza-garage"u8.ToArray()));
  }

  // ── Boundary cases ────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Build_EmptyInput_ProducesValidImage() {
    var image = new FatxWriter().Build();
    Assert.That(image.Length, Is.GreaterThan(0x1000));
    var entries = Read(image);
    Assert.That(entries, Is.Empty);
  }

  [Test, Category("Boundary")]
  public void RoundTrip_LargeFile_SpansMultipleClusters() {
    // 64 KiB payload at 2 KiB cluster size = 32 clusters of one chain.
    var payload = new byte[64 * 1024];
    var rng = new Random(0xFA7);
    rng.NextBytes(payload);

    var image = Build(("big.bin", payload));
    Assert.That(Extract(image, "big.bin"), Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void RoundTrip_NameAtMaxLength_PreservesExactly42Chars() {
    var name = new string('a', 42); // exactly the 42-char limit
    var payload = "max"u8.ToArray();
    var image = Build((name, payload));
    var entries = Read(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name.Length, Is.EqualTo(42));
    Assert.That(entries[0].Name, Is.EqualTo(name));
  }

  [Test, Category("Boundary")]
  public void RoundTrip_FileAtClusterBoundary_NoSlackBytes() {
    // Build a payload whose length matches the writer's default cluster
    // (the writer picks 4 sectors = 2 KiB for tiny images). The extracted
    // bytes must be the same length, not the rounded-up cluster size.
    var payload = new byte[2048];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i ^ 0x55);
    var image = Build(("aligned.bin", payload));

    var entries = Read(image);
    Assert.That(entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(Extract(image, "aligned.bin"), Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void RoundTrip_ZeroByteFile_ZeroSizeInDirent() {
    var image = Build(("empty.txt", []));
    var entries = Read(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Size, Is.EqualTo(0));
    Assert.That(Extract(image, "empty.txt"), Is.Empty);
  }

  // ── Sad path: name sanitisation ───────────────────────────────────────

  [Test, Category("Sad")]
  public void Build_NameLongerThanLimit_TruncatedWithTildeOne() {
    // 50-char name → truncated to 42, last two chars become "~1".
    var name = new string('x', 50);
    var image = Build((name, "data"u8.ToArray()));
    var entries = Read(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name.Length, Is.EqualTo(42));
    Assert.That(entries[0].Name, Does.EndWith("~1"));
  }

  [Test, Category("Sad")]
  public void Build_ControlCharsInName_ReplacedWithUnderscore() {
    // Tab + DEL + form-feed are outside [0x20..0x7E]; writer maps them to '_'.
    // Use \u-form for DEL so it doesn't merge with the trailing hex digit
    // (C# parses \x as 1-4 hex digits; \x7Fc would be U+07FC, not DEL + 'c').
    var image = Build(("a\tb\u007Fc.txt", "x"u8.ToArray()));
    var entries = Read(image);
    Assert.That(entries[0].Name, Is.EqualTo("a_b_c.txt"));
  }
}
