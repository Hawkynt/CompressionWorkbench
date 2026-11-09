#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>UFS2 creation and in-place editing, read back through the reader.</summary>
[TestFixture]
public sealed class Ufs2WriterTests {

  internal static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 31 + seed + (i >> 11));
    return data;
  }

  internal static FormatCreateOptions Ufs2 => new() { FormatSpecific = new Dictionary<string, string> { ["Version"] = "UFS2" } };

  internal static FormatCreateOptions Options(string version, bool bigEndian) => new() {
    FormatSpecific = new Dictionary<string, string> { ["Version"] = version, ["ByteOrder"] = bigEndian ? "BigEndian" : "LittleEndian" },
  };

  internal static byte[] Create(IReadOnlyDictionary<string, byte[]> files, FormatCreateOptions? options = null) {
    using var ms = new MemoryStream();
    new UfsFormatDescriptor().Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(), options ?? Ufs2);
    return ms.ToArray();
  }

  // 16 KiB blocks: 196 608 bytes fill the twelve direct blocks; one more byte needs the
  // single-indirect block; 35 389 440 bytes (12 + 2048 blocks) fill that tree.
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2048)]
  [TestCase(2049)]
  [TestCase(16384)]
  [TestCase(196_608)]
  [TestCase(196_609)]
  public void GivenAFileOfSize_WhenWrittenAsUfs2_ThenTheReaderReturnsItByteExact(int size) {
    var data = Pattern(size, size % 251);
    var image = Create(new Dictionary<string, byte[]> { ["f.bin"] = data });
    using var r = new UfsReader(new MemoryStream(image));
    Assert.That(r.Version, Is.EqualTo(2));
    Assert.That(r.SuperblockOffset, Is.EqualTo(65536));
    var e = r.Entries.Single(x => x.Name == "f.bin");
    Assert.That(r.Extract(e), Is.EqualTo(data));
  }

  [Test, Category("Slow")]
  public void GivenAFileNeedingTheDoubleIndirectTree_WhenWrittenAsUfs2_ThenTheReaderReturnsItByteExact() {
    var data = Pattern(35_389_440 + 70_000, 3);
    var image = Create(new Dictionary<string, byte[]> { ["big.bin"] = data });
    using var r = new UfsReader(new MemoryStream(image));
    Assert.That(r.Extract(r.Entries.Single(x => x.Name == "big.bin")), Is.EqualTo(data));
  }

  [Test]
  public void GivenNestedFolders_WhenWrittenAsUfs2_ThenTheyAreDirectories() {
    var image = Create(new Dictionary<string, byte[]> {
      ["top.txt"] = "t"u8.ToArray(),
      ["a/b/c.txt"] = "c"u8.ToArray(),
      ["a/d.txt"] = "d"u8.ToArray(),
    });
    using var r = new UfsReader(new MemoryStream(image));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName["a"].IsDirectory, Is.True);
    Assert.That(byName["a/b"].IsDirectory, Is.True);
    Assert.That(byName.Keys.Where(k => !k.Contains('/')), Is.EquivalentTo(new[] { "top.txt", "a" }));
  }

  [Test]
  public void GivenAUfs2Volume_WhenFilesAreAddedAndRemoved_ThenTheListingFollows() {
    var image = Create(new Dictionary<string, byte[]> { ["keep.txt"] = "k"u8.ToArray() });
    using var ms = new MemoryStream();
    ms.Write(image);
    var d = new UfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("docs/new.bin", Pattern(300_000, 9))]);
    d.Add(ms, [ArchiveInputInfo.InMemory("gone.txt", "g"u8.ToArray())]);
    d.Remove(ms, ["gone.txt"]);
    ms.Position = 0;
    using var r = new UfsReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "keep.txt", "docs", "docs/new.bin" }));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "docs/new.bin")), Is.EqualTo(Pattern(300_000, 9)));
  }

  [Test]
  public void GivenAFileOnThePathOfAFolder_WhenAdded_ThenItIsRefusedAndTheImageIsUntouched() {
    var image = Create(new Dictionary<string, byte[]> { ["plain"] = [1] });
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<IOException>(() => UfsModifier.AddFile(ms, "plain/child", [2]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }
}
