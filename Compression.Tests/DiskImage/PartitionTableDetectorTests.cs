using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class PartitionTableDetectorTests {

  /// <summary>
  /// Builds a minimal disk image with a single MBR partition entry.
  /// </summary>
  private static byte[] BuildMbrDisk(byte partitionType, uint lbaStart, uint sectorCount, int totalSectors = 2048) {
    var disk = new byte[totalSectors * 512];

    // MBR boot signature
    disk[510] = 0x55;
    disk[511] = 0xAA;

    // First partition entry at offset 0x1BE
    const int entryOffset = 0x1BE;
    disk[entryOffset + 0] = 0x80; // Active flag
    disk[entryOffset + 4] = partitionType;

    // LBA start (little-endian at offset 8 within entry)
    disk[entryOffset + 8] = (byte)(lbaStart & 0xFF);
    disk[entryOffset + 9] = (byte)((lbaStart >> 8) & 0xFF);
    disk[entryOffset + 10] = (byte)((lbaStart >> 16) & 0xFF);
    disk[entryOffset + 11] = (byte)((lbaStart >> 24) & 0xFF);

    // Sector count (little-endian at offset 12 within entry)
    disk[entryOffset + 12] = (byte)(sectorCount & 0xFF);
    disk[entryOffset + 13] = (byte)((sectorCount >> 8) & 0xFF);
    disk[entryOffset + 14] = (byte)((sectorCount >> 16) & 0xFF);
    disk[entryOffset + 15] = (byte)((sectorCount >> 24) & 0xFF);

    return disk;
  }

  /// <summary>
  /// Builds a minimal disk image with GPT headers.
  /// </summary>
  private static byte[] BuildGptDisk(int totalSectors = 4096) {
    var disk = new byte[totalSectors * 512];

    // Protective MBR
    disk[510] = 0x55;
    disk[511] = 0xAA;
    // Protective MBR entry: type 0xEE
    disk[0x1BE + 4] = 0xEE;
    disk[0x1BE + 8] = 1; // LBA start = 1
    var mbrSize = (uint)(totalSectors - 1);
    disk[0x1BE + 12] = (byte)(mbrSize & 0xFF);
    disk[0x1BE + 13] = (byte)((mbrSize >> 8) & 0xFF);
    disk[0x1BE + 14] = (byte)((mbrSize >> 16) & 0xFF);
    disk[0x1BE + 15] = (byte)((mbrSize >> 24) & 0xFF);

    // GPT header at LBA 1 (offset 512)
    var hdrOff = 512;
    "EFI PART"u8.CopyTo(disk.AsSpan(hdrOff));

    // Revision 1.0
    disk[hdrOff + 8] = 0x00; disk[hdrOff + 9] = 0x00; disk[hdrOff + 10] = 0x01; disk[hdrOff + 11] = 0x00;
    // Header size = 92
    disk[hdrOff + 12] = 92; disk[hdrOff + 13] = 0; disk[hdrOff + 14] = 0; disk[hdrOff + 15] = 0;

    // Partition entry LBA = 2
    disk[hdrOff + 72] = 2;
    // Number of partition entries = 1
    disk[hdrOff + 80] = 1;
    // Size of partition entry = 128
    disk[hdrOff + 84] = 128;

    // GPT partition entry at LBA 2 (offset 1024)
    var entryOff = 1024;

    // Type GUID: Microsoft Basic Data = EBD0A0A2-B9E5-4433-87C0-68B6B72699C7
    // Mixed-endian: first 3 components LE, last 2 BE
    var typeGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    WriteMixedEndianGuid(disk, entryOff, typeGuid);

    // Unique GUID (any non-zero value)
    WriteMixedEndianGuid(disk, entryOff + 16, Guid.NewGuid());

    // First LBA = 34 (standard for GPT partitions)
    var firstLba = 34UL;
    disk[entryOff + 32] = (byte)(firstLba & 0xFF);
    disk[entryOff + 33] = (byte)((firstLba >> 8) & 0xFF);

    // Last LBA
    var lastLba = (ulong)(totalSectors - 34);
    disk[entryOff + 40] = (byte)(lastLba & 0xFF);
    disk[entryOff + 41] = (byte)((lastLba >> 8) & 0xFF);

    return disk;
  }

  private static void WriteMixedEndianGuid(byte[] data, int offset, Guid guid) {
    var bytes = guid.ToByteArray();
    // .NET Guid.ToByteArray() already stores first 3 components in LE format
    // which is what GPT uses for mixed-endian
    bytes.CopyTo(data, offset);
  }

  [Test, Category("HappyPath")]
  public void Detect_MbrPartitionTable() {
    var disk = BuildMbrDisk(0x0B, lbaStart: 63, sectorCount: 1024);
    var result = PartitionTableDetector.Detect(disk);

    Assert.That(result.Scheme, Is.EqualTo("MBR"));
    Assert.That(result.Partitions, Has.Count.EqualTo(1));
    Assert.That(result.Partitions[0].TypeName, Does.Contain("FAT32"));
    Assert.That(result.Partitions[0].StartOffset, Is.EqualTo(63 * 512));
    Assert.That(result.Partitions[0].Size, Is.EqualTo(1024 * 512));
    Assert.That(result.Partitions[0].Source, Is.EqualTo("MBR"));
  }

  [Test, Category("HappyPath")]
  public void Detect_GptPartitionTable() {
    var disk = BuildGptDisk();
    var result = PartitionTableDetector.Detect(disk);

    Assert.That(result.Scheme, Is.EqualTo("GPT"));
    Assert.That(result.Partitions, Has.Count.EqualTo(1));
    Assert.That(result.Partitions[0].TypeName, Does.Contain("Basic Data"));
    Assert.That(result.Partitions[0].Source, Is.EqualTo("GPT"));
  }

  [Test, Category("HappyPath")]
  public void Detect_PrefsersGptOverMbr() {
    // GPT disks have a protective MBR, so GPT should be detected first.
    var disk = BuildGptDisk();
    var result = PartitionTableDetector.Detect(disk);
    Assert.That(result.Scheme, Is.EqualTo("GPT"));
  }

  [Test, Category("HappyPath")]
  public void Detect_NoPartitionTable_ReturnsNone() {
    var disk = new byte[4096];
    var result = PartitionTableDetector.Detect(disk);
    Assert.That(result.Scheme, Is.EqualTo("None"));
    Assert.That(result.Partitions, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_TooSmall_ReturnsNone() {
    var disk = new byte[512]; // Less than 1024 bytes
    var result = PartitionTableDetector.Detect(disk);
    Assert.That(result.Scheme, Is.EqualTo("None"));
    Assert.That(result.Partitions, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_StreamOverload_MatchesByteArrayOverload() {
    // Use a disk large enough to contain the partition (lbaStart=63, 512 sectors, total=2048 sectors).
    var disk = BuildMbrDisk(0x07, lbaStart: 63, sectorCount: 512, totalSectors: 2048);
    var byteResult = PartitionTableDetector.Detect(disk);
    using var ms = new MemoryStream(disk);
    var streamResult = PartitionTableDetector.Detect(ms);

    Assert.That(streamResult.Scheme, Is.EqualTo(byteResult.Scheme));
    Assert.That(streamResult.Partitions, Has.Count.EqualTo(byteResult.Partitions.Count));
    Assert.That(streamResult.Partitions[0].TypeName, Is.EqualTo(byteResult.Partitions[0].TypeName));
  }

  [Test, Category("HappyPath")]
  public void ExtractPartitionData_ReturnsCorrectSlice() {
    var disk = new byte[65536];
    // Write known data at partition offset (use a small offset that fits in the array).
    const int partStart = 1024;
    for (var i = 0; i < 100; i++)
      disk[partStart + i] = (byte)(i + 1);

    var partition = new PartitionEntry {
      Index = 0,
      StartOffset = partStart,
      Size = 100,
      TypeName = "Test",
      TypeCode = "0x00",
      Source = "Test"
    };

    var data = PartitionTableDetector.ExtractPartitionData(disk, partition);
    Assert.That(data, Has.Length.EqualTo(100));
    Assert.That(data[0], Is.EqualTo(1));
    Assert.That(data[99], Is.EqualTo(100));
  }

  [Test, Category("HappyPath")]
  public void ExtractPartitionData_ClampsToDiskSize() {
    var disk = new byte[1024];
    var partition = new PartitionEntry {
      Index = 0,
      StartOffset = 512,
      Size = 2048, // Exceeds disk size
      TypeName = "Test",
      TypeCode = "0x00",
      Source = "Test"
    };

    var data = PartitionTableDetector.ExtractPartitionData(disk, partition);
    Assert.That(data, Has.Length.EqualTo(512)); // Clamped to available data
  }

  [Test, Category("ErrorHandling")]
  public void ExtractPartitionData_OutOfRange_ReturnsEmpty() {
    var disk = new byte[1024];
    var partition = new PartitionEntry {
      Index = 0,
      StartOffset = 2048, // Beyond disk
      Size = 512,
      TypeName = "Test",
      TypeCode = "0x00",
      Source = "Test"
    };

    var data = PartitionTableDetector.ExtractPartitionData(disk, partition);
    Assert.That(data, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_MultipleMbrPartitions() {
    var disk = new byte[2048 * 512];
    disk[510] = 0x55;
    disk[511] = 0xAA;

    // Partition 1: NTFS
    var off1 = 0x1BE;
    disk[off1 + 4] = 0x07; // NTFS
    disk[off1 + 8] = 63; // LBA start
    disk[off1 + 12] = 0x00; disk[off1 + 13] = 0x04; // 1024 sectors

    // Partition 2: Linux
    var off2 = 0x1BE + 16;
    disk[off2 + 4] = 0x83; // Linux
    disk[off2 + 8] = 0x27; disk[off2 + 9] = 0x04; // LBA start = 1063
    disk[off2 + 12] = 0x00; disk[off2 + 13] = 0x04; // 1024 sectors

    var result = PartitionTableDetector.Detect(disk);
    Assert.That(result.Scheme, Is.EqualTo("MBR"));
    Assert.That(result.Partitions, Has.Count.EqualTo(2));
    Assert.That(result.Partitions[0].TypeName, Does.Contain("NTFS"));
    Assert.That(result.Partitions[1].TypeName, Does.Contain("Linux"));
  }

  [Test, Category("HappyPath")]
  public void Detect_MbrPartitionBeyondDisk_Filtered() {
    // Create a small disk but with a partition that references beyond it.
    var disk = BuildMbrDisk(0x07, lbaStart: 100000, sectorCount: 1000, totalSectors: 256);
    var result = PartitionTableDetector.Detect(disk);

    // The partition starts beyond the stream, so it should be filtered out.
    Assert.That(result.Scheme, Is.EqualTo("None"));
    Assert.That(result.Partitions, Is.Empty);
  }
}
