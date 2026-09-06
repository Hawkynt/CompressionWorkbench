using System.Text;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Structural checks on an S+tree deep enough to need more than one internal
/// level. One internal node indexes at most 170 children at a 4 KiB blocksize,
/// so a volume past roughly 4 800 small files grows a second internal level and
/// <c>s_tree_height</c> reaches 4.
/// </summary>
/// <remarks>
/// <para>
/// ReiserFS counts node levels UP from the leaves: leaves are
/// <c>DISK_LEAF_NODE_LEVEL</c> = 1, the internal level directly above them is 2,
/// and the root sits at <c>s_tree_height - 1</c>. reiserfsck recomputes the
/// expected value for a node reached at depth <c>h</c> as
/// <c>get_sb_tree_height(sb) - h - 1</c> (reiserfsprogs-3.6.27
/// fsck/check_tree.c:898) and fails the volume outright when a node disagrees
/// (check_tree.c:975, "The level of the node (%d) is not correct").
/// </para>
/// <para>
/// The writer used to stamp every internal node with level 2 whatever its
/// height, so the moment a second internal level appeared the root claimed to be
/// one level above the leaves while the superblock said otherwise, and
/// <c>reiserfsck --check</c> answered "block 8387: The level of the node (2) is
/// not correct, (3) expected" followed by "vpf-10640: The on-disk and the
/// correct bitmaps differs" — the whole subtree having been skipped.
/// </para>
/// <para>
/// The checks below descend from <c>s_root_block</c> exactly as
/// <c>pass_through_tree</c> does, so they see what reiserfsck sees without the
/// tool having to be installed. The delimiting-key rules mirror
/// check_tree.c:bad_path: a leaf's left delimiting key — the nearest key to its
/// left anywhere up the path — must EQUAL the leaf's first item key, and its
/// right delimiting key must be strictly GREATER than the leaf's last item key.
/// </para>
/// </remarks>
[TestFixture]
public class ReiserFsDeepTreeTests {

  private const int BlockSize = 4096;
  private const int BlockHeadSize = 24;
  private const int ItemHeaderSize = 24;
  private const int KeySize = 16;
  private const int DiskChildSize = 8;
  private const int SuperblockOffset = 65536;

  /// <summary>Files needed before the writer stacks a second internal level over the leaves.</summary>
  private const int DeepTreeFileCount = 5000;

  [Test, Category("HappyPath")]
  public void DeepTree_NodeLevelsCountUpFromTheLeaves() {
    var image = BuildFlatVolume(DeepTreeFileCount);
    var treeHeight = BitConverter.ToUInt16(image, SuperblockOffset + 68);

    Assert.That(treeHeight, Is.GreaterThanOrEqualTo(4),
      $"{DeepTreeFileCount} files did not produce a tree with two internal levels — " +
      "the input is too small to exercise the deep path");

    var visited = new List<(uint Block, int Depth, int Level)>();
    Walk(image, BitConverter.ToUInt32(image, SuperblockOffset + 8), 0, visited);

    Assert.Multiple(() => {
      foreach (var (block, depth, level) in visited)
        Assert.That(level, Is.EqualTo(treeHeight - depth - 1),
          $"block {block} at depth {depth} carries blk_level {level}, but a volume of " +
          $"s_tree_height {treeHeight} needs {treeHeight - depth - 1} there");
    });

    // The shape has to be a real one: a single root, at least one intermediate
    // internal level, and leaves at the bottom.
    Assert.That(visited.Count(n => n.Depth == 0), Is.EqualTo(1), "exactly one root");
    Assert.That(visited.Count(n => n.Depth == 1), Is.GreaterThan(1),
      "the second internal level must hold more than one node");
    Assert.That(visited.Where(n => n.Level == 1).Select(n => n.Depth).Distinct().Count(),
      Is.EqualTo(1), "every leaf must sit at the same depth");
  }

  [Test, Category("HappyPath")]
  public void DeepTree_DelimitingKeysBracketEveryLeaf() {
    var image = BuildFlatVolume(DeepTreeFileCount);
    var root = BitConverter.ToUInt32(image, SuperblockOffset + 8);

    var leaves = 0;
    Assert.Multiple(() => {
      foreach (var (block, path, positions) in LeafPaths(image, root)) {
        ++leaves;
        var itemCount = BitConverter.ToUInt16(image, (int)block * BlockSize + 2);
        Assert.That(itemCount, Is.GreaterThan(0), $"leaf {block} holds no items");

        var left = DelimitingKey(image, path, positions, right: false);
        if (left != null)
          Assert.That(CompareKeys(left, ItemKey(image, block, 0)), Is.EqualTo(0),
            $"leaf {block}: its left delimiting key must equal its first item's key");

        var rightKey = DelimitingKey(image, path, positions, right: true);
        if (rightKey != null)
          Assert.That(CompareKeys(rightKey, ItemKey(image, block, itemCount - 1)), Is.EqualTo(1),
            $"leaf {block}: its right delimiting key must be greater than its last item's key");

        // reiserfsck's dc_size rule (check_tree.c:1098): the parent's recorded
        // used space plus the child's free space plus the block head is the
        // whole block.
        if (path.Count > 1) {
          var parent = (int)path[^2] * BlockSize;
          var parentItems = BitConverter.ToUInt16(image, parent + 2);
          var dc = parent + BlockHeadSize + parentItems * KeySize + positions[^1] * DiskChildSize;
          var usedSpace = BitConverter.ToUInt16(image, dc + 4);
          var freeSpace = BitConverter.ToUInt16(image, (int)block * BlockSize + 4);
          Assert.That(usedSpace + freeSpace + BlockHeadSize, Is.EqualTo(BlockSize),
            $"leaf {block}: dc_size {usedSpace} disagrees with blk_free_space {freeSpace}");
        }
      }
    });

    Assert.That(leaves, Is.GreaterThan(170),
      "fewer leaves than one internal node indexes — the tree is not deep enough to prove anything");
  }

  /// <summary>
  /// Builds a volume of <paramref name="fileCount"/> small files in one
  /// directory. Small bodies keep the volume compact while still forcing a leaf
  /// every few dozen files.
  /// </summary>
  private static byte[] BuildFlatVolume(int fileCount) {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    for (var i = 0; i < fileCount; i++)
      w.AddFile($"many/file{i:D6}", Encoding.ASCII.GetBytes($"content-{i:D6}"));
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>Collects every node reached from <paramref name="block"/> with the depth it was reached at.</summary>
  private static void Walk(byte[] image, uint block, int depth, List<(uint, int, int)> sink) {
    var offset = (int)block * BlockSize;
    var level = BitConverter.ToUInt16(image, offset);
    var count = BitConverter.ToUInt16(image, offset + 2);
    sink.Add((block, depth, level));
    if (level <= 1) return;
    var pointers = offset + BlockHeadSize + count * KeySize;
    for (var i = 0; i <= count; i++)
      Walk(image, BitConverter.ToUInt32(image, pointers + i * DiskChildSize), depth + 1, sink);
  }

  /// <summary>
  /// Yields every leaf together with the root-to-leaf block path and the child
  /// position taken at each internal node — what <c>pass_through_tree</c> keeps
  /// so <c>bad_path</c> can recover the delimiting keys.
  /// </summary>
  private static IEnumerable<(uint Block, List<uint> Path, List<int> Positions)> LeafPaths(
    byte[] image, uint root) {
    var path = new List<uint>();
    var positions = new List<int>();

    IEnumerable<(uint, List<uint>, List<int>)> Descend(uint block) {
      path.Add(block);
      var offset = (int)block * BlockSize;
      var level = BitConverter.ToUInt16(image, offset);
      var count = BitConverter.ToUInt16(image, offset + 2);
      if (level <= 1) {
        yield return (block, [.. path], [.. positions]);
      } else {
        var pointers = offset + BlockHeadSize + count * KeySize;
        for (var i = 0; i <= count; i++) {
          positions.Add(i);
          foreach (var found in Descend(BitConverter.ToUInt32(image, pointers + i * DiskChildSize)))
            yield return found;
          positions.RemoveAt(positions.Count - 1);
        }
      }
      path.RemoveAt(path.Count - 1);
    }

    return Descend(root);
  }

  /// <summary>
  /// Recovers a leaf's delimiting key by walking back up the path, exactly as
  /// reiserfsprogs' <c>lkey</c> / <c>rkey</c> (fsck/check_tree.c:1021 and 1036)
  /// do: the left key is the parent key just before the child position, the
  /// right key the one at that position, and a position at either end of a node
  /// defers the question to the level above. Null means the leaf is the tree's
  /// leftmost (or rightmost) and has no delimiter.
  /// </summary>
  private static byte[]? DelimitingKey(byte[] image, List<uint> path, List<int> positions, bool right) {
    for (var h = path.Count - 1; h > 0; h--) {
      var parent = (int)path[h - 1] * BlockSize;
      var parentItems = BitConverter.ToUInt16(image, parent + 2);
      var pos = positions[h - 1];
      if (right) {
        if (pos != parentItems)
          return image[(parent + BlockHeadSize + pos * KeySize)..(parent + BlockHeadSize + (pos + 1) * KeySize)];
      } else if (pos != 0) {
        return image[(parent + BlockHeadSize + (pos - 1) * KeySize)..(parent + BlockHeadSize + pos * KeySize)];
      }
    }
    return null;
  }

  /// <summary>The 16-byte key of item <paramref name="index"/> in a leaf.</summary>
  private static byte[] ItemKey(byte[] image, uint block, int index) {
    var ih = (int)block * BlockSize + BlockHeadSize + index * ItemHeaderSize;
    return image[ih..(ih + KeySize)];
  }

  /// <summary>
  /// reiserfsprogs <c>comp_keys</c> (reiserfscore/stree.c): dir_id, then
  /// objectid, then the offset, then the type. Both are read out of the 16 key
  /// bytes alone — a top type nibble of 0 or 15 marks a v3.5 key whose offset is
  /// the 32-bit word at +8 and whose type comes from the uniqueness word at +12
  /// (node_formats.c:856, :877, :915).
  /// </summary>
  private static int CompareKeys(byte[] a, byte[] b) {
    var dirIds = BitConverter.ToUInt32(a, 0).CompareTo(BitConverter.ToUInt32(b, 0));
    if (dirIds != 0) return Math.Sign(dirIds);
    var objectIds = BitConverter.ToUInt32(a, 4).CompareTo(BitConverter.ToUInt32(b, 4));
    if (objectIds != 0) return Math.Sign(objectIds);
    var offsets = OffsetOf(a).CompareTo(OffsetOf(b));
    if (offsets != 0) return Math.Sign(offsets);
    return Math.Sign(TypeOf(a).CompareTo(TypeOf(b)));
  }

  private static ulong OffsetOf(byte[] key) {
    var raw = BitConverter.ToUInt64(key, 8);
    var typeV2 = (int)(raw >> 60);
    return typeV2 is 0 or 15 ? BitConverter.ToUInt32(key, 8) : raw & 0x0FFFFFFFFFFFFFFFUL;
  }

  private static int TypeOf(byte[] key) {
    var typeV2 = (int)(BitConverter.ToUInt64(key, 8) >> 60);
    if (typeV2 is not (0 or 15)) return typeV2;
    return BitConverter.ToUInt32(key, 12) switch {
      0u => 0,           // V1_SD_UNIQUENESS
      0xFFFFFFFEu => 1,  // V1_INDIRECT_UNIQUENESS
      0xFFFFFFFFu => 2,  // V1_DIRECT_UNIQUENESS
      500u => 3,         // V1_DIRENTRY_UNIQUENESS
      _ => 15,
    };
  }
}
