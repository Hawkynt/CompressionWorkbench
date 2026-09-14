using System.Buffers.Binary;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

/// <summary>
/// Clean-room tests for the classic 128-byte WAFL inode block-tree mechanics
/// published by NetApp. These fixtures deliberately supply level and logical
/// size out-of-band; their on-disk metadata offsets are not guessed.
/// </summary>
[TestFixture]
public class WaflClassicBlockTreeTests {

  private static byte[] BuildInode(bool littleEndian = false, params uint[] pointers) {
    var inode = new byte[WaflClassicBlockTree.InodeSize];
    for (var i = 0; i < pointers.Length; ++i)
      WriteUInt32(inode.AsSpan(WaflClassicBlockTree.InodeMetadataSize + i * sizeof(uint), sizeof(uint)), pointers[i], littleEndian);
    return inode;
  }

  private static byte[] BuildPointerBlock(bool littleEndian = false, params uint[] pointers) {
    var block = new byte[WaflReader.BlockSize];
    for (var i = 0; i < pointers.Length; ++i)
      WriteUInt32(block.AsSpan(i * sizeof(uint), sizeof(uint)), pointers[i], littleEndian);
    return block;
  }

  private static void WriteUInt32(Span<byte> target, uint value, bool littleEndian) {
    if (littleEndian)
      BinaryPrimitives.WriteUInt32LittleEndian(target, value);
    else
      BinaryPrimitives.WriteUInt32BigEndian(target, value);
  }

  private static (long FileBlock, uint? Vbn)[] Project(IEnumerable<WaflClassicDataBlock> blocks)
    => blocks.Select(block => (block.FileBlockNumber, block.Vbn)).ToArray();

  [Test, Category("HappyPath")]
  public void InlineData_ComesFromThePublishedFinal64ByteArea() {
    var inode = new byte[WaflClassicBlockTree.InodeSize];
    for (var i = 0; i < WaflClassicBlockTree.InlineDataCapacity; ++i)
      inode[WaflClassicBlockTree.InodeMetadataSize + i] = (byte)(0x80 + i);

    var actual = WaflClassicBlockTree.ReadInlineData(inode, 17);

    Assert.That(actual, Is.EqualTo(inode.AsSpan(WaflClassicBlockTree.InodeMetadataSize, 17).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Level1_PreservesDirectPointerOrderAndSparseHoles() {
    var inode = BuildInode(false, 7, 0, 9);

    var actual = Project(WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 1,
      littleEndian: false,
      logicalBlockCount: 3,
      volumeBlockCount: 64,
      _ => throw new AssertionException("Level 1 must not read an indirect block.")));

    Assert.That(actual, Is.EqualTo(new (long, uint?)[] { (0, 7), (1, null), (2, 9) }));
  }

  [Test, Category("HappyPath")]
  public void Level2_FollowsSingleIndirectBlocksAndStopsAtLogicalSize() {
    var inode = BuildInode(false, 20, 21);
    var indirect = new Dictionary<uint, byte[]> {
      [20] = BuildPointerBlock(false, 30, 0, 31, 32),
      [21] = BuildPointerBlock(false, 40),
    };

    var actual = Project(WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 2,
      littleEndian: false,
      logicalBlockCount: 3,
      volumeBlockCount: 64,
      vbn => indirect[vbn]));

    Assert.That(actual, Is.EqualTo(new (long, uint?)[] { (0, 30), (1, null), (2, 31) }));
  }

  [Test, Category("HappyPath")]
  public void Level2_AcceptsLittleEndianPointers() {
    var inode = BuildInode(true, 20);
    var indirect = new Dictionary<uint, byte[]> {
      [20] = BuildPointerBlock(true, 30, 31),
    };

    var actual = Project(WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 2,
      littleEndian: true,
      logicalBlockCount: 2,
      volumeBlockCount: 64,
      vbn => indirect[vbn]));

    Assert.That(actual, Is.EqualTo(new (long, uint?)[] { (0, 30), (1, 31) }));
  }

  [Test, Category("HappyPath")]
  public void Level3_FollowsDoubleIndirectTree() {
    var inode = BuildInode(false, 20);
    var indirect = new Dictionary<uint, byte[]> {
      [20] = BuildPointerBlock(false, 21),
      [21] = BuildPointerBlock(false, 30, 0, 31),
    };

    var actual = Project(WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 3,
      littleEndian: false,
      logicalBlockCount: 3,
      volumeBlockCount: 64,
      vbn => indirect[vbn]));

    Assert.That(actual, Is.EqualTo(new (long, uint?)[] { (0, 30), (1, null), (2, 31) }));
  }

  [Test, Category("HappyPath")]
  public void ZeroIndirectPointer_ExpandsToSparseLogicalRangeWithoutReads() {
    var inode = BuildInode(false, 0);

    var actual = Project(WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 3,
      littleEndian: false,
      logicalBlockCount: 4,
      volumeBlockCount: 64,
      _ => throw new AssertionException("Sparse subtrees must not be read.")));

    Assert.That(actual, Is.EqualTo(new (long, uint?)[] { (0, null), (1, null), (2, null), (3, null) }));
  }

  [Test, Category("Sad")]
  public void TraversalRejectsOutOfRangeDirectVbn() {
    var inode = BuildInode(false, 64);
    var sequence = WaflClassicBlockTree.EnumerateDataBlocks(inode, 1, false, 1, 64, _ => []);

    Assert.Throws<InvalidDataException>(() => _ = sequence.ToArray());
  }

  [Test, Category("Sad")]
  public void TraversalRejectsTruncatedIndirectBlock() {
    var inode = BuildInode(false, 20);
    var sequence = WaflClassicBlockTree.EnumerateDataBlocks(inode, 2, false, 1, 64, _ => new byte[1024]);

    Assert.Throws<InvalidDataException>(() => _ = sequence.ToArray());
  }

  [Test, Category("Sad")]
  public void TraversalRejectsIndirectCycles() {
    var inode = BuildInode(false, 20);
    var cycle = BuildPointerBlock(false, 20);
    var sequence = WaflClassicBlockTree.EnumerateDataBlocks(inode, 3, false, 1, 64, _ => cycle);

    Assert.Throws<InvalidDataException>(() => _ = sequence.ToArray());
  }

  [Test, Category("Sad")]
  public void TraversalRejectsLogicalSizeBeyondLevelCapacity() {
    var inode = BuildInode(false);

    Assert.Throws<InvalidDataException>(() => _ = WaflClassicBlockTree.EnumerateDataBlocks(
      inode,
      level: 1,
      littleEndian: false,
      logicalBlockCount: WaflClassicBlockTree.RootPointerCount + 1,
      volumeBlockCount: 64,
      _ => []));
  }

  [Test, Category("Sad")]
  public void Level0RejectsExternalLogicalBlocks() {
    var inode = BuildInode(false);

    Assert.Throws<InvalidDataException>(() => _ = WaflClassicBlockTree.EnumerateDataBlocks(inode, 0, false, 1, 64, _ => []));
  }

  [Test, Category("Sad")]
  public void DecoderRequiresExactly128BytesForClassicInode() {
    Assert.Throws<ArgumentException>(() => _ = WaflClassicBlockTree.ReadInlineData(new byte[127], 1));
  }
}
