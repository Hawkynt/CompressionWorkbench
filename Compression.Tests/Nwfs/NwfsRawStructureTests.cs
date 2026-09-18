using System.Buffers.Binary;
using FileSystem.Nwfs;

namespace Compression.Tests.Nwfs;

[TestFixture]
public sealed class NwfsRawStructureTests {
  private const int SectorSize = 512;
  private const int IoBlockSize = 4096;
  private const int FatEntryBytes = 8;
  private const int FatEntriesPerIoBlock = IoBlockSize / FatEntryBytes;
  private const int FatMirrorBlockGap = 64;

  private readonly record struct Layout(
    long PartitionOffset,
    long LogicalPartitionOffset,
    long VolumeOffset,
    int ClusterSize,
    uint ClusterCount,
    uint Fat1,
    uint Fat2,
    uint Directory1,
    uint Directory2);

  private static byte[] Bytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static Layout ParseLayout(byte[] image) {
    var partitionStart = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(446 + 8, 4));
    var partitionOffset = checked((long)partitionStart * SectorSize);
    var masterOffset = checked(partitionOffset + 0x20L * SectorSize);
    Assert.That(image.AsSpan((int)masterOffset, 8).SequenceEqual("HOTFIX00"u8), Is.True);

    var hotfixSectors = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)masterOffset + 24, 4));
    var logicalPartitionOffset = checked(partitionOffset + (long)hotfixSectors * SectorSize);
    var volumeTableOffset = checked(logicalPartitionOffset + 4L * IoBlockSize);
    Assert.That(image.AsSpan((int)volumeTableOffset, 16).SequenceEqual("NetWare Volumes\0"u8), Is.True);

    var entry = image.AsSpan((int)volumeTableOffset + 32, 60);
    var signature = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
    var clusterSize = (signature & 0xFF) switch {
      3 => 4096,
      4 => 8192,
      5 => 16384,
      6 => 32768,
      7 => 65536,
      _ => 0,
    };
    Assert.That(clusterSize, Is.Not.Zero);

    var rootSectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
    var volumeOffset = checked(logicalPartitionOffset + (long)rootSectors * SectorSize);
    return new Layout(
      partitionOffset,
      logicalPartitionOffset,
      volumeOffset,
      clusterSize,
      BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]),
      BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]),
      BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]),
      BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]),
      BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]));
  }

  private static long FatEntryOffset(Layout layout, uint cluster, bool mirror) {
    var streamBlock = checked((int)(cluster / FatEntriesPerIoBlock));
    var physicalBlock = checked(streamBlock + streamBlock / FatMirrorBlockGap * FatMirrorBlockGap);
    if (mirror)
      physicalBlock = checked(physicalBlock + FatMirrorBlockGap);
    var within = checked((int)(cluster % FatEntriesPerIoBlock) * FatEntryBytes);
    return checked(layout.VolumeOffset + (long)physicalBlock * IoBlockSize + within);
  }

  private static (uint Index, uint Next) ReadFat(byte[] image, Layout layout, uint cluster, bool mirror = false) {
    var offset = checked((int)FatEntryOffset(layout, cluster, mirror));
    return (
      BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 4, 4)));
  }

  private static void WriteFat(byte[] image, Layout layout, uint cluster, uint index, uint next, bool mirror) {
    var offset = checked((int)FatEntryOffset(layout, cluster, mirror));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), index);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4, 4), next);
  }

  private static void WriteFatBoth(byte[] image, Layout layout, uint cluster, uint index, uint next) {
    WriteFat(image, layout, cluster, index, next, mirror: false);
    WriteFat(image, layout, cluster, index, next, mirror: true);
  }

  [Test, Category("Compatibility")]
  public void Writer_UsesTheReferenceMasterVolumeFatAndDetLayout() {
    var writer = new NwfsWriter {
      BlockSize = 32768,
      MinimumImageSize = 4 * 1024 * 1024,
      Timestamp = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc),
    };
    writer.AddFile("PUBLIC/HELLO.BIN", Bytes(70000, 1));
    var image = writer.Build();
    var layout = ParseLayout(image);

    var firstMaster = image.AsSpan(
      checked((int)(layout.PartitionOffset + 0x20L * SectorSize)),
      IoBlockSize).ToArray();
    foreach (var sector in new[] { 0x20, 0x40, 0x60, 0x80 }) {
      var offset = checked((int)(layout.PartitionOffset + (long)sector * SectorSize));
      Assert.That(image.AsSpan(offset, IoBlockSize).SequenceEqual(firstMaster), Is.True,
        $"master copy at sector 0x{sector:X} differs");
    }

    var hotfix = firstMaster[..SectorSize];
    var mirror = firstMaster[SectorSize..(SectorSize * 2)];
    var nwvpMirror = firstMaster[(SectorSize * 2)..(SectorSize * 3)];
    Assert.Multiple(() => {
      Assert.That(hotfix.AsSpan(0, 8).SequenceEqual("HOTFIX00"u8), Is.True);
      Assert.That(mirror.AsSpan(0, 8).SequenceEqual("MIRROR00"u8), Is.True);
      Assert.That(nwvpMirror.AsSpan(0, 16).SequenceEqual("NWVP MIRROR 0001"u8), Is.True);

      // HOTFIX_BLOCK_TABLE is HotFix1, BadBlock1, HotFix2, BadBlock2.
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(hotfix.AsSpan(28)), Is.EqualTo(20u * 8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(hotfix.AsSpan(32)), Is.EqualTo(28u * 8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(hotfix.AsSpan(36)), Is.EqualTo(36u * 8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(hotfix.AsSpan(40)), Is.EqualTo(44u * 8));
    });

    var firstVolumeTable = image.AsSpan(
      checked((int)(layout.LogicalPartitionOffset + 4L * IoBlockSize)),
      SectorSize).ToArray();
    foreach (var block in new[] { 4, 8, 12, 16 }) {
      var offset = checked((int)(layout.LogicalPartitionOffset + (long)block * IoBlockSize));
      Assert.That(image.AsSpan(offset, SectorSize).SequenceEqual(firstVolumeTable), Is.True,
        $"volume table copy at logical block {block} differs");
    }

    var entry = firstVolumeTable[32..92];
    Assert.Multiple(() => {
      Assert.That(firstVolumeTable.AsSpan(0, 16).SequenceEqual("NetWare Volumes\0"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(firstVolumeTable.AsSpan(16, 4)), Is.EqualTo(1));
      Assert.That(entry[0], Is.EqualTo(3));
      Assert.That(entry.AsSpan(1, 3).SequenceEqual("SYS"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(16)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(20)), Is.EqualTo(0x00000106u));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(24)), Is.EqualTo(20u * 8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(36)), Is.Zero);
      Assert.That(layout.Fat1, Is.Zero);
      Assert.That(layout.Fat2, Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(56)), Is.Zero);
    });

    var firstFat = image.AsSpan((int)layout.VolumeOffset, IoBlockSize);
    var mirroredFat = image.AsSpan(
      checked((int)(layout.VolumeOffset + FatMirrorBlockGap * (long)IoBlockSize)),
      IoBlockSize);
    Assert.That(firstFat.SequenceEqual(mirroredFat), Is.True,
      "first FAT block is not mirrored 64 physical 4 KiB blocks away");

    var foundFree = false;
    for (uint cluster = 0; cluster < layout.ClusterCount; ++cluster) {
      var fat = ReadFat(image, layout, cluster);
      if (fat is not (0, 0))
        continue;
      foundFree = true;
      break;
    }
    Assert.That(foundFree, Is.True, "padded image contains no in-range (0,0) free FAT entry");

    var root1 = image.AsSpan(
      checked((int)(layout.VolumeOffset + (long)layout.Directory1 * layout.ClusterSize)),
      128).ToArray();
    var root2 = image.AsSpan(
      checked((int)(layout.VolumeOffset + (long)layout.Directory2 * layout.ClusterSize)),
      128).ToArray();
    Assert.That(root1.AsSpan().SequenceEqual(root2), Is.True);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(root1), Is.EqualTo(0xFFFFFFFDu));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(root1.AsSpan(4)), Is.EqualTo(0x10u));
      Assert.That(root1[9] & 0x04, Is.Not.Zero);
      Assert.That(root1[9] & 0x10, Is.Not.Zero);
      Assert.That(root1[10], Is.Zero);
      Assert.That(root1[11], Is.EqualTo(1));
      Assert.That(root1[23], Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(root1.AsSpan(28)), Is.EqualTo(0x01000000u));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(root1.AsSpan(100)), Is.EqualTo(0xFFFF));
    });
  }

  [Test, Category("ErrorHandling")]
  [TestCase(false)]
  [TestCase(true)]
  public void Reader_RecoversWhenExactlyOneFatCopyIsCorrupt(bool corruptMirror) {
    var payload = Bytes(12000, 2);
    var writer = new NwfsWriter();
    writer.AddFile("HELLO.BIN", payload);
    var image = writer.Build();
    var layout = ParseLayout(image);

    var offset = checked((int)FatEntryOffset(layout, 0, corruptMirror));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), 0x01000000u);

    var volume = NwfsReader.TryOpen(image);
    Assert.That(volume, Is.Not.Null);
    Assert.That(volume!.ReadFile("HELLO.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("ErrorHandling")]
  [TestCase(false)]
  [TestCase(true)]
  public void Reader_RecoversWhenExactlyOneDetCopyIsCorrupt(bool corruptSecondCopy) {
    var payload = Bytes(9000, 3);
    var writer = new NwfsWriter();
    writer.AddFile("PUBLIC/HELLO.BIN", payload);
    var image = writer.Build();
    var layout = ParseLayout(image);
    var head = corruptSecondCopy ? layout.Directory2 : layout.Directory1;
    var rootOffset = checked((int)(layout.VolumeOffset + (long)head * layout.ClusterSize));

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootOffset, 4), 0x12345678u);

    var volume = NwfsReader.TryOpen(image);
    Assert.That(volume, Is.Not.Null);
    Assert.That(volume!.ReadFile("PUBLIC/HELLO.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("Compatibility")]
  public void Reader_UsesFatIndexAsLogicalPositionAndZeroFillsAHole() {
    const int clusterSize = 4096;
    var payload = Bytes(clusterSize * 3, 4);
    var writer = new NwfsWriter { BlockSize = clusterSize };
    writer.AddFile("SPARSE.BIN", payload);
    var image = writer.Build();
    var layout = ParseLayout(image);

    var original = NwfsReader.TryOpen(image)!;
    var item = original.List().Single(entry => entry.Path == "SPARSE.BIN");
    var first = item.FirstBlock;
    var firstFat = ReadFat(image, layout, first);
    var second = firstFat.Next;
    var secondFat = ReadFat(image, layout, second);
    var third = secondFat.Next;
    var thirdFat = ReadFat(image, layout, third);

    Assert.Multiple(() => {
      Assert.That(firstFat.Index, Is.Zero);
      Assert.That(secondFat.Index, Is.EqualTo(1));
      Assert.That(thirdFat.Index, Is.EqualTo(2));
      Assert.That(thirdFat.Next, Is.EqualTo(uint.MaxValue));
    });

    // Splice physical cluster #2 out of the chain while retaining logical indexes
    // 0 and 2. The missing logical index 1 is a file hole and must read as zeros.
    WriteFatBoth(image, layout, first, 0, third);
    WriteFatBoth(image, layout, second, 0, 0);

    var sparse = NwfsReader.TryOpen(image);
    Assert.That(sparse, Is.Not.Null);

    var expected = payload.ToArray();
    Array.Clear(expected, clusterSize, clusterSize);
    Assert.That(sparse!.ReadFile("SPARSE.BIN"), Is.EqualTo(expected));
  }
}
