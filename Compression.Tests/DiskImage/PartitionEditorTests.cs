using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class PartitionEditorTests {

  [SetUp]
  public void EnsureRegistered() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
  }

  private const int SectorSize = 512;

  /// <summary>
  /// Builds a blank disk image of the given size (in sectors) with a valid
  /// MBR boot signature but no partition entries.
  /// </summary>
  private static byte[] BuildEmptyMbrDisk(int sectorCount) {
    var disk = new byte[sectorCount * SectorSize];
    disk[510] = 0x55;
    disk[511] = 0xAA;
    return disk;
  }

  /// <summary>
  /// Builds a disk with a single primary MBR partition (FAT12 by default).
  /// </summary>
  private static byte[] BuildMbrDiskWithPartition(
    int sectorCount, uint partLba, uint partSectors, byte typeByte = 0x01) {
    var disk = BuildEmptyMbrDisk(sectorCount);
    const int entryOff = 0x1BE;
    disk[entryOff + 0] = 0x80; // active
    disk[entryOff + 1] = 0xFE; disk[entryOff + 2] = 0xFF; disk[entryOff + 3] = 0xFF;
    disk[entryOff + 4] = typeByte;
    disk[entryOff + 5] = 0xFE; disk[entryOff + 6] = 0xFF; disk[entryOff + 7] = 0xFF;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff + 8), partLba);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff + 12), partSectors);
    return disk;
  }

  // ── Listing ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ListPartitions_OnMbrDisk_ReturnsExistingEntries() {
    var disk = BuildMbrDiskWithPartition(sectorCount: 2048, partLba: 63, partSectors: 1024, typeByte: 0x07);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    var partitions = editor.ListPartitions();

    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Mbr));
    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].StartOffset, Is.EqualTo(63L * SectorSize));
    Assert.That(partitions[0].Size, Is.EqualTo(1024L * SectorSize));
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0x07"));
  }

  [Test, Category("HappyPath")]
  public void ListPartitions_OnBlankDisk_ReturnsEmptyAndScheme_None() {
    var disk = new byte[4096];
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);

    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.None));
    Assert.That(editor.ListPartitions(), Is.Empty);
  }

  // ── AddPartition ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void AddPartition_OnEmptyMbr_AppearsAfterReload() {
    var disk = BuildEmptyMbrDisk(2048);
    using var ms = new MemoryStream(disk, writable: true) { Capacity = disk.Length };

    var editor = new PartitionEditor(ms);
    editor.AddPartition(63L * SectorSize, 512L * SectorSize, PartitionType.Linux, label: null);

    // Re-read from raw bytes to make sure it was written, not just held in memory.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    var partitions = freshEditor.ListPartitions();

    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].StartOffset, Is.EqualTo(63L * SectorSize));
    Assert.That(partitions[0].Size, Is.EqualTo(512L * SectorSize));
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0x83")); // Linux
  }

  [Test, Category("HappyPath")]
  public void AddPartition_PromotesUntableDiskToMbr() {
    var disk = new byte[4096];
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.None));

    editor.AddPartition(SectorSize, 2L * SectorSize, PartitionType.Fat12, null);

    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Mbr));
    Assert.That(editor.ListPartitions(), Has.Count.EqualTo(1));
    // Verify MBR signature now exists at byte 510.
    Assert.That(ms.ToArray()[510], Is.EqualTo((byte)0x55));
    Assert.That(ms.ToArray()[511], Is.EqualTo((byte)0xAA));
  }

  [Test, Category("ErrorHandling")]
  public void AddPartition_OverlappingExisting_Throws() {
    var disk = BuildMbrDiskWithPartition(2048, partLba: 100, partSectors: 200);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);

    Assert.That(() => editor.AddPartition(150L * SectorSize, 100L * SectorSize, PartitionType.Linux, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void AddPartition_ExceedingDiskLength_Throws() {
    var disk = BuildEmptyMbrDisk(100);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    Assert.That(() => editor.AddPartition(0, 200L * SectorSize, PartitionType.Linux, null),
      Throws.InvalidOperationException);
  }

  [Test, Category("ErrorHandling")]
  public void AddPartition_UnalignedStart_Throws() {
    var disk = BuildEmptyMbrDisk(2048);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    Assert.That(() => editor.AddPartition(123, SectorSize, PartitionType.Linux, null),
      Throws.ArgumentException);
  }

  // ── DeletePartition / PurgePartition ───────────────────────────────

  [Test, Category("HappyPath")]
  public void DeletePartition_RemovesEntryFromTable() {
    var disk = BuildMbrDiskWithPartition(2048, partLba: 63, partSectors: 512);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    Assume.That(editor.ListPartitions(), Has.Count.EqualTo(1));

    editor.DeletePartition(0);

    Assert.That(editor.ListPartitions(), Is.Empty);
    // Re-parse to confirm.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    Assert.That(freshEditor.ListPartitions(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void PurgePartition_ZerosThePartitionBytes() {
    var disk = BuildMbrDiskWithPartition(2048, partLba: 64, partSectors: 8);
    // Write known non-zero bytes into the partition area.
    var partStart = 64 * SectorSize;
    for (var i = 0; i < 8 * SectorSize; ++i) disk[partStart + i] = 0xAA;
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    editor.PurgePartition(0);

    var bytes = ms.ToArray();
    for (var i = 0; i < 8 * SectorSize; ++i)
      Assert.That(bytes[partStart + i], Is.EqualTo((byte)0x00),
        $"byte {i} was not zeroed");
    Assert.That(editor.ListPartitions(), Is.Empty);
  }

  // ── ConvertMbrToGpt ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ConvertMbrToGpt_PreservesPartitionGeometry() {
    // Use 4096 sectors so GPT layout (34 + 33 reserved) has room.
    var disk = BuildMbrDiskWithPartition(4096, partLba: 2048, partSectors: 1024, typeByte: 0x83);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    editor.ConvertMbrToGpt();

    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Gpt));

    // Re-read from a fresh editor to confirm the on-disk GPT is valid.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);

    Assert.That(freshEditor.Scheme, Is.EqualTo(PartitionScheme.Gpt));
    var partitions = freshEditor.ListPartitions();
    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].StartOffset, Is.EqualTo(2048L * SectorSize));
    Assert.That(partitions[0].Size, Is.EqualTo(1024L * SectorSize));
    // MBR 0x83 (Linux) → GPT 0FC63DAF-… (Linux Filesystem)
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0FC63DAF-8483-4772-8E79-3D69D8477DE4"));
  }

  [Test, Category("HappyPath")]
  public void ConvertMbrToGpt_WritesProtectiveMbr() {
    var disk = BuildMbrDiskWithPartition(4096, partLba: 2048, partSectors: 1024, typeByte: 0x07);
    using var ms = new MemoryStream(disk, writable: true);

    new PartitionEditor(ms).ConvertMbrToGpt();

    var bytes = ms.ToArray();
    // Protective MBR partition entry at 0x1BE should have type 0xEE.
    Assert.That(bytes[0x1BE + 4], Is.EqualTo((byte)0xEE));
    // GPT signature at LBA 1.
    var sig = System.Text.Encoding.ASCII.GetString(bytes, 512, 8);
    Assert.That(sig, Is.EqualTo("EFI PART"));
  }

  [Test, Category("HappyPath")]
  public void ConvertMbrToGpt_BackupHeaderAtDiskEnd() {
    var totalSectors = 4096;
    var disk = BuildMbrDiskWithPartition(totalSectors, partLba: 2048, partSectors: 1024);
    using var ms = new MemoryStream(disk, writable: true);

    new PartitionEditor(ms).ConvertMbrToGpt();

    var bytes = ms.ToArray();
    var backupHeaderOffset = (totalSectors - 1) * SectorSize;
    var sig = System.Text.Encoding.ASCII.GetString(bytes, backupHeaderOffset, 8);
    Assert.That(sig, Is.EqualTo("EFI PART"));
  }

  // ── ConvertGptToMbr ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ConvertGptToMbr_RoundTripsPartitionGeometry() {
    var disk = BuildMbrDiskWithPartition(4096, partLba: 2048, partSectors: 1024, typeByte: 0x83);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    editor.ConvertMbrToGpt();
    editor.ConvertGptToMbr();

    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Mbr));
    var partitions = editor.ListPartitions();
    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].StartOffset, Is.EqualTo(2048L * SectorSize));
    Assert.That(partitions[0].Size, Is.EqualTo(1024L * SectorSize));
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0x83"));
  }

  // ── FormatPartition ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FormatPartition_Fat_WritesValidFatBootSectorIntoPartition() {
    // FAT12 1.44MB image fits in ~2880 sectors. Give the disk plenty of room.
    const int totalSectors = 4096;
    var disk = BuildEmptyMbrDisk(totalSectors);
    using var ms = new MemoryStream(disk, writable: true);

    var editor = new PartitionEditor(ms);
    // Partition starts at LBA 64, size big enough for a 1.44MB FAT image.
    const long partStart = 64L * SectorSize;
    const long partSize = 3000L * SectorSize;
    editor.AddPartition(partStart, partSize, PartitionType.Fat12, label: null);

    editor.FormatPartition(0, "Fat", new FormatCreateOptions());

    var bytes = ms.ToArray();
    // The FAT boot sector should end with the 0x55 0xAA signature
    // 510 bytes into the partition's first sector.
    Assert.That(bytes[partStart + 510], Is.EqualTo((byte)0x55),
      "FAT boot sector signature byte 510 wrong");
    Assert.That(bytes[partStart + 511], Is.EqualTo((byte)0xAA),
      "FAT boot sector signature byte 511 wrong");
    // FAT12 BPB has bytes-per-sector field at offset 11 = 512 = 0x00 0x02.
    Assert.That(bytes[partStart + 11], Is.EqualTo((byte)0x00));
    Assert.That(bytes[partStart + 12], Is.EqualTo((byte)0x02));
  }

  [Test, Category("ErrorHandling")]
  public void FormatPartition_UnknownFormat_Throws() {
    var disk = BuildMbrDiskWithPartition(2048, partLba: 64, partSectors: 1024);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    Assert.That(() => editor.FormatPartition(0, "NonexistentFormatXyz", new FormatCreateOptions()),
      Throws.InvalidOperationException);
  }

  // ── Type mapping ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void TypeMapping_KnownMbrBytes_RoundTrip() {
    foreach (PartitionType type in new[] {
               PartitionType.Fat12, PartitionType.Fat16, PartitionType.Fat32Lba,
               PartitionType.NtfsExfat, PartitionType.Linux, PartitionType.LinuxSwap,
               PartitionType.LinuxLvm, PartitionType.AppleHfsPlus, PartitionType.AppleUfs,
               PartitionType.EfiSystem
             }) {
      var b = PartitionTypeMapping.ToMbrByte(type);
      var back = PartitionTypeMapping.FromMbrByte(b);
      Assert.That(back, Is.EqualTo(type), $"MBR round-trip failed for {type}");
    }
  }

  [Test, Category("HappyPath")]
  public void TypeMapping_KnownGptGuids_RoundTrip() {
    foreach (PartitionType type in new[] {
               PartitionType.MicrosoftBasicData, PartitionType.MicrosoftReserved,
               PartitionType.Linux, PartitionType.LinuxSwap, PartitionType.LinuxLvm,
               PartitionType.AppleHfsPlus, PartitionType.AppleApfs,
               PartitionType.EfiSystem, PartitionType.BiosBoot
             }) {
      var guid = PartitionTypeMapping.ToGptGuid(type);
      var back = PartitionTypeMapping.FromGptGuid(guid);
      Assert.That(back, Is.EqualTo(type), $"GPT round-trip failed for {type}");
    }
  }

  // ── IPartitionEditable plumbing ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vhd_AdvertisesIPartitionEditable() {
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IPartitionEditable>());
  }

  [Test, Category("HappyPath")]
  public void Vhd_OpenGuestDiskStream_ExposesPartitionTable() {
    // Build a small raw disk with one FAT12 MBR partition.
    var rawDisk = BuildMbrDiskWithPartition(2048, partLba: 63, partSectors: 1024, typeByte: 0x01);

    // Wrap as a fixed VHD.
    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(rawDisk);
    var vhdBytes = vhdWriter.Build();

    using var ms = new MemoryStream();
    ms.Write(vhdBytes);

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    using var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms);

    var editor = new PartitionEditor(guest);
    Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Mbr));
    Assert.That(editor.ListPartitions(), Has.Count.EqualTo(1));
    Assert.That(editor.ListPartitions()[0].TypeCode, Is.EqualTo("0x01"));
  }

  [Test, Category("HappyPath")]
  public void Vhd_PartitionEditor_AddThenReload_RoundTrips() {
    // Build a 2 MiB raw disk (no partitions yet) and wrap in fixed VHD.
    var rawDisk = BuildEmptyMbrDisk(4096); // 2 MiB

    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(rawDisk);
    var vhdBytes = vhdWriter.Build();

    using var ms = new MemoryStream();
    ms.Write(vhdBytes);

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();

    // First open: add a partition.
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      editor.AddPartition(64L * SectorSize, 1024L * SectorSize, PartitionType.NtfsExfat, null);
    }

    // Reopen and verify.
    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(1));
      Assert.That(parts[0].StartOffset, Is.EqualTo(64L * SectorSize));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x07"));
    }
  }

  [Test, Category("HappyPath")]
  public void OtherVirtualDiskFormats_AdvertiseIPartitionEditable() {
    Assert.That(new FileFormat.Vhdx.VhdxFormatDescriptor(), Is.InstanceOf<IPartitionEditable>());
    Assert.That(new FileFormat.Vmdk.VmdkFormatDescriptor(), Is.InstanceOf<IPartitionEditable>());
    Assert.That(new FileFormat.Qcow2.Qcow2FormatDescriptor(), Is.InstanceOf<IPartitionEditable>());
    Assert.That(new FileFormat.Vdi.VdiFormatDescriptor(), Is.InstanceOf<IPartitionEditable>());
  }
}
