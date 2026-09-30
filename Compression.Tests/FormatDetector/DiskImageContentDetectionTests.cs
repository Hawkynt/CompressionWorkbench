#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;
using Det = Compression.Lib.FormatDetector;

namespace Compression.Tests.FormatDetectorTests;

/// <summary>
/// A disk image's extension says nothing about what is inside it: ".img", ".bin", ".raw", ".dd"
/// or no extension at all may hold a whole partitioned disk, a bare filesystem, an optical image
/// or something else entirely. These tests pin that the bytes decide — partition table first
/// where one is really there, the filesystem where it sits at offset zero, the ISO 9660 view of
/// a hybrid image — and that bytes which prove nothing are not claimed.
/// </summary>
[TestFixture]
public class DiskImageContentDetectionTests {

  private static readonly byte[] HelloPayload = "Hello from inside a disk image"u8.ToArray();
  private static readonly byte[] ExtPayload = "ext payload"u8.ToArray();

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    FormatRegistration.EnsureInitialized();
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_diskdetect_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { /* best effort */ }
  }

  // ── image builders ──────────────────────────────────────────────────────

  private static byte[] BuildFat() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", HelloPayload);
    return w.Build(); // 1.44 MB FAT12 with a valid BPB
  }

  private static byte[] BuildExt() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("ext.txt", ExtPayload);
    return w.Build();
  }

  private static byte[] BuildIso() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("ISO.TXT", HelloPayload);
    return w.Build();
  }

  private static byte[] BuildUdf() {
    var w = new FileSystem.Udf.UdfWriter();
    w.AddFile("udf.txt", HelloPayload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

  /// <summary>Whole-disk image: GPT with a FAT partition and an ext partition, 1 MiB aligned.</summary>
  private static byte[] BuildGptDisk(byte[] first, byte[] second) {
    const long mib = 1024 * 1024;
    var firstLen = AlignUp(first.Length, 512);
    var secondStart = AlignUp(mib + firstLen, mib);
    var secondLen = AlignUp(second.Length, 512);
    var total = AlignUp(secondStart + secondLen, mib) + mib; // room for the backup GPT
    var disk = new byte[total];
    using (var ms = new MemoryStream(disk, writable: true)) {
      var editor = new PartitionEditor(ms);
      editor.AddPartition(mib, firstLen, PartitionType.Fat16Lba, "first");
      editor.AddPartition(secondStart, secondLen, PartitionType.Linux, "second");
      editor.ConvertMbrToGpt();
    }
    first.CopyTo(disk, (int)mib);
    second.CopyTo(disk, (int)secondStart);
    return disk;
  }

  /// <summary>Writes a primary MBR entry into <paramref name="sector0"/>.</summary>
  private static void WriteMbrEntry(byte[] sector0, int slot, byte status, byte type, uint lbaStart, uint sectors) {
    var e = sector0.AsSpan(446 + slot * 16, 16);
    e.Clear();
    e[0] = status;
    e[4] = type;
    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], lbaStart);
    BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sectors);
    sector0[510] = 0x55;
    sector0[511] = 0xAA;
  }

  private string Write(string name, byte[] data) {
    var path = Path.Combine(this._dir, name);
    File.WriteAllBytes(path, data);
    return path;
  }

  private static string Detected(string path) => Det.Detect(path).ToString();

  // ── whole-disk images ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_ImgWithMbrAndFatPartition_When_Detected_Then_IsPartitionedDiskAndPartitionBrowses() {
    var path = this.Write("disk.img", MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12));

    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
    var entries = ArchiveOperations.List(path, null);
    var hello = entries.SingleOrDefault(e => e.Name.EndsWith("HELLO.TXT", StringComparison.OrdinalIgnoreCase));
    Assert.That(hello, Is.Not.Null, string.Join(", ", entries.Select(e => e.Name)));
    Assert.That(hello!.Name, Does.StartWith("Partition1_"));
    Assert.That(ArchiveOperations.ExtractEntry(path, hello.Name, null), Is.EqualTo(HelloPayload));
  }

  [Test, Category("HappyPath")]
  public void Given_ImgWithGptAndFatPlusExtPartitions_When_Detected_Then_BothPartitionsBrowse() {
    var path = this.Write("gpt.img", BuildGptDisk(BuildFat(), BuildExt()));

    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
    var names = ArchiveOperations.List(path, null).Select(e => e.Name).ToList();
    Assert.That(names, Has.Some.Match("^Partition1_.*HELLO\\.TXT$"), string.Join(", ", names));
    Assert.That(names, Has.Some.Match("^Partition2_.*ext\\.txt$"), string.Join(", ", names));
  }

  [Test, Category("HappyPath")]
  public void Given_ExtensionlessMbrDisk_When_Detected_Then_IsPartitionedDisk() {
    var path = this.Write("disk", MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12));
    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
  }

  [TestCase(".raw")]
  [TestCase(".dd")]
  [TestCase(".bin")]
  [TestCase(".hdd")]
  [TestCase(".dsk")]
  [Category("HappyPath")]
  public void Given_GenericDiskExtensionOnGptDisk_When_Detected_Then_IsPartitionedDisk(string extension) {
    var path = this.Write("disk" + extension, BuildGptDisk(BuildFat(), BuildExt()));
    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
  }

  // ── bare filesystems ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_BareFatInImg_When_Detected_Then_IsFat() {
    var path = this.Write("floppy.img", BuildFat());
    Assert.That(Detected(path), Is.EqualTo("Fat"));
  }

  [Test, Category("HappyPath")]
  public void Given_BareExtInBin_When_Detected_Then_IsExtAndBrowses() {
    var path = this.Write("rootfs.bin", BuildExt());

    Assert.That(Detected(path), Is.EqualTo("Ext"));
    Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Has.Some.EndsWith("ext.txt"));
  }

  [Test, Category("HappyPath")]
  public void Given_ExtensionlessBareExt_When_Detected_Then_IsExt() {
    var path = this.Write("rootfs", BuildExt());
    Assert.That(Detected(path), Is.EqualTo("Ext"));
  }

  [Test, Category("HappyPath")]
  public void Given_IsoRenamedToImg_When_Detected_Then_IsIso() {
    var path = this.Write("cd.img", BuildIso());
    Assert.That(Detected(path), Is.EqualTo("Iso"));
  }

  [Test, Category("HappyPath")]
  public void Given_ExtensionlessIso_When_Detected_Then_IsIso() {
    var path = this.Write("cd", BuildIso());
    Assert.That(Detected(path), Is.EqualTo("Iso"));
  }

  [Test, Category("HappyPath")]
  public void Given_UdfOnlyImageNamedIso_When_Detected_Then_IsUdf() {
    var path = this.Write("dvd.iso", BuildUdf());
    Assert.That(Detected(path), Is.EqualTo("Udf"));
  }

  [Test, Category("HappyPath")]
  public void Given_HybridIsoWithValidMbrTable_When_Detected_Then_IsIso() {
    // isohybrid-style: the system area carries an MBR whose entry maps the ISO data,
    // so both interpretations are valid. The ISO 9660 view is the one with the files.
    var iso = BuildIso();
    WriteMbrEntry(iso, 0, 0x80, 0x17, 64, (uint)(iso.Length / 512 - 64));
    var path = this.Write("hybrid.img", iso);

    Assert.That(Detected(path), Is.EqualTo("Iso"));
  }

  [Test, Category("HappyPath")]
  public void Given_VhdWrappingMbrDisk_When_Detected_Then_IsVhdAndPartitionBrowses() {
    var writer = new FileFormat.Vhd.VhdWriter();
    writer.SetDiskData(MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12));
    var path = this.Write("disk.vhd", writer.Build());

    Assert.That(Detected(path), Is.EqualTo("Vhd"));
    Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Has.Some.Match("^Partition1_.*HELLO\\.TXT$"));
  }

  // ── nesting: the partition's own content is detected ────────────────────

  [Test, Category("HappyPath")]
  public void Given_MbrPartitionHoldingIso_When_Listed_Then_IsoDescriptorsBeyondFourKiBAreFound() {
    // ISO 9660 announces itself at 32 KiB into the partition; a partition probe that only
    // looks at the first 4 KiB reports the partition as raw bytes.
    var path = this.Write("cdpart.img", MbrWrapper.Wrap(BuildIso(), 0x17));

    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
    Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Has.Some.Match("^Partition1_.*ISO\\.TXT"));
  }

  [Test, Category("HappyPath")]
  public void Given_PartitionWithUnknownContent_When_RawEntryOpened_Then_StreamIsThePartitionWindow() {
    var payload = new byte[64 * 1024];
    new Random(7).NextBytes(payload);
    var disk = MbrWrapper.Wrap(payload, MbrWrapper.PartitionType.Linux);
    var path = this.Write("opaque.img", disk);
    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));

    var ops = FormatRegistry.GetArchiveOps("PartitionedDisk")!;
    using var fs = File.OpenRead(path);
    var raw = ops.List(fs, null).Single(e => e.Name.EndsWith(".raw", StringComparison.Ordinal));
    using var entry = ops.OpenEntry(fs, raw.Name, null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);

    Assert.That(entry.Length, Is.EqualTo(payload.Length));
    Assert.That(copy.ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Given_PartitionWindowHoldingExt_When_DetectedByContent_Then_IsExtWithoutCopyingThePartition() {
    var disk = BuildGptDisk(BuildFat(), BuildExt());
    using var ms = new MemoryStream(disk, writable: false);
    var ext = PartitionTableDetector.Detect(ms).Partitions[1];
    using var window = new PartitionWindowStream(ms, ext.StartOffset, ext.Size);
    window.Position = 17;

    Assert.That(Det.DetectByContent(window).ToString(), Is.EqualTo("Ext"));
    Assert.That(window.Position, Is.EqualTo(17));
  }

  [Test, Category("HappyPath")]
  public void Given_WholeDiskStream_When_DetectedByContent_Then_IsPartitionedDisk() {
    using var ms = new MemoryStream(MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12), writable: false);
    Assert.That(Det.DetectByContent(ms).ToString(), Is.EqualTo("PartitionedDisk"));
  }

  [Test, Category("Exception")]
  public void Given_NonSeekableStream_When_DetectedByContent_Then_Throws() {
    using var pipe = new System.IO.Pipes.AnonymousPipeServerStream();
    Assert.Throws<ArgumentException>(() => Det.DetectByContent(pipe));
  }

  // ── negative / boundary cases ───────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void Given_RandomBytesImg_When_Detected_Then_IsNotClaimed() {
    var data = new byte[256 * 1024];
    new Random(12345).NextBytes(data);
    var path = this.Write("noise.img", data);

    Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Unknown));
  }

  [Test, Category("EdgeCase")]
  public void Given_RandomBytesBin_When_Detected_Then_IsNotADisk() {
    var data = new byte[256 * 1024];
    new Random(54321).NextBytes(data);
    var path = this.Write("noise.bin", data);

    Assert.That(Detected(path), Is.Not.EqualTo("PartitionedDisk"));
    Assert.That(FormatRegistry.FilesystemFormatIds, Does.Not.Contain(Detected(path)));
  }

  [Test, Category("EdgeCase")]
  public void Given_BootSignatureWithEmptyPartitionTable_When_Detected_Then_IsNeitherDiskNorFat() {
    var data = new byte[2 * 1024 * 1024];
    data[510] = 0x55;
    data[511] = 0xAA;
    var path = this.Write("blank.img", data);

    Assert.That(Det.Detect(path), Is.EqualTo(Det.Format.Unknown));
  }

  [Test, Category("EdgeCase")]
  public void Given_MbrEntryWithInvalidStatusByte_When_Detected_Then_IsNotPartitionedDisk() {
    var data = new byte[2 * 1024 * 1024];
    WriteMbrEntry(data, 0, 0x42, 0x83, 2048, 2048);
    var path = this.Write("badstatus.img", data);

    Assert.That(Detected(path), Is.Not.EqualTo("PartitionedDisk"));
  }

  [Test, Category("EdgeCase")]
  public void Given_MbrEntryStartingPastEndOfImage_When_Detected_Then_IsNotPartitionedDisk() {
    var data = new byte[2 * 1024 * 1024];
    WriteMbrEntry(data, 0, 0x00, 0x83, 1_000_000, 2048);
    var path = this.Write("beyond.img", data);

    Assert.That(Detected(path), Is.Not.EqualTo("PartitionedDisk"));
  }

  [Test, Category("EdgeCase")]
  public void Given_OverlappingMbrEntries_When_Detected_Then_IsNotPartitionedDisk() {
    var data = new byte[4 * 1024 * 1024];
    WriteMbrEntry(data, 0, 0x00, 0x83, 2048, 4096);
    WriteMbrEntry(data, 1, 0x00, 0x83, 4096, 2048);
    var path = this.Write("overlap.img", data);

    Assert.That(Detected(path), Is.Not.EqualTo("PartitionedDisk"));
  }

  [Test, Category("EdgeCase")]
  public void Given_LastSectorOfMbrEntryEndsExactlyAtImageEnd_When_Detected_Then_IsPartitionedDisk() {
    var data = new byte[2 * 1024 * 1024];
    WriteMbrEntry(data, 0, 0x00, 0x83, 2048, 2048); // [1 MiB, 2 MiB) == whole remainder
    var path = this.Write("exact.img", data);

    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
  }

  [Test, Category("EdgeCase")]
  public void Given_FatBootSectorWhoseBootCodeLooksLikeAPartitionTable_When_Detected_Then_IsFat() {
    // The BPB is valid and the "partition" points at bytes holding no filesystem:
    // the volume boot record is the better-supported reading.
    var fat = BuildFat();
    WriteMbrEntry(fat, 0, 0x00, 0x83, 100, 200);
    var path = this.Write("vbr.img", fat);

    Assert.That(Detected(path), Is.EqualTo("Fat"));
  }

  [Test, Category("EdgeCase")]
  public void Given_MbrThatAlsoParsesAsBpbAndPartitionHoldsFat_When_Detected_Then_IsPartitionedDisk() {
    var disk = MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12);
    // Copy the FAT boot sector's jump + BPB into the MBR's boot-code area.
    Array.Copy(disk, 1024 * 1024, disk, 0, 62);
    var path = this.Write("both.img", disk);

    Assert.That(Detected(path), Is.EqualTo("PartitionedDisk"));
  }

  [Test, Category("EdgeCase")]
  public void Given_FatWithoutBootSignatureOrX86Jump_When_Detected_Then_IsStillFat() {
    // Atari ST style: a 68000 branch where the x86 jump would be, and no 0x55AA.
    var fat = BuildFat();
    fat[0] = 0x60;
    fat[1] = 0x1C;
    fat[510] = 0;
    fat[511] = 0;
    var path = this.Write("atari.img", fat);

    Assert.That(Detected(path), Is.EqualTo("Fat"));
  }

  [Test, Category("HappyPath")]
  public void Given_PartitionWithUnknownContent_When_RawEntryExtracted_Then_TheListedNameHoldsThePartitionBytes() {
    var payload = new byte[16 * 1024];
    new Random(3).NextBytes(payload);
    var path = this.Write("opaque2.img", MbrWrapper.Wrap(payload, MbrWrapper.PartitionType.Linux));

    var raw = ArchiveOperations.List(path, null).Single(e => e.Name.EndsWith(".raw", StringComparison.Ordinal));

    Assert.That(ArchiveOperations.ExtractEntry(path, raw.Name, null), Is.EqualTo(payload));
  }

  [Test, Category("EdgeCase")]
  public void Given_ImgPathThatDoesNotExist_When_DetectedForCreate_Then_IsFat() {
    var path = Path.Combine(this._dir, "new.img");
    Assert.That(Det.DetectByExtensionForCreate(path), Is.EqualTo(Det.Format.Fat));
  }

  // ── Lib helpers the explorer uses ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_BareExtInBin_When_DeleteEvaluated_Then_TheFilesystemsModifierIsUsed() {
    var path = this.Write("rootfs.bin", BuildExt());
    Assert.That(DeleteCapability.Evaluate(false, path, 1), Is.EqualTo(DeleteMode.ModifiableArchive));
  }

  [Test, Category("HappyPath")]
  public void Given_ExtensionlessBareExt_When_DeleteEvaluated_Then_TheFilesystemsModifierIsUsed() {
    var path = this.Write("rootfs", BuildExt());
    Assert.That(DeleteCapability.Evaluate(false, path, 1), Is.EqualTo(DeleteMode.ModifiableArchive));
  }

  [Test, Category("HappyPath")]
  public void Given_UnchangedFile_When_DetectedCachedTwice_Then_AgreesWithDetect() {
    var path = this.Write("disk.img", MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12));

    Assert.That(Det.DetectCached(path), Is.EqualTo(Det.Detect(path)));
    Assert.That(Det.DetectCached(path), Is.EqualTo(Det.Detect(path)));
  }

  [Test, Category("EdgeCase")]
  public void Given_FileReplacedWithDifferentContent_When_DetectedCached_Then_CacheIsNotStale() {
    var path = this.Write("swap.img", MbrWrapper.Wrap(BuildFat(), MbrWrapper.PartitionType.Fat12));
    Assert.That(Det.DetectCached(path).ToString(), Is.EqualTo("PartitionedDisk"));

    File.WriteAllBytes(path, BuildIso());
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

    Assert.That(Det.DetectCached(path), Is.EqualTo(Det.Format.Iso));
  }
}
