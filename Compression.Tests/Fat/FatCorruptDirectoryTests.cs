using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Fat;

/// <summary>
/// Damaged directory trees must not take the reader down: a subdirectory whose start cluster
/// points back at an ancestor (or at itself) would otherwise be descended into forever until the
/// stack overflows and kills the process (#805).
/// </summary>
[TestFixture]
public class FatCorruptDirectoryTests {

  /// <summary>A FAT image with docs/inner/deep.txt, where inner's entry is then pointed at <paramref name="target"/>'s cluster.</summary>
  private static byte[] CyclicImage(string target) {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("readme.txt", "root"u8.ToArray());
    w.AddFile("docs/guide.txt", "guide"u8.ToArray());
    w.AddFile("docs/inner/deep.txt", "deep"u8.ToArray());
    var disk = w.Build();

    int docsCluster, innerCluster;
    using (var r = new FileSystem.Fat.FatReader(new MemoryStream(disk))) {
      docsCluster = r.Entries.Single(e => e.Name == "docs").StartCluster;
      innerCluster = r.Entries.Single(e => e.Name == "docs/inner").StartCluster;
    }
    var name = Encoding.ASCII.GetBytes("INNER      ");
    var patched = 0;
    for (var off = 0; off + 32 <= disk.Length; off += 32)
      if (disk.AsSpan(off, 11).SequenceEqual(name) && (disk[off + 11] & 0x10) != 0) {
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off + 26), (ushort)(target == "self" ? innerCluster : docsCluster));
        ++patched;
      }
    Assert.That(patched, Is.EqualTo(1), "the inner directory entry should be found exactly once");
    return disk;
  }

  [TestCase("parent")]
  [TestCase("self")]
  public void GivenADirectoryPointingBackIntoItsOwnAncestry_WhenRead_ThenTheReaderTerminatesWithTheIntactEntries(string target) {
    using var r = new FileSystem.Fat.FatReader(new MemoryStream(CyclicImage(target)));
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("readme.txt").And.Contain("docs/guide.txt").And.Contain("docs/inner"));
    Assert.That(names.Count, Is.LessThan(50), "the cycle must not be expanded");
  }

  [Test]
  public void GivenADeepButValidTree_WhenRead_ThenEveryLevelIsStillListed() {
    var w = new FileSystem.Fat.FatWriter();
    var path = string.Join('/', Enumerable.Range(0, 20).Select(i => $"d{i}"));
    w.AddFile(path + "/leaf.txt", "leaf"u8.ToArray());
    using var r = new FileSystem.Fat.FatReader(new MemoryStream(w.Build()));
    Assert.That(r.Entries.Any(e => e.Name == path + "/leaf.txt"), Is.True);
  }
}
