using Compression.Analysis;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class DiskImageNestingTests {

  [SetUp]
  public void EnsureRegistered() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
  }

  /// <summary>
  /// Builds a raw disk with MBR + a FAT12 partition containing a test file,
  /// then wraps it in a VMDK so the AutoExtractor can detect it by magic.
  /// VMDK is used because its "KDMV" magic is at offset 0 and the writer
  /// produces a cleanly extractable disk image.
  /// </summary>
  private static byte[] BuildVmdkWithMbrAndFatPartition() {
    // Build a small FAT12 filesystem image with a test file.
    var fatWriter = new FileFormat.Fat.FatWriter();
    fatWriter.AddFile("TEST.TXT", "Hello from FAT!"u8.ToArray());
    var fatImage = fatWriter.Build(); // 1.44MB FAT12

    // Build a raw disk image with MBR pointing to the FAT data.
    const int partitionStartSector = 63;
    var partitionSectors = (fatImage.Length + 511) / 512;
    var totalSectors = partitionStartSector + partitionSectors + 1;
    var rawDisk = new byte[totalSectors * 512];

    // Copy FAT image into the partition area.
    Array.Copy(fatImage, 0, rawDisk, partitionStartSector * 512, fatImage.Length);

    // Write MBR with boot signature.
    rawDisk[510] = 0x55;
    rawDisk[511] = 0xAA;

    // First partition entry at 0x1BE: FAT12 type 0x01.
    const int entryOffset = 0x1BE;
    rawDisk[entryOffset + 0] = 0x80; // Active
    rawDisk[entryOffset + 4] = 0x01; // FAT12
    rawDisk[entryOffset + 8] = partitionStartSector;
    rawDisk[entryOffset + 12] = (byte)(partitionSectors & 0xFF);
    rawDisk[entryOffset + 13] = (byte)((partitionSectors >> 8) & 0xFF);
    rawDisk[entryOffset + 14] = (byte)((partitionSectors >> 16) & 0xFF);
    rawDisk[entryOffset + 15] = (byte)((partitionSectors >> 24) & 0xFF);

    // Wrap in VMDK.
    var vmdkWriter = new FileFormat.Vmdk.VmdkWriter();
    vmdkWriter.SetDiskData(rawDisk);
    return vmdkWriter.Build();
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_VmdkWithMbrPartitions_DetectsPartitionTable() {
    var vmdkData = BuildVmdkWithMbrAndFatPartition();
    using var ms = new MemoryStream(vmdkData);

    var extractor = new AutoExtractor();
    var result = extractor.Extract(ms);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("Vmdk"));
    Assert.That(result.PartitionTable, Is.Not.Null);
    Assert.That(result.PartitionTable!.Scheme, Is.EqualTo("MBR"));
    Assert.That(result.PartitionTable.Partitions, Has.Count.EqualTo(1));
    Assert.That(result.PartitionTable.Partitions[0].TypeName, Does.Contain("FAT12"));
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_VmdkWithFatPartition_RecursivelyExtractsFilesystem() {
    var vmdkData = BuildVmdkWithMbrAndFatPartition();
    using var ms = new MemoryStream(vmdkData);

    var extractor = new AutoExtractor();
    var result = extractor.Extract(ms);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.PartitionTable, Is.Not.Null);
    var partition = result.PartitionTable!.Partitions[0];

    // The FAT filesystem inside the partition should be detected and extracted.
    Assert.That(partition.NestedResult, Is.Not.Null);
    Assert.That(partition.NestedResult!.Entries, Has.Count.GreaterThan(0));

    // Look for our test file.
    var testFile = partition.NestedResult.Entries.FirstOrDefault(
      e => e.Name.Contains("TEST", StringComparison.OrdinalIgnoreCase));
    Assert.That(testFile, Is.Not.Null, "Expected to find TEST.TXT in the FAT partition");
    Assert.That(testFile!.Data, Is.EqualTo("Hello from FAT!"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_PlainVmdk_NoPartitionTable() {
    // A VMDK whose raw disk data is not a valid disk (no MBR/GPT).
    var randomData = new byte[4096];
    Array.Fill(randomData, (byte)0xCC);

    var vmdkWriter = new FileFormat.Vmdk.VmdkWriter();
    vmdkWriter.SetDiskData(randomData);
    var vmdkData = vmdkWriter.Build();

    using var ms = new MemoryStream(vmdkData);
    var extractor = new AutoExtractor();
    var result = extractor.Extract(ms);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("Vmdk"));
    // No partition table should be detected in garbage data.
    Assert.That(result.PartitionTable, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_NonDiskImage_NoPartitionTableParsing() {
    // A ZIP archive should not attempt partition table detection.
    using var zipMs = new MemoryStream();
    using (var w = new FileFormat.Zip.ZipWriter(zipMs, leaveOpen: true))
      w.AddEntry("file.txt", "data"u8.ToArray());
    zipMs.Position = 0;

    var extractor = new AutoExtractor();
    var result = extractor.Extract(zipMs);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("Zip"));
    Assert.That(result.PartitionTable, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void PartitionTableDetector_FatDiskImage_NoPartitionTable() {
    // A raw FAT filesystem image has a boot sector that looks like an MBR (0x55AA)
    // but the partition entries are either empty or point beyond the image.
    var fatWriter = new FileFormat.Fat.FatWriter();
    fatWriter.AddFile("TEST.TXT", "data"u8.ToArray());
    var fatImage = fatWriter.Build(); // 1.44MB

    var result = PartitionTableDetector.Detect(fatImage);
    // Any detected partitions should still be within bounds.
    foreach (var p in result.Partitions) {
      Assert.That(p.StartOffset, Is.LessThan(fatImage.Length));
    }
  }

  [Test, Category("HappyPath")]
  public void PartitionTableDetector_ExtractAndProbe_EndToEnd() {
    // Build a raw disk with MBR + FAT partition and verify the full pipeline
    // (detection + extraction) works without going through a disk image format.
    var fatWriter = new FileFormat.Fat.FatWriter();
    fatWriter.AddFile("HELLO.TXT", "world"u8.ToArray());
    var fatImage = fatWriter.Build();

    const int partStart = 63;
    var partSectors = (fatImage.Length + 511) / 512;
    var totalSectors = partStart + partSectors + 1;
    var rawDisk = new byte[totalSectors * 512];

    Array.Copy(fatImage, 0, rawDisk, partStart * 512, fatImage.Length);

    rawDisk[510] = 0x55;
    rawDisk[511] = 0xAA;
    rawDisk[0x1BE + 4] = 0x01; // FAT12
    rawDisk[0x1BE + 8] = partStart;
    rawDisk[0x1BE + 12] = (byte)(partSectors & 0xFF);
    rawDisk[0x1BE + 13] = (byte)((partSectors >> 8) & 0xFF);

    var detection = PartitionTableDetector.Detect(rawDisk);
    Assert.That(detection.Scheme, Is.EqualTo("MBR"));
    Assert.That(detection.Partitions, Has.Count.EqualTo(1));

    // Extract partition data and verify it matches the FAT image.
    var partData = PartitionTableDetector.ExtractPartitionData(rawDisk, detection.Partitions[0]);
    Assert.That(partData, Has.Length.EqualTo(fatImage.Length));
    Assert.That(partData, Is.EqualTo(fatImage));
  }
}
