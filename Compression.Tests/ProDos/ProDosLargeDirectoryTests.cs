using System.Text;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// A ProDOS directory is a chain of 512-byte blocks linked by the prev/next
/// pointer pair at the start of each block. The volume directory header (0xF)
/// or subdirectory header (0xE) occupies the first slot of the first block;
/// the remaining slots hold 0x27-byte entries, thirteen per block. When a
/// directory accumulates more children than its current block chain can hold,
/// the writer must append further blocks to the chain (updating prev/next links
/// and the volume bitmap) so the directory can hold hundreds of entries.
/// </summary>
[TestFixture]
public class ProDosLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void Subdirectory_WithHundredsOfEntries_RoundTripsAllChildren() {
    // 300 children comfortably overflows a single 512-byte block (12 usable
    // slots) and forces the subdirectory's block chain to grow to ~24 blocks.
    const int count = 300;
    var w = new ProDosWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < count; i++) {
      var name = $"DOCS/FILE{i:D4}";
      var data = Encoding.ASCII.GetBytes($"CONTENT FOR ENTRY {i}");
      w.AddFile(name, data);
      expected[$"DOCS/FILE{i:D4}"] = data;
    }

    // The 5.25" floppy (280 blocks) cannot hold 300 key blocks; use the 800 KB disk.
    var img = w.Build("BIGDIR", ProDosWriter.Disk800KTotalBlocks);

    using var r = new ProDosReader(new MemoryStream(img));

    var byPath = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count), "every child file is present");

    foreach (var (path, data) in expected) {
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
      Assert.That(byPath[path], Is.EqualTo(data), $"{path} content intact");
    }
  }

  [Test, Category("RoundTrip")]
  public void Subdirectory_EntriesSpanningManyBlocks_PreserveContentAtEachBlockBoundary() {
    // 400 entries cross ~31 block boundaries (13 entries/block). Spot-check the
    // first, a mid-chain, and the last entry to prove the block chain is read in
    // order with correct content rather than just counted.
    const int count = 400;
    var w = new ProDosWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < count; i++) {
      var name = $"BIG/E{i:D4}";
      var data = Encoding.ASCII.GetBytes($"ENTRY {i} PAYLOAD {new string('X', i % 40)}");
      w.AddFile(name, data);
      expected[$"BIG/E{i:D4}"] = data;
    }

    var img = w.Build("CHAINS", ProDosWriter.Disk800KTotalBlocks);

    using var r = new ProDosReader(new MemoryStream(img));

    var byPath = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count), "every child file is present across the block chain");
    foreach (var i in new[] { 0, 13, 14, count / 2, count - 1 }) {
      var path = $"BIG/E{i:D4}";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
      Assert.That(byPath[path], Is.EqualTo(expected[path]), $"{path} content intact");
    }
  }
}
