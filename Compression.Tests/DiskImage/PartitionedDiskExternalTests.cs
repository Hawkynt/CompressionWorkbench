#pragma warning disable CS1591
using System.Globalization;
using Compression.Core.DiskImage;
using Compression.Registry;
using FileFormat.PartitionedDisk;

namespace Compression.Tests.DiskImage;

/// <summary>
/// The partitioned-disk reader and writer against the reference Linux tools: tables written here
/// must pass <c>sgdisk -v</c> / <c>sfdisk --verify</c> and read back in <c>parted</c>; filesystems
/// edited in place inside a partition must pass <c>fsck.vfat</c> / <c>e2fsck</c>; and the
/// tables <c>sfdisk</c>, <c>fdisk -b 4096</c> and <c>parted</c> (bsd, amiga, sun, dvh) write must
/// be read at the offsets those tools report. Skipped when WSL (or, off Windows, a tool) is missing.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class PartitionedDiskExternalTests {

  private static readonly PartitionedDiskFormatDescriptor Desc = new();
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_pdisk_ext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireTools(params string[] tools) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    foreach (var tool in tools)
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' is not installed in the WSL distro.");
  }

  /// <summary>Runs a bash script in the fixture directory; returns stdout+stderr, asserting success unless told otherwise.</summary>
  private string Bash(string script, bool mustSucceed = true) {
    var dir = FsInteropToolbox.WinToWsl(this._dir);
    var result = FsInteropToolbox.RunWsl($"set -e; export LC_ALL=C; cd {dir}; {script}");
    var output = result.StdOut + result.StdErr;
    if (mustSucceed)
      Assert.That(result.ExitCode, Is.EqualTo(0), $"script failed:\n{script}\n{output}");
    return output;
  }

  private string PathOf(string name) => Path.Combine(this._dir, name);

  private static FormatCreateOptions Options(string scheme, int sectorSize = 512) => new() {
    FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
      ["scheme"] = scheme, ["sectorSize"] = sectorSize.ToString(CultureInfo.InvariantCulture),
    },
  };

  private void CreateDisk(string disk, string scheme, int sectorSize, params string[] images) {
    using var fs = File.Create(this.PathOf(disk));
    Desc.Create(fs, [.. images.Select(i => new ArchiveInputInfo(this.PathOf(i), i, false))], Options(scheme, sectorSize));
  }

  /// <summary>Partition (start, size) in bytes as <c>parted -m unit B print</c> reports them.</summary>
  private List<(long Start, long Size)> PartedLayout(string disk) {
    var output = this.Bash($"parted -s -m {disk} unit B print");
    var result = new List<(long, long)>();
    foreach (var line in output.Split('\n')) {
      var f = line.Trim().Split(':');
      if (f.Length < 4 || !int.TryParse(f[0], out _)) continue;
      result.Add((long.Parse(f[1].TrimEnd('B'), CultureInfo.InvariantCulture),
                  long.Parse(f[3].TrimEnd('B'), CultureInfo.InvariantCulture)));
    }
    return result;
  }

  // ── Create, verified by the table tools and fsck ───────────────────

  [Test]
  public void Given_VfatAndExt4Images_When_CreatedAsGpt_Then_SgdiskSfdiskPartedAndFsckAgree() {
    RequireTools("sgdisk", "sfdisk", "parted", "mkfs.vfat", "fsck.vfat", "mkfs.ext4", "e2fsck", "mcopy");
    this.Bash(
      "echo dos > dos.txt; mkdir -p root && echo linux > root/linux.txt; " +
      "mkfs.vfat -C a.img 8192 >/dev/null; mcopy -i a.img dos.txt ::/DOS.TXT; " +
      "truncate -s 12M b.img; mkfs.ext4 -q -F -d root b.img");

    this.CreateDisk("disk.img", "gpt", 512, "a.img", "b.img");

    var sgdisk = this.Bash("sgdisk -v disk.img");
    Assert.That(sgdisk, Does.Contain("No problems found"), sgdisk);
    var sfdisk = this.Bash("sfdisk --verify disk.img");
    Assert.That(sfdisk, Does.Contain("No errors detected"), sfdisk);

    var layout = this.PartedLayout("disk.img");
    var ours = PartitionTableDetector.Detect(File.ReadAllBytes(this.PathOf("disk.img"))).Partitions;
    Assert.That(layout, Is.EqualTo(ours.Select(p => (p.StartOffset, p.Size)).ToList()));
    Assert.That(layout[0].Start, Is.EqualTo(1024 * 1024));

    var p1 = ours[0]; var p2 = ours[1];
    this.Bash(
      $"dd if=disk.img of=p1.img bs=512 skip={p1.StartOffset / 512} count={p1.Size / 512} status=none; " +
      $"dd if=disk.img of=p2.img bs=512 skip={p2.StartOffset / 512} count={p2.Size / 512} status=none; " +
      "fsck.vfat -n p1.img; e2fsck -fn p2.img; test \"$(mcopy -i disk.img@@1M ::/DOS.TXT -)\" = dos");
  }

  [Test]
  public void Given_VfatImage_When_CreatedAsMbr_Then_SfdiskVerifiesAndTheTypeIsFat32() {
    RequireTools("sfdisk", "mkfs.vfat", "fsck.vfat", "mcopy");
    this.Bash("echo dos > dos.txt; mkfs.vfat -C a.img 8192 >/dev/null; mcopy -i a.img dos.txt ::/DOS.TXT");

    this.CreateDisk("disk.img", "mbr", 512, "a.img");

    var verify = this.Bash("sfdisk --verify disk.img");
    Assert.That(verify, Does.Contain("No errors detected"), verify);
    var dump = this.Bash("sfdisk --dump disk.img");
    Assert.That(dump, Does.Contain("label: dos"));
    Assert.That(dump, Does.Match(@"start=\s*2048, size=\s*16384, type=c"), dump);
    Assert.That(this.Bash("mcopy -i disk.img@@1M ::/DOS.TXT -").Trim(), Is.EqualTo("dos"));
  }

  [Test]
  public void Given_VfatImage_When_CreatedAs4KnGpt_Then_FdiskWith4096ByteSectorsReadsTheSameLayout() {
    RequireTools("fdisk", "mkfs.vfat", "mcopy");
    this.Bash("echo dos > dos.txt; mkfs.vfat -S 4096 -C a.img 8192 >/dev/null; mcopy -i a.img dos.txt ::/DOS.TXT");

    this.CreateDisk("disk.img", "gpt", 4096, "a.img");

    var listing = this.Bash("fdisk -b 4096 -l disk.img");
    Assert.That(listing, Does.Contain("Disklabel type: gpt"), listing);
    Assert.That(listing, Does.Match(@"disk\.img1\s+256\s+2303\s+2048"), listing); // 1 MiB / 4096 = LBA 256, 8 MiB = 2048 blocks
    Assert.That(listing, Does.Not.Contain("corrupt").And.Not.Contain("mismatch"), listing);
    Assert.That(this.Bash("mcopy -i disk.img@@1M ::/DOS.TXT -").Trim(), Is.EqualTo("dos"));
  }

  // ── Read what the tools write ──────────────────────────────────────

  [Test]
  public void Given_FdiskGptAt4096ByteSectors_When_Listed_Then_TheVfatPartitionBrowses() {
    RequireTools("fdisk", "mkfs.vfat", "mcopy");
    this.Bash(
      "echo dos > dos.txt; truncate -s 24M disk.img; " +
      "printf 'g\\nn\\n1\\n256\\n+8M\\nw\\n' | fdisk -b 4096 disk.img >/dev/null; " +
      "mkfs.vfat -S 4096 -C p1.img 8192 >/dev/null; mcopy -i p1.img dos.txt ::/DOS.TXT; " +
      "dd if=p1.img of=disk.img bs=4096 seek=256 conv=notrunc status=none");

    var detection = PartitionTableDetector.Detect(File.ReadAllBytes(this.PathOf("disk.img")));
    Assert.That(detection.Scheme, Is.EqualTo("GPT"));
    Assert.That(detection.SectorSize, Is.EqualTo(4096));
    Assert.That(detection.Partitions.Single().StartOffset, Is.EqualTo(256L * 4096));
    Assert.That(Compression.Lib.FormatDetector.Detect(this.PathOf("disk.img")).ToString(), Is.EqualTo("PartitionedDisk"));
    Assert.That(Compression.Lib.ArchiveOperations.List(this.PathOf("disk.img"), null).Select(e => e.Name),
      Has.Some.Match("^Partition1_.*/DOS\\.TXT$"));
  }

  [Test]
  public void Given_SfdiskExtendedWithLogicals_When_Listed_Then_LogicalsAppearAtSfdiskOffsets() {
    RequireTools("sfdisk", "mkfs.vfat", "mcopy");
    this.Bash(
      "echo dos > dos.txt; truncate -s 32M disk.img; " +
      "printf 'label: dos\\nstart=2048, size=8192, type=83\\nstart=10240, type=5\\nstart=12288, size=8192, type=c\\nstart=22528, size=8192, type=83\\n' | sfdisk -q disk.img; " +
      "mkfs.vfat --offset=12288 disk.img 4096 >/dev/null; mcopy -i disk.img@@6M dos.txt ::/DOS.TXT");

    var dump = this.Bash("sfdisk --dump disk.img");
    var sfdiskStarts = System.Text.RegularExpressions.Regex.Matches(dump, @"start=\s*(\d+), size=\s*(\d+), type=(\w+)")
      .Where(m => m.Groups[3].Value != "5")
      .Select(m => (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 512, long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 512))
      .ToList();
    var ours = PartitionTableDetector.Detect(File.ReadAllBytes(this.PathOf("disk.img"))).Partitions;
    Assert.That(ours.Select(p => (p.StartOffset, p.Size)), Is.EquivalentTo(sfdiskStarts), dump);
    Assert.That(ours.Count(p => p.Source == "EBR"), Is.EqualTo(2));
    Assert.That(Compression.Lib.ArchiveOperations.List(this.PathOf("disk.img"), null).Select(e => e.Name),
      Has.Some.Match("/DOS\\.TXT$"));
  }

  [TestCase("bsd")]
  [TestCase("amiga")]
  [TestCase("sun")]
  [TestCase("dvh")]
  public void Given_PartedLabel_When_Detected_Then_PartitionsMatchPartedsOffsets(string label) {
    RequireTools("parted");
    // mkpart's leading arguments differ per label: amiga takes a name, dvh a primary/logical
    // kind plus a filesystem type, bsd and sun only a filesystem type.
    var kind = label switch { "amiga" => "primary", "dvh" => "primary ext2", _ => "ext2" };
    var made = this.Bash(
      $"truncate -s 64M disk.img; parted -s disk.img mklabel {label}; " +
      $"parted -s disk.img mkpart {kind} 4MiB 24MiB; parted -s disk.img mkpart {kind} 24MiB 44MiB; " +
      "xxd -l 1536 disk.img | grep -v ' 0000 0000 0000 0000 0000 0000 0000 0000 '", mustSucceed: false);
    var parted = this.PartedLayout("disk.img");
    if (parted.Count == 0) Assert.Ignore($"parted cannot write a {label} label here: {made}");

    var bytes = File.ReadAllBytes(this.PathOf("disk.img"));
    var detection = PartitionTableDetector.Detect(bytes);
    var expectedScheme = label switch { "bsd" => "BSD", "amiga" => "RDB", "sun" => "Sun", _ => "SGI" };
    Assert.That(detection.Scheme, Is.EqualTo(expectedScheme), made);
    // parted lists the label's bookkeeping slices (SGI volume header, Sun whole-disk) as partitions
    // too; every data partition we report must be one parted reports, and each of parted's two
    // mkpart partitions must be among ours.
    var ours = detection.Partitions.Select(p => (p.StartOffset, p.Size)).ToList();
    Assert.That(parted, Is.SupersetOf(ours.Where(o => parted.Contains(o))));
    var dataPartitions = parted.Where(p => p.Start >= 1024 * 1024 - 64 * 1024 && p.Size >= 16L * 1024 * 1024).ToList();
    Assert.That(dataPartitions, Has.Count.EqualTo(2), $"{string.Join(", ", parted)}\n{made}");
    Assert.That(ours, Is.SupersetOf(dataPartitions), $"ours: {string.Join(", ", ours)}; parted: {string.Join(", ", parted)}");

    using var ms = new MemoryStream(bytes);
    Assert.That(PartitionTableDetector.HasSelfValidatingLabel(ms), Is.True);
    Assert.That(Compression.Lib.FormatDetector.Detect(this.PathOf("disk.img")).ToString(), Is.EqualTo("PartitionedDisk"));
  }

  // ── Edit in place, verified by fsck ────────────────────────────────

  [Test]
  public void Given_SfdiskGptWithVfatAndExt4_When_FilesEditedInPlace_Then_FsckIsCleanAndTheToolsReadTheEdits() {
    RequireTools("sfdisk", "sgdisk", "mkfs.vfat", "fsck.vfat", "mkfs.ext4", "e2fsck", "debugfs", "mcopy");
    this.Bash(
      "mkdir -p rootfs && echo linux > rootfs/linux.txt && echo gone > rootfs/gone.txt && echo dos > dos.txt; " +
      "truncate -s 24M disk.img; " +
      "printf 'label: gpt\\nstart=2048, size=16384, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\\nstart=18432, size=28672, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4\\n' | sfdisk -q disk.img; " +
      "mkfs.vfat --offset=2048 disk.img 8192 >/dev/null; mcopy -i disk.img@@1M dos.txt ::/DOS.TXT; " +
      "mkfs.ext4 -q -F -d rootfs -E offset=9437184 disk.img 14M");

    var path = this.PathOf("disk.img");
    var before = File.ReadAllBytes(path);
    var parts = PartitionTableDetector.Detect(before).Partitions;
    string fat, ext;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      fat = PartitionedDiskLister.MakePartitionPrefix(parts[0]);
      ext = PartitionedDiskLister.MakePartitionPrefix(parts[1]);
      Desc.Add(fs, [
        ArchiveInputInfo.InMemory($"{fat}/NEW.TXT", "added to fat\n"u8.ToArray()),
        ArchiveInputInfo.InMemory($"{ext}/added.txt", "added to ext4\n"u8.ToArray()),
      ]);
      Desc.Remove(fs, [$"{ext}/gone.txt"]);
    }

    var after = File.ReadAllBytes(path);
    Assert.That(after.Length, Is.EqualTo(before.Length));
    // Nothing outside the two partitions moved: table, gaps and backup GPT are byte-identical.
    var inside = new bool[after.Length];
    foreach (var p in parts) Array.Fill(inside, true, (int)p.StartOffset, (int)p.Size);
    for (var i = 0; i < after.Length; ++i)
      if (!inside[i] && after[i] != before[i])
        Assert.Fail($"byte {i} outside the partitions changed");

    var output = this.Bash(
      "sgdisk -v disk.img; " +
      $"dd if=disk.img of=p1.img bs=512 skip={parts[0].StartOffset / 512} count={parts[0].Size / 512} status=none; " +
      $"dd if=disk.img of=p2.img bs=512 skip={parts[1].StartOffset / 512} count={parts[1].Size / 512} status=none; " +
      "fsck.vfat -n p1.img; e2fsck -fn p2.img; " +
      "echo FAT=$(mcopy -i disk.img@@1M ::/NEW.TXT -); " +
      "echo EXT=$(debugfs -R 'cat /added.txt' p2.img 2>/dev/null); " +
      "echo GONE=$(debugfs -R 'ls -l /' p2.img 2>/dev/null | grep -c gone.txt || true)");
    Assert.That(output, Does.Contain("No problems found"), output);
    Assert.That(output, Does.Contain("FAT=added to fat"), output);
    Assert.That(output, Does.Contain("EXT=added to ext4"), output);
    Assert.That(output, Does.Contain("GONE=0"), output);
  }
}
