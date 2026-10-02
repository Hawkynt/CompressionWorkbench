using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Removing a file in place freed only its first extent: the rest of a fragmented file stayed
/// allocated in the bitmap with no file owning it, and the free-block count fell short. These
/// tests judge every removal by the volume's own invariants — each allocated block is owned by
/// some fork or metadata structure, each owned block is allocated, and the volume header's free
/// count equals the bitmap's clear bits — and refuse what the editor cannot remove losslessly.
/// The fsck.hfsplus check of the same removal is in <see cref="HfsPlusExternalConformanceTests"/>.
/// </summary>
[TestFixture]
public sealed class HfsPlusRemoveExtentsTests {

  private const uint BlockSize = 4096;

  /// <summary>Where the file record whose catalog key names <paramref name="name"/> starts.</summary>
  internal static int FileRecordOffset(byte[] image, string name) {
    var key = new byte[2 + name.Length * 2];
    BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)name.Length);
    Encoding.BigEndianUnicode.GetBytes(name).CopyTo(key, 2);
    for (var from = 0; ;) {
      var at = image.AsSpan(from).IndexOf(key);
      Assert.That(at, Is.GreaterThanOrEqualTo(0), $"no catalog key for {name}");
      at += from;
      var keyStart = at - 6;
      if (keyStart >= 0 && BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(keyStart)) == 6 + name.Length * 2) {
        var dataOffset = keyStart + 2 + 6 + name.Length * 2;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (BinaryPrimitives.ReadInt16BigEndian(image.AsSpan(dataOffset)) == 2) return dataOffset;
      }
      from = at + 1;
    }
  }

  /// <summary>
  /// A volume with "keep.txt" and "split.bin", whose three blocks are described as three
  /// extents out of disk order: (s, 1), (s+2, 1), (s+1, 1).
  /// </summary>
  internal static byte[] FragmentedVolume(out byte[] payload) {
    payload = new byte[3 * BlockSize];
    new Random(3).NextBytes(payload);
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", "keep"u8.ToArray());
    w.AddFile("split.bin", payload);
    var image = w.Build(BlockSize);
    var record = FileRecordOffset(image, "split.bin");
    var extents = record + 88 + 16;
    var start = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(extents));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(extents + 4)), Is.EqualTo(3u), "precondition: one run of three blocks");

    // Logical block 1 now lives at s+2 and logical block 2 at s+1.
    var b1 = image.AsSpan((int)((start + 1) * BlockSize), (int)BlockSize).ToArray();
    image.AsSpan((int)((start + 2) * BlockSize), (int)BlockSize).CopyTo(image.AsSpan((int)((start + 1) * BlockSize)));
    b1.CopyTo(image, (int)((start + 2) * BlockSize));
    uint[] layout = [start, 1, start + 2, 1, start + 1, 1];
    for (var i = 0; i < layout.Length; i++)
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(extents + 4 * i), layout[i]);
    payload = [.. payload[..(int)BlockSize], .. payload[(int)(2 * BlockSize)..], .. payload[(int)BlockSize..(int)(2 * BlockSize)]];
    return image;
  }

  /// <summary>
  /// The bitmap agrees with the volume: no allocated block without an owner, no owned block left
  /// free, and freeBlocks equal to the clear bits.
  /// </summary>
  internal static void AssertBitmapConsistent(byte[] image) {
    var vh = image.AsSpan(1024);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
    var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[48..]);
    var bitmapAt = (long)BinaryPrimitives.ReadUInt32BigEndian(vh[(112 + 16)..]) * BlockSize;
    bool Allocated(long block) => (image[bitmapAt + block / 8] & (0x80 >> (int)(block % 8))) != 0;
    var clear = 0L;
    for (long b = 0; b < totalBlocks; b++) if (!Allocated(b)) clear++;

    var map = HfsPlusExtentMap.Enumerate(new MemoryStream(image)).ToList();
    // A block is owned when any structure the map names overlaps it (the alternate volume
    // header claims only its 1024 bytes of the last block).
    var owned = map.Where(e => e.FileName != "allocated (unattributed)" && e.Length > 0)
      .SelectMany(e => Enumerable.Range(0, (int)((e.Offset + e.Length - 1) / BlockSize - e.Offset / BlockSize + 1)).Select(i => e.Offset / BlockSize + i))
      .ToHashSet();
    var unowned = Enumerable.Range(0, (int)totalBlocks).Where(b => Allocated(b) && !owned.Contains(b)).ToList();
    var ownedButFree = map
      .Where(e => e.Kind == DefragBlockKind.Used || e.FileName?.EndsWith("(resource fork)", StringComparison.Ordinal) == true)
      .SelectMany(e => Enumerable.Range(0, (int)((e.Length + BlockSize - 1) / BlockSize)).Select(i => e.Offset / BlockSize + i))
      .Where(b => !Allocated(b))
      .ToList();
    Assert.Multiple(() => {
      Assert.That(unowned, Is.Empty, "allocated blocks no fork or structure owns");
      Assert.That(ownedButFree, Is.Empty, "fork blocks the bitmap calls free");
      Assert.That(freeBlocks, Is.EqualTo(clear), "volume header free count vs. bitmap");
    });
  }

  private static byte[] Remove(byte[] image, string name, bool wipe = true) {
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, name, wipe), Is.True);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenAFileInThreeExtents_WhenRemoved_ThenEveryBlockIsFreedAndTheBitmapAgrees() {
    var image = FragmentedVolume(out _);
    AssertBitmapConsistent(image);
    var freeBefore = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 48));
    var after = Remove(image, "split.bin");
    AssertBitmapConsistent(after);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 48)), Is.EqualTo(freeBefore + 3));
      Assert.That(new HfsPlusReader(new MemoryStream(after)).Entries.Select(e => e.FullPath), Is.EqualTo(new[] { "keep.txt" }));
    });
  }

  [TestCase(true, TestName = "GivenWipe_WhenAFragmentedFileIsRemoved_ThenEveryExtentIsZeroed")]
  [TestCase(false, TestName = "GivenNoWipe_WhenAFragmentedFileIsRemoved_ThenTheBlocksKeepTheirBytes")]
  [Category("HappyPath")]
  public void Wipe(bool wipe) {
    var image = FragmentedVolume(out _);
    var start = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(FileRecordOffset(image, "split.bin") + 88 + 16));
    var before = image.AsSpan(start * (int)BlockSize, 3 * (int)BlockSize).ToArray();
    var after = Remove(image, "split.bin", wipe).AsSpan(start * (int)BlockSize, 3 * (int)BlockSize).ToArray();
    Assert.That(after, wipe ? Is.All.Zero : Is.EqualTo(before));
  }

  [Test, Category("HappyPath")]
  public void GivenAFileWithAResourceFork_WhenRemoved_ThenItsResourceBlocksAreFreedToo() {
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", "keep"u8.ToArray());
    w.AddFile("forked.txt", "data"u8.ToArray());
    var image = w.Build(BlockSize);

    // Give forked.txt a one-block resource fork in the first free block past the data.
    var vh = image.AsSpan(1024);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
    var bitmapAt = (int)(BinaryPrimitives.ReadUInt32BigEndian(vh[(112 + 16)..]) * BlockSize);
    var free = Enumerable.Range(0, (int)totalBlocks - 2).Reverse()
      .First(b => (image[bitmapAt + b / 8] & (0x80 >> (b % 8))) == 0);
    image[bitmapAt + free / 8] |= (byte)(0x80 >> (free % 8));
    BinaryPrimitives.WriteUInt32BigEndian(vh[48..], BinaryPrimitives.ReadUInt32BigEndian(vh[48..]) - 1);
    var resource = FileRecordOffset(image, "forked.txt") + 168;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(resource), 10);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(resource + 12), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(resource + 16), (uint)free);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(resource + 20), 1);
    AssertBitmapConsistent(image);

    var after = Remove(image, "forked.txt");
    AssertBitmapConsistent(after);
    Assert.That(after[bitmapAt + free / 8] & (0x80 >> (free % 8)), Is.Zero, "the resource fork's block is free");
  }

  [Test, Category("BoundaryCase")]
  public void GivenAnEmptyFile_WhenRemoved_ThenNoBlockChangesAndTheBitmapAgrees() {
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", "keep"u8.ToArray());
    w.AddFile("empty", []);
    var image = w.Build(BlockSize);
    var after = Remove(image, "empty");
    AssertBitmapConsistent(after);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 48)), Is.EqualTo(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 48))));
  }

  private static void AssertRefusedAndUnchanged(byte[] image, string name) {
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.RemoveFile(ms, name));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAForkContinuedInTheOverflowFile_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = FragmentedVolume(out _);
    // totalBlocks above what the eight descriptors hold: the rest would be overflow records.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(FileRecordOffset(image, "split.bin") + 88 + 12), 4);
    AssertRefusedAndUnchanged(image, "split.bin");
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFileWithExtendedAttributes_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = FragmentedVolume(out _);
    var record = FileRecordOffset(image, "split.bin");
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(record + 2), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(record + 2)) | 0x0004));
    AssertRefusedAndUnchanged(image, "split.bin");
  }

  [Test, Category("ExceptionalCase")]
  public void GivenATransparentlyCompressedFile_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = FragmentedVolume(out _);
    image[FileRecordOffset(image, "split.bin") + 32 + 9] |= 0x20;
    AssertRefusedAndUnchanged(image, "split.bin");
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnAllocationFileElsewhere_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = FragmentedVolume(out _);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 112 + 16), 2);
    AssertRefusedAndUnchanged(image, "split.bin");
  }
}
