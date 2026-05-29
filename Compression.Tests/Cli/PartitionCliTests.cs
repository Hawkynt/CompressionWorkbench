using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Lib;

namespace Compression.Tests.Cli;

/// <summary>
/// Verifies the partition-editor CLI commands by driving
/// <see cref="PartitionOperations"/> directly against temp raw images.
/// (The dispatch surface in <c>Program.cs</c> is a thin shell around these
/// helpers, so testing the helpers covers the same code paths the CLI uses.)
/// </summary>
[TestFixture]
public class PartitionCliTests {

  private const int SectorSize = 512;

  [SetUp]
  public void EnsureRegistered() => FormatRegistration.EnsureInitialized();

  /// <summary>
  /// Writes a sparse raw disk image of the given byte size with just the MBR
  /// boot signature populated, then returns its absolute path. The file is
  /// created with a synthetic extension so <see cref="FormatDetector"/>
  /// returns <c>Unknown</c> and <see cref="PartitionOperations"/> falls back
  /// to the raw-stream path (rather than picking up a filesystem descriptor
  /// for an empty image).
  /// </summary>
  private static string CreateRawMbrImage(int sectorCount) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-partcli-{Guid.NewGuid():N}.partcli-test");
    var disk = new byte[sectorCount * SectorSize];
    disk[510] = 0x55;
    disk[511] = 0xAA;
    File.WriteAllBytes(path, disk);
    return path;
  }

  private static string CreateBlankImage(int sectorCount) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-partcli-{Guid.NewGuid():N}.partcli-test");
    File.WriteAllBytes(path, new byte[sectorCount * SectorSize]);
    return path;
  }

  // ── List ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_OnPrePopulatedMbrImage_ReturnsExistingEntries() {
    // Build a disk with a single primary partition baked into the MBR table.
    var path = CreateRawMbrImage(2048);
    try {
      var disk = File.ReadAllBytes(path);
      const int entryOff = 0x1BE;
      disk[entryOff + 0] = 0x80;
      disk[entryOff + 1] = 0xFE; disk[entryOff + 2] = 0xFF; disk[entryOff + 3] = 0xFF;
      disk[entryOff + 4] = 0x83; // Linux
      disk[entryOff + 5] = 0xFE; disk[entryOff + 6] = 0xFF; disk[entryOff + 7] = 0xFF;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff + 8), 63u);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff + 12), 1024u);
      File.WriteAllBytes(path, disk);

      var result = PartitionOperations.List(path);

      Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Mbr));
      Assert.That(result.Partitions, Has.Count.EqualTo(1));
      Assert.That(result.Partitions[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(result.Partitions[0].StartOffset, Is.EqualTo(63L * SectorSize));
      Assert.That(result.Partitions[0].Size, Is.EqualTo(1024L * SectorSize));
      Assert.That(result.Partitions[0].IsActive, Is.True);
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("HappyPath")]
  public void List_OnBlankImage_ReturnsNoneScheme_NoPartitions() {
    var path = CreateBlankImage(4096);
    try {
      var result = PartitionOperations.List(path);
      Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.None));
      Assert.That(result.Partitions, Is.Empty);
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Add → List ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_ThenList_RoundTripsThroughDisk() {
    var path = CreateRawMbrImage(8192);
    try {
      PartitionOperations.Add(path, 64L * SectorSize, 1024L * SectorSize, PartitionType.Linux, label: null);
      PartitionOperations.Add(path, 2048L * SectorSize, 1024L * SectorSize, PartitionType.Fat32Lba, label: null);

      var result = PartitionOperations.List(path);

      Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Mbr));
      Assert.That(result.Partitions, Has.Count.EqualTo(2));
      Assert.That(result.Partitions[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(result.Partitions[0].StartOffset, Is.EqualTo(64L * SectorSize));
      Assert.That(result.Partitions[1].TypeCode, Is.EqualTo("0x0C"));
      Assert.That(result.Partitions[1].StartOffset, Is.EqualTo(2048L * SectorSize));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("HappyPath")]
  public void Add_AcceptsAliasedTypeNames() {
    var path = CreateRawMbrImage(4096);
    try {
      var t1 = PartitionOperations.ParseType("fat32");
      Assert.That(t1, Is.EqualTo(PartitionType.Fat32Lba));
      var t2 = PartitionOperations.ParseType("ntfs");
      Assert.That(t2, Is.EqualTo(PartitionType.NtfsExfat));
      var t3 = PartitionOperations.ParseType("ext4");
      Assert.That(t3, Is.EqualTo(PartitionType.Linux));
      var t4 = PartitionOperations.ParseType("EFI");
      Assert.That(t4, Is.EqualTo(PartitionType.EfiSystem));
      var t5 = PartitionOperations.ParseType("Linux"); // direct enum-name path
      Assert.That(t5, Is.EqualTo(PartitionType.Linux));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("EdgeCase")]
  public void ParseType_OnUnknownToken_Throws() {
    Assert.That(() => PartitionOperations.ParseType("bogus-fs"), Throws.ArgumentException);
  }

  // ── Delete ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Delete_ByIndex_RemovesEntryFromTable() {
    var path = CreateRawMbrImage(4096);
    try {
      PartitionOperations.Add(path, 64L * SectorSize, 512L * SectorSize, PartitionType.Linux, null);
      PartitionOperations.Add(path, 1024L * SectorSize, 512L * SectorSize, PartitionType.Fat32Lba, null);
      Assert.That(PartitionOperations.List(path).Partitions, Has.Count.EqualTo(2));

      PartitionOperations.Delete(path, 0);

      var result = PartitionOperations.List(path);
      Assert.That(result.Partitions, Has.Count.EqualTo(1));
      Assert.That(result.Partitions[0].TypeCode, Is.EqualTo("0x0C"));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Purge ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Purge_ZeroFillsBytesAndRemovesEntry() {
    var path = CreateRawMbrImage(4096);
    try {
      PartitionOperations.Add(path, 64L * SectorSize, 256L * SectorSize, PartitionType.Linux, null);

      // Write a sentinel into the partition data range so we can confirm it is
      // zeroed by purge.
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
        fs.Position = 64L * SectorSize;
        fs.WriteByte(0xAB);
        fs.WriteByte(0xCD);
      }

      PartitionOperations.Purge(path, 0);

      var listed = PartitionOperations.List(path);
      Assert.That(listed.Partitions, Is.Empty);

      using var verify = new FileStream(path, FileMode.Open, FileAccess.Read);
      verify.Position = 64L * SectorSize;
      Assert.That(verify.ReadByte(), Is.EqualTo(0));
      Assert.That(verify.ReadByte(), Is.EqualTo(0));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Convert MBR <-> GPT ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Convert_MbrToGpt_ChangesScheme_PreservesPartitions() {
    // GPT needs (34 + 33) sectors of overhead; give us comfortably more.
    var path = CreateRawMbrImage(8192);
    try {
      PartitionOperations.Add(path, 256L * SectorSize, 1024L * SectorSize, PartitionType.Linux, null);
      PartitionOperations.Add(path, 2048L * SectorSize, 1024L * SectorSize, PartitionType.Fat32Lba, null);

      PartitionOperations.Convert(path, PartitionScheme.Gpt);

      var result = PartitionOperations.List(path);
      Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Gpt));
      Assert.That(result.Partitions, Has.Count.EqualTo(2));
      Assert.That(result.Partitions.All(p => p.Source == "GPT"), Is.True);

      // Verify backup GPT and CRCs are intact.
      var verification = PartitionOperations.Verify(path);
      Assert.That(verification.Scheme, Is.EqualTo(PartitionScheme.Gpt));
      Assert.That(verification.IsValid, Is.True, "GPT integrity: " + string.Join("; ", verification.Issues));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("HappyPath")]
  public void Convert_GptToMbr_RoundTripsBackToMbr() {
    var path = CreateRawMbrImage(8192);
    try {
      PartitionOperations.Add(path, 256L * SectorSize, 1024L * SectorSize, PartitionType.Linux, null);
      PartitionOperations.Convert(path, PartitionScheme.Gpt);
      Assert.That(PartitionOperations.List(path).Scheme, Is.EqualTo(PartitionScheme.Gpt));

      PartitionOperations.Convert(path, PartitionScheme.Mbr);

      var result = PartitionOperations.List(path);
      Assert.That(result.Scheme, Is.EqualTo(PartitionScheme.Mbr));
      Assert.That(result.Partitions, Has.Count.EqualTo(1));
      Assert.That(result.Partitions[0].StartOffset, Is.EqualTo(256L * SectorSize));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("EdgeCase")]
  public void Convert_NoOp_WhenAlreadyInTargetScheme() {
    var path = CreateRawMbrImage(2048);
    try {
      PartitionOperations.Add(path, 64L * SectorSize, 256L * SectorSize, PartitionType.Linux, null);
      Assert.DoesNotThrow(() => PartitionOperations.Convert(path, PartitionScheme.Mbr));
      Assert.That(PartitionOperations.List(path).Scheme, Is.EqualTo(PartitionScheme.Mbr));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("ErrorPath")]
  public void ParseScheme_UnknownValue_Throws() {
    Assert.That(() => PartitionOperations.ParseScheme("apm"), Throws.ArgumentException);
  }

  // ── Verify ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Verify_OnFreshMbrAfterAdd_ReportsOk() {
    var path = CreateRawMbrImage(4096);
    try {
      PartitionOperations.Add(path, 64L * SectorSize, 256L * SectorSize, PartitionType.Linux, null);
      var verification = PartitionOperations.Verify(path);
      Assert.That(verification.Scheme, Is.EqualTo(PartitionScheme.Mbr));
      Assert.That(verification.IsValid, Is.True, "MBR integrity: " + string.Join("; ", verification.Issues));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test, Category("EdgeCase")]
  public void Verify_OnBlankImage_ReportsNoScheme() {
    var path = CreateBlankImage(4096);
    try {
      var verification = PartitionOperations.Verify(path);
      Assert.That(verification.Scheme, Is.EqualTo(PartitionScheme.None));
      Assert.That(verification.IsValid, Is.False);
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }
}
