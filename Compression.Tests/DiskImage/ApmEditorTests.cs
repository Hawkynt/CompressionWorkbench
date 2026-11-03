using Compression.Core.DiskImage;
using Compression.Registry;
using FileFormat.PartitionedDisk;

namespace Compression.Tests.DiskImage;

/// <summary>
/// Creating Apple Partition Maps and adding and deleting their entries, checked by our parser and
/// by parted (libparted's mac label) and partx (util-linux / libblkid) in WSL.
/// </summary>
[TestFixture]
public sealed class ApmEditorTests {
  private static byte[] Image(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static ArchiveInputInfo Raw(string name, byte[] data) => new(name, name, false, data);

  private static MemoryStream CreateDisk(params (string Name, byte[] Data)[] parts) {
    var disk = new MemoryStream();
    new PartitionedDiskFormatDescriptor().Create(disk, [.. parts.Select(p => Raw(p.Name, p.Data))],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["scheme"] = "apm" } });
    return disk;
  }

  [Test]
  public void GivenAnEmptyMap_WhenCreated_ThenItHoldsTheMapAndOneFreeRun() {
    var disk = new MemoryStream(new byte[4 << 20]);
    var editor = ApmEditor.CreateEmpty(disk);
    Assert.That(editor.Entries.Select(e => (e.Start, e.Blocks, e.Type)),
      Is.EqualTo(new[] { (1L, 63L, "Apple_partition_map"), (64L, (4L << 20) / 512 - 64, "Apple_Free") }));
    Assert.That(ApmParser.Parse(disk), Is.Empty);
  }

  [Test]
  public void GivenPartitionsAdded_WhenParsed_ThenTheyAppearWithFreeRunsAround() {
    var disk = new MemoryStream(new byte[8 << 20]);
    var editor = ApmEditor.CreateEmpty(disk);
    editor.AddPartition(2048, 4096, "Apple_HFS", "Macintosh HD");
    editor.AddPartition(8192, 2048, "Apple_UNIX_SVR2", "linux");
    var parsed = ApmParser.Parse(disk);
    Assert.That(parsed.Select(p => (p.StartOffset, p.Size, p.TypeName, p.Name)),
      Is.EqualTo(new[] { (2048L * 512, 4096L * 512, "Apple_HFS", "Macintosh HD"), (8192L * 512, 2048L * 512, "Apple_UNIX_SVR2", "linux") }));
    var reopened = ApmEditor.Open(disk);
    Assert.That(reopened.Entries.Sum(e => e.Blocks), Is.EqualTo((8L << 20) / 512 - 1), "entries no longer cover the disk");
  }

  [Test]
  public void GivenAPartitionDeleted_WhenParsed_ThenItsSpaceMergesIntoOneFreeRun() {
    var disk = new MemoryStream(new byte[8 << 20]);
    var editor = ApmEditor.CreateEmpty(disk);
    editor.AddPartition(2048, 4096, "Apple_HFS", "a");
    editor.DeletePartition(2048);
    Assert.That(ApmEditor.Open(disk).Entries.Select(e => e.Type), Is.EqualTo(new[] { "Apple_partition_map", "Apple_Free" }));
  }

  [Test]
  public void GivenARangeThatIsNotFree_WhenAdded_ThenItIsRefusedAndTheMapIsUnchanged() {
    var disk = new MemoryStream(new byte[8 << 20]);
    var editor = ApmEditor.CreateEmpty(disk);
    editor.AddPartition(2048, 4096, "Apple_HFS", "a");
    var before = disk.ToArray();
    Assert.Throws<InvalidOperationException>(() => editor.AddPartition(4000, 100, "Apple_HFS", "b"));
    Assert.Throws<InvalidOperationException>(() => editor.DeletePartition(1));
    Assert.Throws<InvalidOperationException>(() => editor.DeletePartition(5000));
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAFullMap_WhenAnotherPartitionIsAdded_ThenItIsRefused() {
    var disk = new MemoryStream(new byte[1 << 20]);
    var editor = ApmEditor.CreateEmpty(disk, mapBlocks: 4);
    editor.AddPartition(100, 10, "Apple_HFS", "a"); // map, free, a, free
    Assert.Throws<InvalidOperationException>(() => editor.AddPartition(200, 10, "Apple_HFS", "b"));
  }

  [Test]
  public void GivenTwoImages_WhenAnApmDiskIsCreated_ThenEachPartitionHoldsItsImage() {
    var a = Image(300_000, 1);
    var b = Image(70_000, 2);
    var disk = CreateDisk(("alpha.raw", a), ("beta.raw", b));
    var parsed = ApmParser.Parse(disk);
    Assert.That(parsed.Select(p => (p.StartOffset, p.Name)), Is.EqualTo(new[] { (1L << 20, "alpha"), (2L << 20, "beta") }));
    Assert.That(disk.ToArray().AsSpan((int)parsed[0].StartOffset, a.Length).SequenceEqual(a), Is.True);
    Assert.That(disk.ToArray().AsSpan((int)parsed[1].StartOffset, b.Length).SequenceEqual(b), Is.True);
  }

  [Test]
  public void GivenAnApmDisk_WhenAPartitionIsAddedAndRemovedThroughTheDescriptor_ThenTheMapFollows() {
    var disk = CreateDisk(("alpha.raw", Image(300_000, 1)));
    disk.SetLength(8 << 20);
    var descriptor = new PartitionedDiskFormatDescriptor();
    var gamma = Image(100_000, 3);
    descriptor.Add(disk, [Raw("gamma.raw", gamma)]);
    var parsed = ApmParser.Parse(disk);
    Assert.That(parsed.Select(p => p.Name), Is.EqualTo(new[] { "alpha", "gamma" }));
    Assert.That(disk.ToArray().AsSpan((int)parsed[1].StartOffset, gamma.Length).SequenceEqual(gamma), Is.True);

    descriptor.Remove(disk, ["Partition2_Apple_UNIX_SVR2"]);
    Assert.That(ApmParser.Parse(disk).Select(p => p.Name), Is.EqualTo(new[] { "alpha" }));
  }

  // ── third-party readers ───────────────────────────────────────────────────

  private string _dir = "";

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-apm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private static void RequireTools() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("parted") || !FsInteropToolbox.WslHasTool("partx"))
      Assert.Ignore("parted and partx are not installed: `sudo apt-get install -y parted util-linux`.");
  }

  private static string Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}{r.StdErr}");
    Assert.That(r.ExitCode, Is.Zero, command);
    return r.StdOut;
  }

  /// <summary>parted's machine-readable listing: (start sector, end sector, name) per partition.</summary>
  private static string[] Parted(string path)
    => Run($"parted -s -m {FsInteropToolbox.WinToWsl(path)} unit s print").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).Where(l => char.IsAsciiDigit(l[0])).Select(l => string.Join(':', l.Split(':')[1..3].Append(l.Split(':')[5])))
      .ToArray();

  [Test]
  [Category("ExternalInterop")]
  public void GivenOurApmDisk_WhenPartedAndPartxReadIt_ThenTheyListOurPartitions() {
    RequireTools();
    var path = Path.Combine(this._dir, "ours.img");
    File.WriteAllBytes(path, CreateDisk(("alpha.raw", Image(300_000, 1)), ("beta.raw", Image(70_000, 2))).ToArray());
    Assert.That(Parted(path), Is.EqualTo(new[] { "1s:63s:Apple", "2048s:2633s:alpha", "4096s:4232s:beta" }));
    var partx = Run($"partx --show -o START,SECTORS,NAME,TYPE {FsInteropToolbox.WinToWsl(path)}");
    Assert.That(partx, Does.Contain("Apple_partition_map").And.Contain("alpha").And.Contain("beta"));
  }

  [Test]
  [Category("ExternalInterop")]
  public void GivenAPartedApmDisk_WhenWeAddAndRemoveAPartition_ThenPartedSeesExactlyThat() {
    RequireTools();
    var path = Path.Combine(this._dir, "parted.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    Run($"truncate -s 16M {wsl} && parted -s {wsl} mklabel mac && parted -s {wsl} mkpart primary hfs+ 1MiB 4MiB");
    var original = Parted(path);

    var gamma = Image(500_000, 3);
    using (var disk = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      new PartitionedDiskFormatDescriptor().Add(disk, [Raw("gamma.raw", gamma)]);
    var added = Parted(path);
    Assert.That(added, Has.Length.EqualTo(original.Length + 1));
    Assert.That(added.Take(original.Length), Is.EqualTo(original), "the existing partitions moved");
    Assert.That(added[^1], Does.EndWith(":gamma"));

    using (var disk = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      new PartitionedDiskFormatDescriptor().Remove(disk, ["Partition2_Apple_UNIX_SVR2"]);
    Assert.That(Parted(path), Is.EqualTo(original));
  }
}
