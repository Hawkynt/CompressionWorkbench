#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// The NTFS layout map is what the wipe and the defragmenter treat as the truth
/// about which bytes are in use: whatever it leaves out is zeroed or overwritten.
/// It used to report only each file's unnamed <c>$DATA</c>, so a directory's
/// <c>$INDEX_ALLOCATION</c>, <c>$Secure:$SDS</c> and every other non-resident
/// attribute read as free space.
/// </summary>
[TestFixture]
public class NtfsExtentMapCoverageTests {

  private static byte[] BuildSpilledDirectoryImage(out Dictionary<string, byte[]> expected) {
    var w = new NtfsWriter();
    expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < 300; i++) {
      var path = $"dir/file{i:D4}.txt";
      var content = System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}");
      w.AddFile(path, content);
      expected[path] = content;
    }
    var big = new byte[40000];
    new Random(3).NextBytes(big);
    w.AddFile("big.bin", big);
    expected["big.bin"] = big;
    return w.Build(16 * 1024 * 1024);
  }

  [Test]
  public void GivenADirectoryWhoseIndexSpilled_WhenMapped_ThenTheIndexAllocationIsReserved() {
    var image = BuildSpilledDirectoryImage(out _);
    using var ms = new MemoryStream(image);

    var extents = NtfsExtentMap.Enumerate(ms).ToList();

    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved
                                 && e.FileName != null && e.FileName.Contains("$INDEX_ALLOCATION")),
      Is.True, "the spilled directory's INDX blocks must be claimed, not left to read as free space");
  }

  [Test]
  public void GivenADirectoryWhoseIndexSpilled_WhenUnusedSpaceIsWiped_ThenEveryFileStillListsAndReadsBack() {
    var image = BuildSpilledDirectoryImage(out var expected);
    using var ms = new MemoryStream();
    ms.Write(image);

    new NtfsFormatDescriptor().WipeUnusedSpace(ms);

    ms.Position = 0;
    var reader = new NtfsReader(ms);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name.Replace('\\', '/'), e => reader.Extract(e));
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), "the wipe removed directory entries");
    foreach (var (name, data) in expected)
      Assert.That(files[name], Is.EqualTo(data), $"'{name}' changed");
  }

  [Test]
  public void GivenTwoFilesSharingALeafName_WhenClusterTipsAreWiped_ThenTheLargeOneKeepsItsTail() {
    // One small resident file and one large single-run file share the leaf
    // name; the size of the small one must never be used to trim the large one.
    var big = new byte[20000];
    new Random(11).NextBytes(big);
    var w = new NtfsWriter();
    w.AddFile("a/data.bin", "tiny"u8.ToArray());
    w.AddFile("b/data.bin", big);
    using var ms = new MemoryStream();
    ms.Write(w.Build(8 * 1024 * 1024));

    new NtfsFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true);

    ms.Position = 0;
    var reader = new NtfsReader(ms);
    var entry = reader.Entries.Single(e => e.Name.Replace('\\', '/') == "b/data.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(big));
  }
}
