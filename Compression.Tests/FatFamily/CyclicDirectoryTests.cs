using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.FatFamily;

/// <summary>
/// A damaged directory tree - a subdirectory whose start cluster points back at an ancestor or at
/// itself - must not take the reader down by recursing until the stack overflows (as it did for
/// FAT in #805). The same check for the FAT+ and FATX readers, which walk their trees the same way.
/// </summary>
[TestFixture]
public sealed class CyclicDirectoryTests {
  private static byte[] Tree<TWriter>(Func<TWriter> create, Action<TWriter, string, byte[]> add, Func<TWriter, byte[]> build) {
    var w = create();
    add(w, "readme.txt", "root"u8.ToArray());
    add(w, "docs/guide.txt", "guide"u8.ToArray());
    add(w, "docs/inner/deep.txt", "deep"u8.ToArray());
    return build(w);
  }

  // ── FAT+ (FAT32-style 32-byte entries: start cluster low word at 26, high word at 20) ──

  private static byte[] FatPlusCycle(string target) {
    var disk = Tree(() => new FileSystem.FatPlus.FatPlusWriter(), static (w, n, d) => w.AddFile(n, d), static w => w.BuildAutoSized());
    int docs, inner;
    using (var r = new FileSystem.FatPlus.FatPlusReader(new MemoryStream(disk))) {
      docs = r.Entries.Single(e => e.Name == "DOCS").StartCluster;
      inner = r.Entries.Single(e => e.Name == "DOCS/INNER").StartCluster;
    }
    var to = target == "self" ? inner : docs;
    var name = Encoding.ASCII.GetBytes("INNER      ");
    var patched = 0;
    for (var off = 0; off + 32 <= disk.Length; off += 32)
      if (disk.AsSpan(off, 11).SequenceEqual(name) && (disk[off + 11] & 0x10) != 0) {
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off + 26), (ushort)to);
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off + 20), (ushort)(to >> 16));
        ++patched;
      }
    Assert.That(patched, Is.EqualTo(1));
    return disk;
  }

  [TestCase("parent")]
  [TestCase("self")]
  public void GivenAFatPlusDirectoryPointingIntoItsAncestry_WhenRead_ThenTheReaderTerminates(string target) {
    using var r = new FileSystem.FatPlus.FatPlusReader(new MemoryStream(FatPlusCycle(target)));
    var names = r.Entries.Select(e => e.Name).ToList();   // FAT+ short names are stored in upper case
    Assert.That(names, Does.Contain("README.TXT").And.Contain("DOCS/GUIDE.TXT").And.Contain("DOCS/INNER"));
    Assert.That(names.Count, Is.LessThan(50), "the cycle must not be expanded");
  }

  // ── FATX (name length, attributes, name; first cluster at 0x2C of a 64-byte record) ──

  private static byte[] FatxCycle(string target) {
    var disk = Tree(() => new FileSystem.Fatx.FatxWriter(), static (w, n, d) => w.AddFile(n, d), static w => w.Build());
    uint docs, inner;
    using (var r = new FileSystem.Fatx.FatxReader(new MemoryStream(disk))) {
      docs = r.Entries.Single(e => e.Name == "docs").FirstCluster;
      inner = r.Entries.Single(e => e.Name == "docs/inner").FirstCluster;
    }
    var patched = 0;
    for (var off = 0; off + 64 <= disk.Length; off += 64)
      if (disk[off] == 5 && (disk[off + 1] & 0x10) != 0 && disk.AsSpan(off + 2, 5).SequenceEqual("inner"u8)) {
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 0x2C), target == "self" ? inner : docs);
        ++patched;
      }
    Assert.That(patched, Is.EqualTo(1));
    return disk;
  }

  [TestCase("parent")]
  [TestCase("self")]
  public void GivenAFatxDirectoryPointingIntoItsAncestry_WhenRead_ThenTheReaderTerminates(string target) {
    using var r = new FileSystem.Fatx.FatxReader(new MemoryStream(FatxCycle(target)));
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("readme.txt").And.Contain("docs/guide.txt").And.Contain("docs/inner"));
    Assert.That(names.Count, Is.LessThan(50), "the cycle must not be expanded");
  }

  [Test]
  public void GivenDeepButValidTrees_WhenRead_ThenEveryLevelIsListed() {
    var path = string.Join('/', Enumerable.Range(0, 12).Select(i => $"d{i}")) + "/leaf.txt";
    var fp = new FileSystem.FatPlus.FatPlusWriter();
    fp.AddFile(path, "x"u8.ToArray());
    using (var r = new FileSystem.FatPlus.FatPlusReader(new MemoryStream(fp.BuildAutoSized())))
      Assert.That(r.Entries.Any(e => e.Name == path.ToUpperInvariant()), Is.True, "FAT+");
    var fx = new FileSystem.Fatx.FatxWriter();
    fx.AddFile(path, "x"u8.ToArray());
    using (var r = new FileSystem.Fatx.FatxReader(new MemoryStream(fx.Build())))
      Assert.That(r.Entries.Any(e => e.Name == path), Is.True, "FATX");
  }
}
