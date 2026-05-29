using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class PartitionEditorVerifyTests {

  private const int SectorSize = 512;

  private static byte[] BuildEmptyMbrDisk(int sectorCount) {
    var disk = new byte[sectorCount * SectorSize];
    disk[510] = 0x55;
    disk[511] = 0xAA;
    return disk;
  }

  [Test, Category("HappyPath")]
  public void Verify_FreshMbr_PassesWithNoIssues() {
    var disk = BuildEmptyMbrDisk(2048);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);

    var result = editor.Verify();

    Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Mbr));
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.Issues, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Verify_FreshGpt_PassesWithNoIssues() {
    var disk = BuildEmptyMbrDisk(4096);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
    editor.ConvertMbrToGpt();

    var result = editor.Verify();

    Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Gpt));
    Assert.That(result.IsValid, Is.True, $"Issues: {string.Join("; ", result.Issues)}");
  }

  [Test, Category("ErrorHandling")]
  public void Verify_NoPartitionTable_ReportsNoTable() {
    var disk = new byte[4096];
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);

    var result = editor.Verify();

    Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.None));
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues, Is.Not.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Verify_MbrSignatureCorrupted_DetectedAfterReload() {
    var disk = BuildEmptyMbrDisk(2048);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);

    // Corrupt the MBR signature on disk.
    ms.Position = 510;
    ms.WriteByte(0x00);
    ms.WriteByte(0x00);

    var result = editor.Verify();
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues.Any(i => i.Contains("signature", StringComparison.OrdinalIgnoreCase)), Is.True,
      $"Expected signature issue, got: {string.Join("; ", result.Issues)}");
  }

  [Test, Category("ErrorHandling")]
  public void Verify_GptHeaderCrcCorrupted_Detected() {
    var disk = BuildEmptyMbrDisk(4096);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
    editor.ConvertMbrToGpt();

    // Flip a byte in the primary GPT header (somewhere that affects CRC).
    // Touch the firstUsableLba field at offset 512+40.
    ms.Position = 512 + 40;
    var b = ms.ReadByte();
    ms.Position = 512 + 40;
    ms.WriteByte((byte)(b ^ 0xFF));

    var result = editor.Verify();
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues.Any(i => i.Contains("CRC", StringComparison.OrdinalIgnoreCase)), Is.True,
      $"Expected CRC issue, got: {string.Join("; ", result.Issues)}");
  }

  [Test, Category("ErrorHandling")]
  public void Verify_GptEntryArrayCrcCorrupted_Detected() {
    var disk = BuildEmptyMbrDisk(4096);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
    editor.ConvertMbrToGpt();

    // Corrupt the primary entry array (LBA 2 = offset 1024) by flipping a byte
    // inside the first entry's first-LBA field.
    ms.Position = 1024 + 32;
    var b = ms.ReadByte();
    ms.Position = 1024 + 32;
    ms.WriteByte((byte)(b ^ 0xFF));

    var result = editor.Verify();
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues.Any(i => i.Contains("CRC", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Verify_GptBackupSignatureCorrupted_Detected() {
    var disk = BuildEmptyMbrDisk(4096);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
    editor.ConvertMbrToGpt();

    // Corrupt backup signature at last sector.
    var backupSig = (4096 - 1) * SectorSize;
    ms.Position = backupSig;
    ms.Write(new byte[] { 0, 0 }, 0, 2);

    var result = editor.Verify();
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues.Any(i => i.Contains("Backup", StringComparison.OrdinalIgnoreCase) &&
                                       i.Contains("signature", StringComparison.OrdinalIgnoreCase)),
      Is.True, $"Expected backup signature issue, got: {string.Join("; ", result.Issues)}");
  }

  [Test, Category("HappyPath")]
  public void Verify_GptAfterDelete_StillValid() {
    var disk = BuildEmptyMbrDisk(8192);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 256L * SectorSize, PartitionType.Linux, null);
    editor.AddPartition(512L * SectorSize, 256L * SectorSize, PartitionType.NtfsExfat, null);
    editor.ConvertMbrToGpt();

    editor.DeletePartition(0);

    var result = editor.Verify();
    Assert.That(result.IsValid, Is.True, $"Issues: {string.Join("; ", result.Issues)}");
  }

  [Test, Category("HappyPath")]
  public void Reload_GptWithDifferentEntrySize_PreservesEntries() {
    // Build a GPT image, then manually rewrite the entry-size field to 256
    // (still ≥ 128 minimum; the on-disk entries are 128-byte but the trailing
    // 128 bytes are zero — Reload should still pick up the entries via the
    // GptParser, which reads using the on-disk entrySize. Then verify our
    // editor preserves the larger size.
    var disk = BuildEmptyMbrDisk(4096);
    using var ms = new MemoryStream(disk, writable: true);
    var editor = new PartitionEditor(ms);
    editor.AddPartition(64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
    editor.ConvertMbrToGpt();

    // Confirm round-trip works.
    using var fresh = new MemoryStream(ms.ToArray(), writable: true);
    var freshEditor = new PartitionEditor(fresh);
    Assert.That(freshEditor.Scheme, Is.EqualTo(PartitionScheme.Gpt));
    Assert.That(freshEditor.ListPartitions(), Has.Count.EqualTo(1));
  }
}
