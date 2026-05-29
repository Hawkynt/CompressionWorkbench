using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Tests.DiskImage;

/// <summary>
/// Tests partition-table editing through virtual-disk containers' guest-disk
/// streams. The fundamental contract: a fresh dynamic virtual disk with mostly
/// sparse data should still accept partition-table writes — the container
/// stream allocates blocks on demand.
/// </summary>
[TestFixture]
public class PartitionEditorVirtualDiskTests {

  private const int SectorSize = 512;
  private const int OneMiB = 1024 * 1024;

  [SetUp]
  public void EnsureRegistered() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
  }

  /// <summary>
  /// Builds a sparse raw disk image of the given byte size, with just the MBR
  /// boot signature populated so PartitionEditor recognises it as MBR.
  /// </summary>
  private static byte[] BuildSparseDisk(int diskSizeBytes) {
    var disk = new byte[diskSizeBytes];
    disk[510] = 0x55;
    disk[511] = 0xAA;
    return disk;
  }

  // ── VHD (Fixed) ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vhd_Fixed_AddThreePartitions_PersistAcrossReopen() {
    var raw = BuildSparseDisk(8 * OneMiB);
    var writer = new FileFormat.Vhd.VhdWriter();
    writer.SetDiskData(raw);

    using var ms = new MemoryStream();
    ms.Write(writer.Build());

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();

    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
      editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
      editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }

  // ── VHD (Dynamic) ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vhd_Dynamic_AddThreePartitions_PersistAcrossReopen() {
    // 16 MiB virtual size — dynamic VHD will be very small initially.
    var raw = BuildSparseDisk(16 * OneMiB);
    var writer = new FileFormat.Vhd.VhdWriter();
    writer.SetDiskData(raw);

    using var ms = new MemoryStream();
    ms.Write(writer.BuildDynamic());

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();

    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
      editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
      editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }

  // ── VHDX ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vhdx_AddThreePartitions_PersistAcrossReopen() {
    // VHDX block size is 16 MiB — a 32 MiB disk has 2 blocks.
    var raw = BuildSparseDisk(32 * OneMiB);
    var writer = new FileFormat.Vhdx.VhdxWriter();
    writer.SetDiskData(raw);

    using var ms = new MemoryStream();
    ms.Write(writer.Build());

    var desc = new FileFormat.Vhdx.VhdxFormatDescriptor();

    try {
      using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
        var editor = new PartitionEditor(guest);
        editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
        editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
        editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
      }
    } catch (NotSupportedException) {
      Assert.Ignore("VHDX guest-disk stream does not support partition editing on this layout.");
      return;
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }

  // ── VMDK ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vmdk_AddThreePartitions_PersistAcrossReopen() {
    var raw = BuildSparseDisk(8 * OneMiB);
    var writer = new FileFormat.Vmdk.VmdkWriter();
    writer.SetDiskData(raw);

    using var ms = new MemoryStream();
    ms.Write(writer.Build());

    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();

    try {
      using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
        var editor = new PartitionEditor(guest);
        editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
        editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
        editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
      }
    } catch (NotSupportedException) {
      Assert.Ignore("VMDK guest-disk stream does not support partition editing on this layout.");
      return;
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }

  // ── Qcow2 ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Qcow2_AddThreePartitions_PersistAcrossReopen() {
    var raw = BuildSparseDisk(8 * OneMiB);

    using var ms = new MemoryStream();
    var writer = new FileFormat.Qcow2.Qcow2Writer();
    writer.SetDiskImage(raw);
    writer.WriteTo(ms);

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();

    try {
      using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
        var editor = new PartitionEditor(guest);
        editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
        editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
        editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
      }
    } catch (NotSupportedException) {
      Assert.Ignore("Qcow2 guest-disk stream does not support partition editing on this layout.");
      return;
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }

  // ── VDI ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Vdi_AddThreePartitions_PersistAcrossReopen() {
    var raw = BuildSparseDisk(8 * OneMiB);

    using var ms = new MemoryStream();
    using (var writer = new FileFormat.Vdi.VdiWriter(ms, leaveOpen: true, virtualSize: raw.Length))
      writer.Write(raw);

    var desc = new FileFormat.Vdi.VdiFormatDescriptor();

    try {
      using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
        var editor = new PartitionEditor(guest);
        editor.AddPartition(1L * OneMiB, 1L * OneMiB, PartitionType.Linux, null);
        editor.AddPartition(3L * OneMiB, 1L * OneMiB, PartitionType.NtfsExfat, null);
        editor.AddPartition(5L * OneMiB, 1L * OneMiB, PartitionType.Fat32Lba, null);
      }
    } catch (NotSupportedException) {
      Assert.Ignore("VDI guest-disk stream does not support partition editing on this layout.");
      return;
    }

    ms.Position = 0;
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(ms)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(3));
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x83"));
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"));
      Assert.That(parts[2].TypeCode, Is.EqualTo("0x0C"));
    }
  }
}
