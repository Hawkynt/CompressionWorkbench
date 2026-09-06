using System.Text;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Large-directory support for the ReiserFS writer. A single directory holding
/// many entries cannot fit inside one 4 KiB leaf block: both the directory item
/// (the reiserfs_de_head array plus packed names) and the per-file stat-data /
/// direct items overflow it. The writer must therefore grow a real S+tree —
/// formatting several leaf blocks and an internal block that points at them via
/// disk_child pointers — and the reader must descend that internal block to
/// reach every leaf. Each file must round-trip at its full path with its content
/// intact.
/// </summary>
[TestFixture]
public class ReiserFsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_RoundTripsThroughReader() {
    const int fileCount = 1000;

    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < fileCount; i++) {
      var path = $"dir/file{i:D4}";
      var payload = Encoding.ASCII.GetBytes($"content-{i:D4}");
      w.AddFile(path, payload);
      expected[path] = payload;
    }

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath, Has.Count.EqualTo(fileCount),
      "every file in the large directory must be surfaced exactly once");

    Assert.Multiple(() => {
      foreach (var (path, _) in expected)
        Assert.That(byPath.ContainsKey(path), Is.True, $"file present at its path: {path}");
    });

    // Spot-check content across the range (first, last, a few middles).
    foreach (var i in new[] { 0, 1, 250, 499, 500, 750, 998, 999 }) {
      var path = $"dir/file{i:D4}";
      Assert.That(byPath[path], Is.EqualTo(expected[path]), $"content intact for {path}");
    }

    // The containing directory must surface as a real directory object.
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs, Does.Contain("dir"), "containing directory present");
  }

  /// <summary>
  /// A directory whose entries need more than one DIRENTRY item must spread
  /// those items over as many leaves.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The kernel never puts two directory items of one directory into a single
  /// node — they are mergeable, so balancing would have fused them — and
  /// reiserfsck encodes that as a hard rule: inside a leaf, the left neighbour
  /// of a DIRENTRY or INDIRECT item has to be the stat-data of the very same
  /// object (reiserfsprogs check_tree.c:bad_pair). Only the leaf's first item is
  /// unconstrained.
  /// </para>
  /// <para>
  /// The greedy leaf packer used to ignore that. Whenever a directory's first
  /// DIRENTRY chunk happened to start a leaf, the short remainder chunk fitted
  /// behind it and <c>fsck.reiserfs</c> rejected the volume with
  /// "bad_leaf: … The wrong order of items: [2 109 0x1 DIR (3)],
  /// [2 109 0x2d7e6a80 DIR (3)]" — both keys of one directory, both ascending,
  /// yet fatal. Our own reader merged the chunks happily, so nothing but the
  /// on-disk layout gives the bug away; the check below therefore walks the
  /// S+tree instead of trusting the entry list.
  /// </para>
  /// <para>
  /// The padding files at the volume root exist only to shift the packing — with
  /// none of them the split lands on a leaf boundary by luck and the volume was
  /// valid even before the fix.
  /// </para>
  /// </remarks>
  [Test, Category("RoundTrip")]
  public void SplitDirectoryItemsDoNotShareALeaf() {
    const int fileCount = 120;
    const int padCount = 9;

    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var rng = new Random(99);
    for (var i = 0; i < padCount; i++) {
      var path = $"pad{i:D3}.bin";
      var payload = new byte[rng.Next(1, 900)];
      rng.NextBytes(payload);
      w.AddFile(path, payload);
      expected[path] = payload;
    }
    for (var i = 0; i < fileCount; i++) {
      var path = $"many/f{i:D4}.bin";
      var payload = new byte[rng.Next(1, 900)];
      rng.NextBytes(payload);
      w.AddFile(path, payload);
      expected[path] = payload;
    }

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();
    ms.Position = 0;

    // ReiserFsEntry.Size stays 0 until a body is read, so the sizes come from
    // the extracted bodies themselves.
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath, Has.Count.EqualTo(expected.Count),
      "every file must be listed exactly once across the split directory items");
    Assert.Multiple(() => {
      foreach (var (path, payload) in expected) {
        Assert.That(byPath.ContainsKey(path), Is.True, $"file present at its path: {path}");
        Assert.That(byPath.GetValueOrDefault(path)?.Length, Is.EqualTo(payload.Length),
          $"size intact for {path}");
      }
    });

    // The directory really has to be split, or the rest of the test proves nothing.
    var leaves = LeafBlocks(image).ToList();
    var direntryItems = new Dictionary<(uint DirId, uint ObjectId), int>();
    foreach (var leaf in leaves)
      foreach (var item in ItemsOf(image, leaf))
        if (item.Type == TypeDirentry)
          direntryItems[(item.DirId, item.ObjectId)] =
            direntryItems.GetValueOrDefault((item.DirId, item.ObjectId)) + 1;

    Assert.That(direntryItems.Values, Has.Some.GreaterThan(1),
      "no directory needed several DIRENTRY items — the test input is too small");

    // reiserfsck's rule: only the first item of a leaf may follow a foreign key.
    foreach (var leaf in leaves) {
      var items = ItemsOf(image, leaf).ToList();
      for (var i = 1; i < items.Count; i++) {
        if (items[i].Type == TypeStatData) continue;
        var previous = items[i - 1];
        Assert.That(
          previous.Type == TypeStatData
          && previous.DirId == items[i].DirId
          && previous.ObjectId == items[i].ObjectId,
          Is.True,
          $"leaf {leaf} item {i} (type {items[i].Type}, key {items[i].DirId}/{items[i].ObjectId}) " +
          $"follows a foreign item (type {previous.Type}, key {previous.DirId}/{previous.ObjectId})");
      }
    }
  }

  private const int BlockSize = 4096;
  private const int BlockHeadSize = 24;
  private const int ItemHeaderSize = 24;
  private const int SuperblockOffset = 65536;
  private const int TypeStatData = 0;
  private const int TypeDirentry = 3;

  /// <summary>
  /// Descends the S+tree from <c>s_root_block</c> and yields every leaf block
  /// number, so the checks below see exactly the blocks reiserfsck would.
  /// </summary>
  private static IEnumerable<uint> LeafBlocks(byte[] image) {
    var pending = new Stack<uint>();
    pending.Push(BitConverter.ToUInt32(image, SuperblockOffset + 8)); // s_root_block
    while (pending.Count > 0) {
      var block = pending.Pop();
      var offset = (int)block * BlockSize;
      var level = BitConverter.ToUInt16(image, offset);
      var count = BitConverter.ToUInt16(image, offset + 2);
      if (level == 1) {
        yield return block;
        continue;
      }
      // Internal node: `count` keys of 16 bytes, then `count + 1` disk_child.
      var pointers = offset + BlockHeadSize + count * 16;
      for (var i = 0; i <= count; i++)
        pending.Push(BitConverter.ToUInt32(image, pointers + i * 8));
    }
  }

  /// <summary>
  /// Yields a leaf's item keys in on-disk order. The item type lives in the
  /// uniqueness word for v3.5 keys and in the top 4 bits of the 64-bit offset
  /// for v3.6 keys, so it is decoded per item_head's ih_key_format.
  /// </summary>
  private static IEnumerable<(uint DirId, uint ObjectId, int Type)> ItemsOf(byte[] image, uint block) {
    var offset = (int)block * BlockSize;
    var count = BitConverter.ToUInt16(image, offset + 2);
    for (var i = 0; i < count; i++) {
      var ih = offset + BlockHeadSize + i * ItemHeaderSize;
      var type = BitConverter.ToUInt16(image, ih + 22) == 0
        ? BitConverter.ToUInt32(image, ih + 12) switch {
          0u => 0,           // V1_SD_UNIQUENESS
          0xFFFFFFFEu => 1,  // V1_INDIRECT_UNIQUENESS
          0xFFFFFFFFu => 2,  // V1_DIRECT_UNIQUENESS
          500u => 3,         // V1_DIRENTRY_UNIQUENESS
          _ => -1,
        }
        : (int)(BitConverter.ToUInt64(image, ih + 8) >> 60);
      yield return (BitConverter.ToUInt32(image, ih), BitConverter.ToUInt32(image, ih + 4), type);
    }
  }
}
