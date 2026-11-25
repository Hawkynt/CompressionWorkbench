using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using Compression.Tests.Reiser4;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

/// <summary>
/// The writer and the reader against each other and against a volume the linux-tux3 kernel wrote
/// (<c>ReferenceVectors/kernel.img.gz</c>, <c>kernel-gen.sh</c>): the reader must see exactly what the
/// kernel read back, and what the writer makes must read back object for object. The kernel's own
/// judgement of the writer's volumes is in <see cref="Tux3KernelOracleTests" />.
/// </summary>
[TestFixture]
public sealed class Tux3WriterTests {

  private const long Time = 1_700_000_000_000_000_000;

  private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

  private static byte[] Random(int size, int seed) {
    var bytes = new byte[size];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static byte[] KernelVolume() {
    using var res = typeof(Tux3WriterTests).Assembly.GetManifestResourceStream("Tux3Vectors.kernel.img.gz")
      ?? throw new InvalidOperationException("kernel.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, Reiser4KernelVolumeTests.ManifestEntry> KernelManifest() {
    using var res = typeof(Tux3WriterTests).Assembly.GetManifestResourceStream("Tux3Vectors.kernel.manifest")
      ?? throw new InvalidOperationException("kernel.manifest not embedded");
    using var reader = new StreamReader(res);
    return Reiser4KernelVolumeTests.ParseManifest(reader.ReadToEnd());
  }

  /// <summary>Asserts the reader sees exactly what a linux-tux3 kernel manifest lists.</summary>
  internal static void AssertMatches(Tux3Reader reader, IReadOnlyDictionary<string, Reiser4KernelVolumeTests.ManifestEntry> manifest, string label) {
    var entries = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    var expected = manifest.Where(static p => p.Key != ".").ToDictionary(StringComparer.Ordinal);
    Assert.That(entries.Keys.Order(StringComparer.Ordinal), Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)), label + ": the namespace");
    foreach (var (path, want) in expected) {
      var node = entries[path].Node!;
      var where = $"{label}: {path}";
      Assert.That(node.Mode & 0xFFF, Is.EqualTo(want.Mode), where + " mode");
      Assert.That((node.Uid, node.Gid), Is.EqualTo((want.Uid, want.Gid)), where + " owner");
      Assert.That(node.Links, Is.EqualTo(want.Links), where + " links");
      Assert.That(node.ModifiedNanoseconds / 1_000_000_000, Is.EqualTo(want.ModifiedTime), where + " mtime");
      switch (want.Type) {
        case "directory":
          Assert.That(node.IsDirectory, Is.True, where);
          break;
        case "regular file" or "regular empty file":
          Assert.That(node.IsFile, Is.True, where);
          Assert.That(node.Size, Is.EqualTo(want.Size), where + " size");
          Assert.That(Md5(reader.Extract(entries[path])), Is.EqualTo(want.Value), where + " bytes");
          break;
        case "symbolic link":
          Assert.That(node.LinkTarget, Is.EqualTo(want.Value), where + " target");
          break;
        case "fifo":
          Assert.That(node.Mode & 0xF000, Is.EqualTo(0x1000), where);
          break;
        case "character special file":
          Assert.That((node.Mode & 0xF000, node.Device), Is.EqualTo((0x2000, want.Device)), where);
          break;
        case "block special file":
          Assert.That((node.Mode & 0xF000, node.Device), Is.EqualTo((0x6000, want.Device)), where);
          break;
        default:
          Assert.Fail($"{where}: unexpected type {want.Type}");
          break;
      }
    }
  }

  [Test, Category("HappyPath"), Category("Oracle")]
  public void GivenAVolumeTheKernelWrote_WhenRead_ThenEveryEntryIsWhatTheKernelReadBack() {
    using var reader = new Tux3Reader(new MemoryStream(KernelVolume()));
    Assert.That(reader.Tree!.ReplayedRecords, Is.GreaterThan(0), "the kernel left index changes in the log");
    Assert.Multiple(() => AssertMatches(reader, KernelManifest(), "kernel"));
  }

  /// <summary>A writer with every object kind it makes, and what each should read back as.</summary>
  internal static (Tux3Writer Writer, Dictionary<string, (string Type, byte[] Data, uint Links)> Expected) EveryKind(int maxDirectBlocks = 0x7FFF) {
    var writer = new Tux3Writer { TimeNanoseconds = Time, MaxDirectBlocks = maxDirectBlocks };
    var expected = new Dictionary<string, (string, byte[], uint)>(StringComparer.Ordinal);
    var seed = 0;
    void File(string path, byte[] data) {
      writer.AddFile(path, data);
      expected[path] = ("file", data, 1);
    }
    foreach (var size in new[] { 0, 1, 100, 4_095, 4_096, 4_097, 70_000, 300_000 }) File($"sizes/f{size}", Random(size, ++seed));
    File("names/" + new string('n', 255), Random(10, ++seed));
    File("names/x", Random(10, ++seed));
    for (var i = 0; i < 400; ++i) File($"many/m{i:D3}", Encoding.ASCII.GetBytes($"file {i}\n"));
    writer.AddDirectory("empty/inner");
    writer.AddSymlink("links/sym", "../sizes/f100");
    writer.AddHardLink("links/hard", "sizes/f4097");
    expected["sizes/f4097"] = expected["sizes/f4097"] with { Item3 = 2 };
    expected["links/hard"] = expected["sizes/f4097"];
    expected["links/sym"] = ("symlink", Encoding.ASCII.GetBytes("../sizes/f100"), 1);
    writer.AddSpecialFile("special/fifo", 0x11A4);
    writer.AddSpecialFile("special/char", 0x21A4, 1UL << 20 | 3);
    writer.AddSpecialFile("special/block", 0x61A4, 259UL << 20 | 300_000);
    expected["special/fifo"] = ("fifo", [], 1);
    expected["special/char"] = ("char", [], 1);
    expected["special/block"] = ("block", [], 1);
    foreach (var dir in new[] { "sizes", "names", "many", "empty", "empty/inner", "links", "special" }) expected[dir] = ("dir", [], 0);
    return (writer, expected);
  }

  [TestCase(0x7FFF), TestCase(1), Category("HappyPath")]
  public void GivenEveryObjectKind_WhenWrittenAndRead_ThenEachReadsBackAsWritten(int maxDirectBlocks) {
    var (writer, expected) = EveryKind(maxDirectBlocks);
    using var reader = new Tux3Reader(new MemoryStream(writer.Build()));
    var entries = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    Assert.That(entries.Keys.Order(StringComparer.Ordinal), Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)));
    Assert.Multiple(() => {
      foreach (var (path, (type, data, links)) in expected) {
        var node = entries[path].Node!;
        switch (type) {
          case "file":
            Assert.That(reader.Extract(entries[path]), Is.EqualTo(data), path);
            Assert.That(node.Links, Is.EqualTo(links), path + " links");
            break;
          case "symlink":
            Assert.That(node.LinkTarget, Is.EqualTo(Encoding.ASCII.GetString(data)), path);
            break;
          case "dir":
            Assert.That(node.IsDirectory, Is.True, path);
            break;
        }
      }
      Assert.That(entries["special/char"].Node!.Device, Is.EqualTo(1UL << 20 | 3));
      Assert.That(entries["special/block"].Node!.Device, Is.EqualTo(259UL << 20 | 300_000), "the wide device encoding");
      Assert.That(entries["empty"].Node!.Links, Is.EqualTo(3u), "two plus its subdirectory");
      Assert.That(entries["sizes/f1"].Node!.ModifiedNanoseconds, Is.EqualTo(Time));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenOwnerModeAndTime_WhenWritten_ThenTheyAreKept() {
    var writer = new Tux3Writer { TimeNanoseconds = Time };
    writer.AddFile("a/owned", [1, 2], new Tux3Writer.Metadata(0x1E9, 1234, 5678, 981_173_106_000_000_000));
    writer.AddDirectory("a", new Tux3Writer.Metadata(0x1C0, 7, 8));
    using var reader = new Tux3Reader(new MemoryStream(writer.Build()));
    var owned = reader.Entries.Single(static e => e.Name == "a/owned").Node!;
    var dir = reader.Entries.Single(static e => e.Name == "a").Node!;
    Assert.Multiple(() => {
      Assert.That((owned.Mode, owned.Uid, owned.Gid, owned.ModifiedNanoseconds), Is.EqualTo((0x81E9, 1234u, 5678u, 981_173_106_000_000_000L)));
      Assert.That((dir.Mode, dir.Uid, dir.Gid), Is.EqualTo((0x41C0, 7u, 8u)));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheDescriptor_WhenCreating_ThenTheVolumeListsAndExtractsItsInputs() {
    var descriptor = new Tux3FormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    var data = Random(9_000, 3);
    using var image = new MemoryStream();
    descriptor.Create(image, [ArchiveInputInfo.InMemory("d/file.bin", data), new ArchiveInputInfo("", "empty", IsDirectory: true)], new FormatCreateOptions());
    image.Position = 0;
    var listed = descriptor.List(image, null);
    Assert.That(listed.Select(static e => e.Name), Is.EquivalentTo(new[] { "d", "d/file.bin", "empty" }));
    image.Position = 0;
    using var reader = new Tux3Reader(image);
    Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "d/file.bin")), Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenAnEmptyWriter_WhenBuilt_ThenTheVolumeIsSixteenMebibytesWithAnEmptyRoot() {
    var image = new Tux3Writer().Build();
    using var reader = new Tux3Reader(new MemoryStream(image));
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(16 * 1024 * 1024));
      Assert.That(reader.Revision, Is.EqualTo("2015-06-01"));
      Assert.That(reader.Entries, Is.Empty);
    });
  }

  [Test, Category("Boundary")]
  public void GivenContentLargerThanTheMinimum_WhenBuilt_ThenTheVolumeGrowsToHoldIt() {
    var writer = new Tux3Writer();
    writer.AddFile("big", Random(20 * 1024 * 1024, 9));
    var image = writer.Build();
    using var reader = new Tux3Reader(new MemoryStream(image));
    Assert.That(image.Length, Is.GreaterThan(20 * 1024 * 1024));
    Assert.That(reader.Extract(reader.Entries.Single()).Length, Is.EqualTo(20 * 1024 * 1024));
  }

  [Test, Category("ErrorHandling")]
  public void GivenInvalidRequests_WhenAdding_ThenTheyAreRefused() {
    var writer = new Tux3Writer();
    writer.AddFile("a/file", [1]);
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() => writer.AddFile("a/file", [2]), "a second object at one path");
      Assert.Throws<InvalidDataException>(() => writer.AddFile("a/file/below", [2]), "a file used as a directory");
      Assert.Throws<NotSupportedException>(() => writer.AddFile("a/../b", [2]), "a '..' component");
      Assert.Throws<NotSupportedException>(() => writer.AddFile("a/" + new string('x', 256), [2]), "a 256-byte name");
      Assert.Throws<NotSupportedException>(() => writer.AddHardLink("a/link", "a"), "a hard link to a directory");
      Assert.Throws<FileNotFoundException>(() => writer.AddHardLink("a/link", "a/missing"), "a hard link to nothing");
      Assert.Throws<ArgumentException>(() => writer.AddSpecialFile("a/odd", 0x81A4), "a regular file as a special file");
    });
  }
}
