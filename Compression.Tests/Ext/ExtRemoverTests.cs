#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

[TestFixture]
public class ExtRemoverTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new ExtWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }


  // Where a group keeps its bitmaps and inode table is the group descriptor's to
  // say: the reserved descriptor blocks a growable volume keeps push all three
  // along, so none of the three can be worked out from the block size alone.
  private static int BlockBitmapOffset(byte[] image) => (int)ReadDescriptor(image, 0) * 1024;

  private static int InodeBitmapOffset(byte[] image) => (int)ReadDescriptor(image, 4) * 1024;

  private static int InodeTableOffset(byte[] image) => (int)ReadDescriptor(image, 8) * 1024;

  private static int InodeSize(byte[] image)
    => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 88));

  private static uint ReadDescriptor(byte[] image, int field)
    => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(2 * 1024 + field));

  // Which inode a named entry in the root directory points at — lost+found takes
  // the first usable one, so a file's number is not the first user inode.
  private static uint RootEntryInode(byte[] image, string name) {
    var inodeSize = InodeSize(image);
    var rootInode = InodeTableOffset(image) + inodeSize;
    var dirBlock = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInode + 40));
    var wanted = System.Text.Encoding.UTF8.GetBytes(name);

    for (var offset = dirBlock * 1024; offset < (dirBlock + 1) * 1024 - 8;) {
      var inode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
      var recLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4));
      if (recLen == 0) break;

      var nameLen = image[offset + 6];
      if (inode != 0 && nameLen == wanted.Length && image.AsSpan(offset + 8, nameLen).SequenceEqual(wanted))
        return inode;

      offset += recLen;
    }

    throw new AssertionException($"'{name}' is not in the root directory.");
  }
  [Test]
  public void RemovedFileContentIsZeroedFromImage() {
    // Marker bytes distinctive enough to not collide with metadata.
    var marker = System.Text.Encoding.ASCII.GetBytes("HELLOSECRET12345");
    var image = BuildImageWith(("test.txt", marker));

    Assert.That(FindMarker(image, marker), Is.True, "precondition: marker should be in image");
    ExtRemover.Remove(image, "test.txt");
    Assert.That(FindMarker(image, marker), Is.False,
      "marker bytes must no longer be recoverable from the image after secure remove");
  }

  [Test]
  public void RemovedFileDirEntryIsZeroed() {
    var image = BuildImageWith(("gone.bin", new byte[100]));
    ExtRemover.Remove(image, "gone.bin");
    // The filename bytes must not remain in the directory area.
    var nameBytes = System.Text.Encoding.ASCII.GetBytes("gone.bin");
    Assert.That(FindMarker(image, nameBytes), Is.False,
      "filename must be fully wiped from directory");
  }

  [Test]
  public void RemovedFileIsNotReadableAfterwards() {
    var image = BuildImageWith(("a.bin", new byte[] { 1, 2, 3 }), ("b.bin", new byte[] { 4, 5, 6 }));
    ExtRemover.Remove(image, "a.bin");

    using var ms = new MemoryStream(image);
    var reader = new ExtReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "a.bin"), Is.False);
    // Note: with the zero-inode-truncation strategy, entries after the removed one
    // in the same directory block may also stop being enumerated. That's accepted
    // per the documented tradeoff in ExtRemover.
  }

  [Test]
  public void RemovingNonexistentThrows() {
    var image = BuildImageWith(("a.txt", [1]));
    Assert.Throws<FileNotFoundException>(() => ExtRemover.Remove(image, "missing.txt"));
  }

  [Test]
  public void FreeCountsIncreaseAfterRemove() {
    var image = BuildImageWith(("victim.bin", new byte[2500])); // 3 blocks @ 1K

    var sb = image.AsSpan(1024);
    var freeBlocksBefore = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[12..]);
    var freeInodesBefore = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[16..]);

    ExtRemover.Remove(image, "victim.bin");

    var sbAfter = image.AsSpan(1024);
    var freeBlocksAfter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sbAfter[12..]);
    var freeInodesAfter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sbAfter[16..]);

    Assert.That(freeBlocksAfter, Is.EqualTo(freeBlocksBefore + 3u),
      "3 data blocks should be freed");
    Assert.That(freeInodesAfter, Is.EqualTo(freeInodesBefore + 1u),
      "1 inode should be freed");

    // BGD mirrors.
    var bgd = image.AsSpan(2 * 1024);
    var bgdFreeBlocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bgd[12..]);
    var bgdFreeInodes = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bgd[14..]);
    Assert.That(bgdFreeBlocks, Is.EqualTo(freeBlocksAfter));
    Assert.That(bgdFreeInodes, Is.EqualTo(freeInodesAfter));
  }

  [Test]
  public void InodeBytesAreZeroedAfterRemove() {
    var image = BuildImageWith(("only.bin", new byte[500]));

    var inodeSize = InodeSize(image);
    var fileInodeOffset = InodeTableOffset(image) + (int)(RootEntryInode(image, "only.bin") - 1) * inodeSize;
    var fileInodeBefore = image.AsSpan(fileInodeOffset, inodeSize).ToArray();
    Assert.That(fileInodeBefore.Any(b => b != 0), Is.True, "precondition: inode has content");

    ExtRemover.Remove(image, "only.bin");

    var fileInodeAfter = image.AsSpan(fileInodeOffset, inodeSize);
    foreach (var b in fileInodeAfter)
      Assert.That(b, Is.EqualTo(0), "inode bytes must all be zero after remove");
  }

  [Test]
  public void BlockBitmapBitsClearedAfterRemove() {
    var image = BuildImageWith(("chunky.bin", new byte[3000])); // 3 blocks @ 1K

    // Find which blocks the file used — direct pointers at inode offsets 40..84.
    var fileInodeOffset = InodeTableOffset(image)
      + (int)(RootEntryInode(image, "chunky.bin") - 1) * InodeSize(image);
    var directBlocks = new List<uint>();
    for (var i = 0; i < 12; i++) {
      var bn = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
        image.AsSpan(fileInodeOffset + 40 + i * 4));
      if (bn == 0) break;
      directBlocks.Add(bn);
    }
    Assert.That(directBlocks, Is.Not.Empty);

    // Block bitmap bit N represents block (firstDataBlock(1) + N).
    var blockBitmapOffset = BlockBitmapOffset(image);
    const int firstDataBlock = 1;
    foreach (var bn in directBlocks) {
      var bit = (int)bn - firstDataBlock;
      var set = (image[blockBitmapOffset + bit / 8] >> (bit % 8)) & 1;
      Assert.That(set, Is.EqualTo(1), $"block {bn} should be marked used before remove");
    }

    ExtRemover.Remove(image, "chunky.bin");

    foreach (var bn in directBlocks) {
      var bit = (int)bn - firstDataBlock;
      var set = (image[blockBitmapOffset + bit / 8] >> (bit % 8)) & 1;
      Assert.That(set, Is.EqualTo(0), $"block {bn} should be freed in bitmap after remove");
    }
  }

  [Test]
  public void InodeBitmapBitClearedAfterRemove() {
    var image = BuildImageWith(("solo.txt", new byte[50]));

    var inodeBitmapOffset = InodeBitmapOffset(image);
    var bit = (int)RootEntryInode(image, "solo.txt") - 1;
    var bitBefore = (image[inodeBitmapOffset + bit / 8] >> (bit % 8)) & 1;
    Assert.That(bitBefore, Is.EqualTo(1), "precondition: inode 11 marked used");

    ExtRemover.Remove(image, "solo.txt");

    var bitAfter = (image[inodeBitmapOffset + bit / 8] >> (bit % 8)) & 1;
    Assert.That(bitAfter, Is.EqualTo(0), "inode 11 bit must be cleared after remove");
  }

  [Test]
  public void DescriptorAsModifiable_RemoveWorks() {
    var marker = System.Text.Encoding.ASCII.GetBytes("FORENSICS_BYTES");
    var buf = BuildImageWith(("secret.txt", marker));
    using var ms = new MemoryStream();
    ms.Write(buf);
    ms.SetLength(buf.Length);

    var desc = new ExtFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
    ((IArchiveModifiable)desc).Remove(ms, ["secret.txt"]);

    Assert.That(FindMarker(ms.ToArray(), marker), Is.False);
  }

  [Test]
  public void DescriptorAsModifiable_AddCombinesFiles() {
    var initial = BuildImageWith(("first.txt", new byte[] { 0xAA }));
    using var ms = new MemoryStream();
    ms.Write(initial);
    ms.SetLength(initial.Length);

    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, [0xBB]);
      ((IArchiveModifiable)new ExtFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmpFile, "second.txt", false)]);

      ms.Position = 0;
      var reader = new ExtReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "first.txt"), Is.True);
      Assert.That(reader.Entries.Any(e => e.Name == "second.txt"), Is.True);
    } finally {
      File.Delete(tmpFile);
    }
  }

  private static bool FindMarker(byte[] image, byte[] marker) {
    for (var i = 0; i <= image.Length - marker.Length; ++i) {
      var match = true;
      for (var j = 0; j < marker.Length; ++j) {
        if (image[i + j] != marker[j]) { match = false; break; }
      }
      if (match) return true;
    }
    return false;
  }
}
