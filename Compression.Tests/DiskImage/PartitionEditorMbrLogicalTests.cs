using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class PartitionEditorMbrLogicalTests {

  private const int SectorSize = 512;

  private static byte[] BuildEmptyMbrDisk(int sectorCount) {
    var disk = new byte[sectorCount * SectorSize];
    disk[510] = 0x55;
    disk[511] = 0xAA;
    return disk;
  }

  [Test, Category("HappyPath")]
  public void AddPartition_ExtendedContainer_AppearsInListing() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 4096L * SectorSize, PartitionType.ExtendedLba, null);

    var partitions = editor.ListPartitions();
    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].Source, Is.EqualTo("MBR (Extended Container)"));
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0x0F"));
  }

  [Test, Category("HappyPath")]
  public void AddLogicalPartition_WithinExtendedContainer_AddsAndPersists() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    // Extended container at LBA 64, size 4096 sectors.
    editor.AddPartition(64L * SectorSize, 4096L * SectorSize, PartitionType.ExtendedLba, null);

    // First logical: data at LBA 65 (EBR at LBA 64 = container start).
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "logical1");

    var partitions = editor.ListPartitions();
    Assert.That(partitions, Has.Count.EqualTo(2));
    Assert.That(partitions[0].Source, Is.EqualTo("MBR (Extended Container)"));
    Assert.That(partitions[1].Source, Is.EqualTo("EBR"));
    Assert.That(partitions[1].StartOffset, Is.EqualTo(65L * SectorSize));
    Assert.That(partitions[1].Size, Is.EqualTo(256L * SectorSize));
    Assert.That(partitions[1].TypeCode, Is.EqualTo("0x83"));

    // Round-trip: reload from raw bytes.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    var freshParts = freshEditor.ListPartitions();
    Assert.That(freshParts, Has.Count.EqualTo(2));
    Assert.That(freshParts[1].Source, Is.EqualTo("EBR"));
    Assert.That(freshParts[1].StartOffset, Is.EqualTo(65L * SectorSize));
    Assert.That(freshParts[1].TypeCode, Is.EqualTo("0x83"));
  }

  [Test, Category("HappyPath")]
  public void MbrLogicalChain_ThreeLogicals_ListReadsAll() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 6000L * SectorSize, PartitionType.ExtendedLba, null);

    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "logA");
    editor.AddLogicalPartition(400L * SectorSize, 512L * SectorSize, PartitionType.NtfsExfat, "logB");
    editor.AddLogicalPartition(1024L * SectorSize, 1024L * SectorSize, PartitionType.Fat32Lba, "logC");

    var partitions = editor.ListPartitions();
    var logicals = partitions.Where(p => p.Source == "EBR").ToList();
    Assert.That(logicals, Has.Count.EqualTo(3));

    // Re-open from raw bytes — exercises the EBR chain walker.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    var freshLogicals = freshEditor.ListPartitions().Where(p => p.Source == "EBR")
      .OrderBy(p => p.StartOffset).ToList();
    Assert.That(freshLogicals, Has.Count.EqualTo(3));
    Assert.That(freshLogicals[0].StartOffset, Is.EqualTo(65L * SectorSize));
    Assert.That(freshLogicals[0].TypeCode, Is.EqualTo("0x83"));
    Assert.That(freshLogicals[1].StartOffset, Is.EqualTo(400L * SectorSize));
    Assert.That(freshLogicals[1].TypeCode, Is.EqualTo("0x07"));
    Assert.That(freshLogicals[2].StartOffset, Is.EqualTo(1024L * SectorSize));
    Assert.That(freshLogicals[2].TypeCode, Is.EqualTo("0x0C"));
  }

  [Test, Category("HappyPath")]
  public void AddFourthLogical_AfterThree_ListAllFour() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 6000L * SectorSize, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L1");
    editor.AddLogicalPartition(400L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L2");
    editor.AddLogicalPartition(800L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L3");
    editor.AddLogicalPartition(1200L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, "L4");

    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    var logicals = freshEditor.ListPartitions().Where(p => p.Source == "EBR")
      .OrderBy(p => p.StartOffset).ToList();
    Assert.That(logicals, Has.Count.EqualTo(4));
    Assert.That(logicals[3].TypeCode, Is.EqualTo("0x07"));
  }

  [Test, Category("HappyPath")]
  public void DeleteSecondLogical_ChainStillIntact() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 6000L * SectorSize, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L1");
    editor.AddLogicalPartition(400L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, "L2");
    editor.AddLogicalPartition(800L * SectorSize, 256L * SectorSize, PartitionType.Fat32Lba, "L3");

    // Find index of L2 in the in-memory list (logicals come after primaries).
    var parts = editor.ListPartitions();
    var l2Index = -1;
    for (var i = 0; i < parts.Count; ++i) {
      if (parts[i].Source == "EBR" && parts[i].StartOffset == 400L * SectorSize) {
        l2Index = i;
        break;
      }
    }
    Assert.That(l2Index, Is.GreaterThanOrEqualTo(0));

    editor.DeletePartition(l2Index);

    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    var logicals = freshEditor.ListPartitions().Where(p => p.Source == "EBR")
      .OrderBy(p => p.StartOffset).ToList();

    Assert.That(logicals, Has.Count.EqualTo(2));
    Assert.That(logicals[0].StartOffset, Is.EqualTo(65L * SectorSize));
    Assert.That(logicals[0].TypeCode, Is.EqualTo("0x83"));
    Assert.That(logicals[1].StartOffset, Is.EqualTo(800L * SectorSize));
    Assert.That(logicals[1].TypeCode, Is.EqualTo("0x0C"));
  }

  [Test, Category("HappyPath")]
  public void DeleteExtendedContainer_DropsAllLogicals() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 6000L * SectorSize, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L1");
    editor.AddLogicalPartition(400L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, "L2");

    editor.DeletePartition(0); // container is first.

    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    Assert.That(freshEditor.ListPartitions(), Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void AddLogicalPartition_WithoutContainer_Throws() {
    var disk = BuildEmptyMbrDisk(2048);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    Assert.That(() => editor.AddLogicalPartition(100L * SectorSize, 100L * SectorSize, PartitionType.Linux, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void AddLogicalPartition_OutsideContainer_Throws() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 256L * SectorSize, PartitionType.ExtendedLba, null);

    // Try to place logical beyond container end.
    Assert.That(() => editor.AddLogicalPartition(500L * SectorSize, 64L * SectorSize, PartitionType.Linux, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void AddSecondExtendedContainer_Throws() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 256L * SectorSize, PartitionType.ExtendedLba, null);

    Assert.That(() => editor.AddPartition(2048L * SectorSize, 256L * SectorSize, PartitionType.ExtendedLba, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void AddLogicalPartition_WithExtendedType_Throws() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 4096L * SectorSize, PartitionType.ExtendedLba, null);

    Assert.That(() => editor.AddLogicalPartition(66L * SectorSize, 256L * SectorSize, PartitionType.ExtendedLba, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("HappyPath")]
  public void PurgeLogical_ZeroesDataAndEbr() {
    var disk = BuildEmptyMbrDisk(8192);
    // Pre-fill DATA sectors with 0xAA (skip MBR sector 0 and let the editor
    // populate the partition table cleanly). Filling 0xAA into the partition
    // table area at offset 446..509 would create phantom entries that look
    // like 4 full primary slots.
    for (var i = SectorSize; i < disk.Length; i++) disk[i] = 0xAA;
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 4096L * SectorSize, PartitionType.ExtendedLba, null);
    // Second logical so the EBR sector preceding its data can be tested.
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L1");
    editor.AddLogicalPartition(500L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, "L2");

    // Find L2's index.
    var parts2 = editor.ListPartitions();
    var idx = -1;
    for (var i = 0; i < parts2.Count; ++i)
      if (parts2[i].Source == "EBR" && parts2[i].StartOffset == 500L * SectorSize) { idx = i; break; }
    Assert.That(idx, Is.GreaterThanOrEqualTo(0));

    // Pre-fill L2's data region after editor writes so leftovers can be checked.
    var dataStart = 500 * SectorSize;
    var dataLen = 256 * SectorSize;
    ms.Position = dataStart;
    var fillCc = new byte[dataLen];
    Array.Fill<byte>(fillCc, 0xCC);
    ms.Write(fillCc, 0, dataLen);

    editor.PurgePartition(idx);

    var bytes = ms.ToArray();
    // EBR sector (LBA 499 = byte 499*512) should be zero.
    for (var i = 0; i < SectorSize; i++)
      Assert.That(bytes[499 * SectorSize + i], Is.EqualTo((byte)0x00),
        $"EBR byte {i} should be zero");
    // Data sector should be zero.
    for (var i = 0; i < dataLen; i++)
      Assert.That(bytes[dataStart + i], Is.EqualTo((byte)0x00),
        $"Data byte {i} should be zero");
  }

  [Test, Category("HappyPath")]
  public void MbrLogicalChain_VerifyPassesAfterEdits() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    editor.AddPartition(64L * SectorSize, 6000L * SectorSize, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(65L * SectorSize, 256L * SectorSize, PartitionType.Linux, "L1");
    editor.AddLogicalPartition(400L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, "L2");
    editor.AddLogicalPartition(800L * SectorSize, 256L * SectorSize, PartitionType.Fat32Lba, "L3");

    var verification = editor.Verify();
    Assert.That(verification.IsValid, Is.True, $"Verification failed: {string.Join("; ", verification.Issues)}");
  }

  [Test, Category("HappyPath")]
  public void PrimaryAndLogical_BothExist_PrimaryWritesToMbrSlot() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    // Primary at LBA 8.
    editor.AddPartition(8L * SectorSize, 32L * SectorSize, PartitionType.Linux, null);
    // Extended container at LBA 64.
    editor.AddPartition(64L * SectorSize, 4096L * SectorSize, PartitionType.ExtendedLba, null);
    editor.AddLogicalPartition(65L * SectorSize, 128L * SectorSize, PartitionType.Linux, "L1");

    var bytes = ms.ToArray();
    // MBR slot 0: primary @ LBA 8.
    var lba0 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1BE + 8));
    var typ0 = bytes[0x1BE + 4];
    Assert.That(lba0, Is.EqualTo((uint)8));
    Assert.That(typ0, Is.EqualTo((byte)0x83));

    // MBR slot 1: extended @ LBA 64.
    var lba1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1BE + 16 + 8));
    var typ1 = bytes[0x1BE + 16 + 4];
    Assert.That(lba1, Is.EqualTo((uint)64));
    Assert.That(typ1, Is.EqualTo((byte)0x0F));

    // First EBR sits at container start (LBA 64). Its entry points to LBA 1 (relative).
    var ebrTypByte = bytes[64 * SectorSize + 0x1BE + 4];
    var ebrRelativeLba = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(64 * SectorSize + 0x1BE + 8));
    Assert.That(ebrTypByte, Is.EqualTo((byte)0x83));
    Assert.That(ebrRelativeLba, Is.EqualTo((uint)1));
  }
}
