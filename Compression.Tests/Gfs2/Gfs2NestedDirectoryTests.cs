using Compression.Registry;
using FileSystem.Gfs2;
using System.Text;

namespace Compression.Tests.Gfs2;

[TestFixture]
public class Gfs2NestedDirectoryTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + seed * 17 + i / 4096);
    return data;
  }

  [Test, Category("RoundTrip")]
  public void WriterAndReader_NestedStuffedDirectories_PreservePathsAndPayloads() {
    var small = Payload(1, 37);
    var indirect = Payload(2, 9_000);
    var writer = new Gfs2Writer();
    writer.AddFile("etc/app/config.bin", small);
    writer.AddFile("var\\lib\\data.bin", indirect);

    using var image = new MemoryStream(writer.Build());
    using var reader = new Gfs2Reader(image);

    Assert.Multiple(() => {
      Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.Name), Is.EquivalentTo(new[] {
        "etc", "etc/app", "var", "var/lib",
      }));
      Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name), Is.EquivalentTo(new[] {
        "etc/app/config.bin", "var/lib/data.bin",
      }));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "etc/app/config.bin")), Is.EqualTo(small));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "var/lib/data.bin")), Is.EqualTo(indirect));
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_CreateExtractAndModify_PreserveNestedPaths() {
    var descriptor = new Gfs2FormatDescriptor();
    var original = Payload(3, 8_000);
    var added = Payload(4, 129);
    using var image = new MemoryStream();

    descriptor.Create(image,
      [ArchiveInputInfo.InMemory("assets/images/original.bin", original)],
      new FormatCreateOptions());

    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("assets/generated/added.bin", added)]);

    image.Position = 0;
    var names = descriptor.List(image, null).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("assets"));
      Assert.That(names, Does.Contain("assets/images"));
      Assert.That(names, Does.Contain("assets/images/original.bin"));
      Assert.That(names, Does.Contain("assets/generated"));
      Assert.That(names, Does.Contain("assets/generated/added.bin"));
    });

    var outputDir = Path.Combine(Path.GetTempPath(), "gfs2_nested_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDir);
    try {
      image.Position = 0;
      descriptor.Extract(image, outputDir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(outputDir, "assets", "images", "original.bin")), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(Path.Combine(outputDir, "assets", "generated", "added.bin")), Is.EqualTo(added));
      });
    } finally {
      try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsNonCanonicalOrTraversalPaths() {
    foreach (var path in new[] { "../escape.bin", "a/../escape.bin", "/absolute.bin", "a//b.bin", "a/./b.bin" }) {
      var writer = new Gfs2Writer();
      writer.AddFile(path, [1, 2, 3]);
      Assert.That(() => writer.Build(), Throws.InstanceOf<InvalidDataException>(), path);
    }
  }

  [Test, Category("RoundTrip")]
  public void Writer_DirectoryPastStuffedCapacity_PromotesToExHash() {
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var writer = new Gfs2Writer();
    for (var i = 0; i < 96; ++i) {
      var path = $"crowded/file-{i:D2}-{new string('x', 32)}.bin";
      var payload = Payload(i, 3 + i % 13);
      expected.Add(path, payload);
      writer.AddFile(path, payload);
    }

    using var image = new MemoryStream(writer.Build());
    using var reader = new Gfs2Reader(image);
    var files = reader.Entries.Where(static e => !e.IsDirectory)
      .ToDictionary(static e => e.Name, StringComparer.Ordinal);

    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (path, payload) in expected)
      Assert.That(reader.Extract(files[path]), Is.EqualTo(payload), path);
  }

  [Test, Category("Boundary")]
  public void Writer_ExHashCollisionSplit_GrowsHashTableIntoJournalDataBlocks() {
    var names = FindNamesWithSameTopHashByte(72);
    var writer = new Gfs2Writer(sizeBytes: 64L * 1024 * 1024);
    foreach (var name in names)
      writer.AddFile($"collision/{name}", Encoding.ASCII.GetBytes(name));

    var bytes = writer.Build();
    using var image = new MemoryStream(bytes);
    using var reader = new Gfs2Reader(image);
    var files = reader.Entries.Where(static e => !e.IsDirectory).ToArray();

    Assert.That(files.Select(static e => e.Name),
      Is.EquivalentTo(names.Select(static name => $"collision/{name}")));
    foreach (var entry in files) {
      var leafName = entry.Name[(entry.Name.LastIndexOf('/') + 1)..];
      Assert.That(reader.Extract(entry), Is.EqualTo(Encoding.ASCII.GetBytes(leafName)), entry.Name);
    }
  }

  private static string[] FindNamesWithSameTopHashByte(int count) {
    var groups = new Dictionary<byte, List<string>>();
    for (var i = 0; i < 100_000; ++i) {
      var name = $"hash-{i:D6}-{i * 2654435761u:X8}.bin";
      var hash = Crc32(Encoding.ASCII.GetBytes(name));
      var prefix = (byte)(hash >> 24);
      if (!groups.TryGetValue(prefix, out var list))
        groups[prefix] = list = [];
      list.Add(name);
      if (list.Count == count)
        return [.. list];
    }

    throw new InvalidOperationException($"Could not find {count} names sharing one GFS2 hash prefix.");
  }

  private static uint Crc32(ReadOnlySpan<byte> bytes) {
    var crc = 0xFFFF_FFFFu;
    foreach (var value in bytes) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 1) != 0 ? 0xEDB8_8320u ^ (crc >> 1) : crc >> 1;
    }
    return crc ^ 0xFFFF_FFFFu;
  }
}
