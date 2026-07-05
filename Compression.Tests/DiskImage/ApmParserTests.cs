using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class ApmParserTests {

  /// <summary>
  /// Hand-crafts a minimal Apple Partition Map with the given block size:
  /// block 0 = DDR ("ER"), then a 3-entry map (self-descriptor, one HFS
  /// partition, one Apple_Free run). Big-endian throughout.
  /// </summary>
  private static byte[] BuildApm(int blockSize, bool withDdr = true) {
    const int totalBlocks = 40;
    var disk = new byte[totalBlocks * blockSize];

    if (withDdr) {
      BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(0), 0x4552);            // sbSig "ER"
      BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), (ushort)blockSize); // sbBlkSize
      BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(4), (uint)totalBlocks); // sbBlkCount
    }

    WriteEntry(disk, blockSize, entryIndex: 0, mapBlkCnt: 3, pyStart: 1, blkCnt: 3, type: "Apple_partition_map", name: "Apple");
    WriteEntry(disk, blockSize, entryIndex: 1, mapBlkCnt: 3, pyStart: 4, blkCnt: 10, type: "Apple_HFS", name: "MacOS");
    WriteEntry(disk, blockSize, entryIndex: 2, mapBlkCnt: 3, pyStart: 14, blkCnt: 5, type: "Apple_Free", name: "Extra");

    return disk;
  }

  private static void WriteEntry(byte[] disk, int blockSize, int entryIndex, uint mapBlkCnt,
      uint pyStart, uint blkCnt, string type, string name) {
    var off = (1 + entryIndex) * blockSize;
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(off), 0x504D);      // pmSig "PM"
    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(off + 4), mapBlkCnt);
    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(off + 8), pyStart);
    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(off + 12), blkCnt);
    Encoding.ASCII.GetBytes(name).CopyTo(disk.AsSpan(off + 16, 32));
    Encoding.ASCII.GetBytes(type).CopyTo(disk.AsSpan(off + 48, 32));
  }

  [Test, Category("HappyPath")]
  public void Parse_512BlockSize_EnumeratesOnlyRealPartitions() {
    const int bs = 512;
    var disk = BuildApm(bs);
    using var ms = new MemoryStream(disk);

    var parts = ApmParser.Parse(ms);

    // Only the HFS partition survives: the map self-descriptor and Apple_Free are skipped.
    Assert.That(parts, Has.Count.EqualTo(1));
    Assert.That(parts[0].TypeName, Is.EqualTo("Apple_HFS"));
    Assert.That(parts[0].StartOffset, Is.EqualTo(4L * bs));
    Assert.That(parts[0].Size, Is.EqualTo(10L * bs));
    Assert.That(parts[0].Name, Is.EqualTo("MacOS"));
    Assert.That(parts[0].Source, Is.EqualTo("APM"));
  }

  [Test, Category("HappyPath")]
  public void Parse_2048BlockSize_UsesDdrBlockSize() {
    const int bs = 2048;
    var disk = BuildApm(bs);
    using var ms = new MemoryStream(disk);

    var parts = ApmParser.Parse(ms);

    Assert.That(parts, Has.Count.EqualTo(1));
    Assert.That(parts[0].StartOffset, Is.EqualTo(4L * bs));
    Assert.That(parts[0].Size, Is.EqualTo(10L * bs));
  }

  [Test, Category("HappyPath")]
  public void Parse_NoDdr_ProbesBlock1Signature() {
    const int bs = 512;
    var disk = BuildApm(bs, withDdr: false);
    using var ms = new MemoryStream(disk);

    Assert.That(ApmParser.IsApm(disk), Is.True);
    var parts = ApmParser.Parse(ms);
    Assert.That(parts, Has.Count.EqualTo(1));
    Assert.That(parts[0].TypeName, Is.EqualTo("Apple_HFS"));
  }

  [Test, Category("HappyPath")]
  public void Detector_RecognisesApmScheme() {
    var disk = BuildApm(512);
    var result = PartitionTableDetector.Detect(disk);

    Assert.That(result.Scheme, Is.EqualTo("APM"));
    Assert.That(result.Partitions, Has.Count.EqualTo(1));
    Assert.That(result.Partitions[0].TypeName, Is.EqualTo("Apple_HFS"));
  }

  [Test, Category("Exceptional")]
  public void IsApm_PlainData_ReturnsFalse() {
    var disk = new byte[4096];
    Assert.That(ApmParser.IsApm(disk), Is.False);
  }
}
