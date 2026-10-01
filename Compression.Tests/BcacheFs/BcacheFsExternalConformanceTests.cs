using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// bcachefs-tools as the judge of what this package writes, and as the author of
/// what it reads.
/// </summary>
/// <remarks>
/// <para><c>bcachefs fsck -n</c> from the distribution's bcachefs-tools (1.3.x on
/// Debian and Ubuntu) walks every tree, recomputes every bucket's usage from the
/// extents and nodes that occupy it, and compares the result with the alloc keys,
/// the backpointers, the freespace tree and the usage totals in the superblock; it
/// checks every inode against its directory entry and every directory's link
/// count. A volume it finds nothing to say about is a volume the format's owners
/// accept. One test feeds it a deliberately wrong total to show it is not
/// silent.</para>
///
/// <para>A checker cannot read file contents, so where a FUSE-capable bcachefs is
/// available the volume is also mounted and its tree, modes, link targets and bytes
/// compared with what went in. (Where the kernel has the driver,
/// <c>BcacheFsVolumeTests</c> mounts the volume through <c>ThirdPartyFsCheck</c>.) Extra checkers — a newer bcachefs-tools build, say — are named in
/// <c>CWB_BCACHEFS_EXTRA</c> as space-separated paths inside the Linux environment
/// and judge every volume as well.</para>
///
/// <para>Each test skips with an install hint when no Linux environment or no
/// bcachefs-tools is present. A checker that runs and objects fails the test; that
/// is never downgraded to a skip.</para>
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class BcacheFsExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── The judges ───────────────────────────────────────────────────────────

  private static void RequireBcachefsTools() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("No Linux environment (WSL) to run bcachefs-tools in.");
    if (!FsInteropToolbox.WslHasTool("bcachefs"))
      Assert.Ignore("bcachefs-tools is not installed: `sudo apt install -y bcachefs-tools`.");
  }

  /// <summary>The distribution's bcachefs first, then any extra build named in the environment.</summary>
  private static IReadOnlyList<string> Checkers() {
    var extra = Environment.GetEnvironmentVariable("CWB_BCACHEFS_EXTRA") ?? "";
    return ["bcachefs", .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
  }

  private static string Combined((string StdOut, string StdErr, int ExitCode) result)
    => (result.StdOut ?? "") + "\n" + (result.StdErr ?? "");

  /// <summary>
  /// Every checker walks the whole volume and finds nothing to repair.
  /// </summary>
  private static void AssertFsckClean(string imagePath) {
    var wsl = FsInteropToolbox.WinToWsl(imagePath);
    foreach (var checker in Checkers()) {
      var result = FsInteropToolbox.RunWsl($"{checker} fsck -n {wsl}");
      var output = Combined(result);
      TestContext.Out.WriteLine($"$ {checker} fsck -n  (exit {result.ExitCode})\n{output}");
      Assert.That(result.ExitCode, Is.EqualTo(0), $"{checker} fsck rejected the volume:\n{output}");
      Assert.That(output, Does.Contain("check_nlinks... done"), $"{checker} fsck did not walk the whole volume:\n{output}");
      Assert.That(output, Does.Not.Contain("fixing"), $"{checker} fsck found something to repair:\n{output}");
    }
  }

  private static string Write(string path, BcacheFsWriter writer) {
    using (var stream = File.Create(path)) writer.WriteTo(stream);
    return path;
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 37 + seed * 19 + i / 257);
    return result;
  }

  /// <summary>What a mount should show: files with their bytes, links with their targets, directories.</summary>
  private sealed record Tree(
    Dictionary<string, byte[]> Files,
    Dictionary<string, string> Links,
    HashSet<string> Directories);

  /// <summary>A volume holding a bit of everything the writer can express.</summary>
  private static (BcacheFsWriter Writer, Tree Expected) Sampler() {
    var tree = new Tree(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    var writer = new BcacheFsWriter();
    writer.SetLabel("cwb-conformance");

    void File(string name, byte[] data) {
      writer.AddFile(name, data);
      tree.Files[name] = data;
    }

    File("HELLO.TXT", Encoding.ASCII.GetBytes("a file to check"));
    File("empty.bin", []);
    File("sub/deeper/NESTED.BIN", Payload(9_000, 1));
    File("sub/odd.bin", Payload(4_097, 2));
    // Forty-seven full extents and a short one.
    File("big.bin", Payload(3_000_123, 3));
    writer.AddSymlink("link-to-hello", "HELLO.TXT");
    tree.Links["link-to-hello"] = "HELLO.TXT";
    writer.AddSymlink("sub/up", "../HELLO.TXT");
    tree.Links["sub/up"] = "../HELLO.TXT";
    writer.AddDirectory("empty/dir");
    foreach (var d in new[] { "sub", "sub/deeper", "empty", "empty/dir" }) tree.Directories.Add(d);
    return (writer, tree);
  }

  // ── What this package writes, judged by bcachefs fsck ────────────────────

  [Test, Category("HappyPath")]
  public void GivenAnEmptyVolume_WhenChecked_ThenFsckFindsNothingToFix() {
    RequireBcachefsTools();
    AssertFsckClean(Write(Path.Combine(this._tmpDir, "empty.img"), new BcacheFsWriter()));
  }

  [Test, Category("HappyPath")]
  public void GivenFilesLinksAndDirectoriesOfEveryShape_WhenChecked_ThenFsckFindsNothingToFix() {
    RequireBcachefsTools();
    AssertFsckClean(Write(Path.Combine(this._tmpDir, "sampler.img"), Sampler().Writer));
  }

  [Test, Category("BoundaryValue")]
  public void GivenSoManyFilesThatEveryTreeNeedsSeveralNodes_WhenChecked_ThenFsckFindsNothingToFix() {
    RequireBcachefsTools();
    var writer = new BcacheFsWriter();
    for (var i = 0; i < 1_500; ++i)
      writer.AddFile($"d{i % 7}/f{i:0000}.txt", Encoding.ASCII.GetBytes($"file {i}"));
    var path = Write(Path.Combine(this._tmpDir, "many.img"), writer);

    using (var image = File.OpenRead(path)) {
      var volume = BcacheFsVolume.Open(image);
      Assert.That(volume.NodeSectors(BcacheFsFormat.BtreeInodes).Count(), Is.GreaterThan(1),
        "precondition: the inodes tree spans more than one node");
      Assert.That(volume.NodeSectors(BcacheFsFormat.BtreeDirents).Count(), Is.GreaterThan(1),
        "precondition: the dirents tree spans more than one node");
    }

    AssertFsckClean(path);
  }

  [Test, Category("HappyPath")]
  public void GivenAVolume_WhenFilesAreAddedReplacedAndRemovedInPlace_ThenFsckFindsNothingToFix() {
    RequireBcachefsTools();
    var path = Path.Combine(this._tmpDir, "edited.img");
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) {
      descriptor.Create(image, [
        ArchiveInputInfo.InMemory("keep.bin", Payload(70_000, 4)),
        ArchiveInputInfo.InMemory("replace.bin", Payload(20_000, 5)),
        ArchiveInputInfo.InMemory("dir/remove.bin", Payload(5_000, 6)),
      ], new FormatCreateOptions());
      image.Position = 0;
      descriptor.Add(image, [
        ArchiveInputInfo.InMemory("replace.bin", Payload(140_000, 7)),
        ArchiveInputInfo.InMemory("new/deep/added.bin", Payload(1_000, 8)),
      ]);
      image.Position = 0;
      descriptor.Remove(image, ["dir/remove.bin"]);
    }

    AssertFsckClean(path);
  }

  [Test, Category("HappyPath")]
  [TestCase(MetadataZone.Unchanged)]
  [TestCase(MetadataZone.Back)]
  [TestCase(MetadataZone.BeforeContent)]
  public void GivenAFragmentedVolume_WhenDefragmented_ThenFsckFindsNothingToFix(MetadataZone zone) {
    RequireBcachefsTools();
    var path = Path.Combine(this._tmpDir, "defrag.img");
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) {
      descriptor.Create(image,
        [.. Enumerable.Range(0, 10).Select(i => ArchiveInputInfo.InMemory($"F{i}.BIN", Payload(40_000 + i * 3_000, i)))],
        new FormatCreateOptions());
      image.Position = 0;
      descriptor.Remove(image, ["F1.BIN", "F4.BIN", "F7.BIN"]);
      image.Position = 0;
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart, MetadataZonePlacement = zone });
    }

    AssertFsckClean(path);
  }

  /// <summary>
  /// The checker is not silent: a volume whose usage totals are one inode off is
  /// reported, not accepted.
  /// </summary>
  /// <remarks>
  /// The clean section and the journal entry both carry the inode count, and the
  /// checker recounts the inodes and compares. Without this, "fsck found nothing"
  /// could as well mean "fsck did not look".
  /// </remarks>
  [Test, Category("ErrorHandling")]
  public void GivenAVolumeWhoseInodeCountIsWrong_WhenChecked_ThenFsckReportsIt() {
    RequireBcachefsTools();
    var path = Write(Path.Combine(this._tmpDir, "lying.img"), Sampler().Writer);
    CorruptInodeCount(path);

    var result = FsInteropToolbox.RunWsl($"bcachefs fsck -n {FsInteropToolbox.WinToWsl(path)}");
    var output = Combined(result);
    TestContext.Out.WriteLine(output);
    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.Not.EqualTo(0), $"fsck accepted a wrong inode count:\n{output}");
      Assert.That(output, Does.Contain("nr_inodes"), $"fsck should name the wrong total:\n{output}");
    });
  }

  /// <summary>Adds one to the inode count in every superblock copy and in the journal entry.</summary>
  private static void CorruptInodeCount(string path) {
    // u64s 1, usage kind "inodes", level 0, entry type "usage".
    byte[] header = [1, 0, BcacheFsFormat.FsUsageInodes, 0, BcacheFsFormat.JsetUsage, 0, 0, 0];
    var bytes = File.ReadAllBytes(path);
    var patched = 0;
    for (var at = 0; ;) {
      var found = bytes.AsSpan(at).IndexOf(header);
      if (found < 0) break;
      at += found;
      var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8));
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 8), value + 1);
      at += header.Length;
      ++patched;
    }
    Assert.That(patched, Is.EqualTo(4), "three superblock copies and the journal entry carry the count");

    // The journal entry's checksum covers everything from its magic to its last entry.
    var journal = BcacheFsWriter.JournalFirstBucket * (long)BcacheFsFormat.BucketBytes;
    var u64s = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)journal + 40));
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)journal),
      BcacheFsFormat.MetadataChecksum(bytes.AsSpan((int)journal + 16, 40 + (int)u64s * 8)));
    File.WriteAllBytes(path, bytes);

    using var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
    BcacheFsSuperblockEditor.Restamp(image);
  }

  // ── What bcachefs-tools writes, read here ────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAVolumeTheInstalledToolsFormatted_WhenRead_ThenItIsAnEmptyRootWithLostAndFound() {
    RequireBcachefsTools();
    var path = Path.Combine(this._tmpDir, "theirs.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var format = FsInteropToolbox.RunWsl($"truncate -s 128M {wsl} && bcachefs format -q --force {wsl}");
    Assert.That(format.ExitCode, Is.EqualTo(0), $"bcachefs format failed:\n{Combined(format)}");

    using var image = File.OpenRead(path);
    using var reader = new BcacheFsReader(image);
    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.Directories, Is.EqualTo(new[] { "lost+found" }));
      Assert.That(reader.Entries, Is.Empty);
    });
  }

  // ── What this package writes, mounted ────────────────────────────────────

  /// <summary>
  /// A bcachefs that can serve the volume through FUSE sees exactly the tree, the
  /// modes, the link targets and the bytes that went in.
  /// </summary>
  /// <remarks>
  /// The distribution's 1.3.x build has no working FUSE support, so this runs where
  /// a newer build is named in <c>CWB_BCACHEFS_EXTRA</c>. The mount is read-write on
  /// a copy — a newer bcachefs upgrades the volume as it mounts it — so the original
  /// is left exactly as written. Reading a file through the driver verifies each
  /// extent's checksum on the way.
  /// </remarks>
  /// <summary>The first checker that can serve a volume through FUSE, or a skip.</summary>
  private static string RequireFuseChecker() {
    var fuse = Checkers().FirstOrDefault(c =>
      FsInteropToolbox.RunWsl(
        $"out=$({c} fusemount --help 2>&1); ! echo \"$out\" | grep -q 'Unknown command' "
        + "&& echo \"$out\" | grep -qi usage && command -v fusermount3").ExitCode == 0);
    if (fuse == null)
      Assert.Ignore("No FUSE-capable bcachefs: name a bcachefs-tools build with FUSE support in CWB_BCACHEFS_EXTRA.");
    return fuse;
  }

  /// <summary>
  /// Mounts a copy of <paramref name="imagePath" /> through FUSE, runs
  /// <paramref name="commands" /> inside the mount point and returns what they print.
  /// </summary>
  /// <remarks>
  /// Read-write on a copy: a newer bcachefs upgrades the volume as it mounts it, so
  /// the original stays exactly as written.
  /// </remarks>
  private static string RunInsideFuseMount(string fuse, string imagePath, string commands) {
    var id = Guid.NewGuid().ToString("N");
    var script =
      $"set -e; img=/tmp/cwb-{id}.img; mnt=/tmp/cwb-{id}.mnt; out=/tmp/cwb-{id}.out; "
      + $"cp {FsInteropToolbox.WinToWsl(imagePath)} $img; mkdir -p $mnt; "
      + $"( timeout 300 {fuse} fusemount -f $img $mnt > /tmp/cwb-{id}.log 2>&1 & ); "
      + "for i in $(seq 1 100); do mountpoint -q $mnt && break; sleep 0.1; done; mountpoint -q $mnt; "
      + $"( cd $mnt; {commands} ) > $out; fusermount3 -u $mnt; cat $out; rm -rf $img $mnt /tmp/cwb-{id}.*";
    var result = FsInteropToolbox.RunWsl(script);
    var output = Combined(result);
    TestContext.Out.WriteLine(output);
    Assert.That(result.ExitCode, Is.EqualTo(0), $"the FUSE mount failed:\n{output}");
    return result.StdOut;
  }

  [Test, Category("HappyPath")]
  public void GivenOurVolume_WhenMountedThroughFuse_ThenTreeModesLinksAndBytesMatch() {
    RequireBcachefsTools();
    var fuse = RequireFuseChecker();

    var (writer, expected) = Sampler();
    var path = Write(Path.Combine(this._tmpDir, "fuse.img"), writer);
    var stdout = RunInsideFuseMount(fuse, path,
      "find . -mindepth 1 -printf '%y %m %s %P\\t%l\\n' | sort; "
      + "find . -type f -print0 | sort -z | xargs -0 -r sha256sum");

    var seenFiles = new Dictionary<string, (string Mode, long Size)>(StringComparer.Ordinal);
    var seenLinks = new Dictionary<string, string>(StringComparer.Ordinal);
    var seenDirs = new HashSet<string>(StringComparer.Ordinal);
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      if (line.Length > 66 && line[64] == ' ' && line[65] == ' ') {
        hashes[line[66..].TrimStart('.', '/')] = line[..64];
        continue;
      }
      var tab = line.IndexOf('\t');
      var fields = line[..tab].Split(' ', 4);
      var name = fields[3];
      switch (fields[0]) {
        case "f": seenFiles[name] = (fields[1], long.Parse(fields[2], CultureInfo.InvariantCulture)); break;
        case "l": seenLinks[name] = line[(tab + 1)..]; break;
        case "d": seenDirs.Add(name); break;
      }
    }

    Assert.Multiple(() => {
      Assert.That(seenFiles.Keys, Is.EquivalentTo(expected.Files.Keys));
      Assert.That(seenLinks, Is.EquivalentTo(expected.Links));
      Assert.That(seenDirs, Is.EquivalentTo(expected.Directories));
      foreach (var (name, data) in expected.Files) {
        if (!seenFiles.TryGetValue(name, out var seen)) continue;
        Assert.That(seen.Size, Is.EqualTo(data.Length), $"{name} length");
        Assert.That(seen.Mode, Is.EqualTo("644"), $"{name} mode");
        Assert.That(hashes.GetValueOrDefault(name), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(data))),
          $"{name} must read back byte for byte through the driver");
      }
    });
  }

  /// <summary>
  /// Owner, group, permission bits and times given to a file come back through the
  /// driver as <c>stat</c> sees them, and the checker still finds nothing to fix.
  /// </summary>
  /// <remarks>
  /// Birth time has no place in what FUSE reports, and bcachefs' FUSE front end
  /// shows any time before the epoch as the epoch itself; both are checked through
  /// this package's reader instead (<c>BcacheFsVolumeTests</c>), and the encoding of
  /// a pre-epoch time against a volume the tools wrote
  /// (<c>BcacheFsReferenceVectorTests</c>).
  /// </remarks>
  [Test, Category("RoundTrip")]
  public void GivenFilesWithOwnersModesAndTimes_WhenMountedThroughFuse_ThenStatShowsThem() {
    RequireBcachefsTools();
    var access = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1_234_567);
    var files = new (string Name, ArchiveEntryMetadata Metadata)[] {
      ("owned.bin", new ArchiveEntryMetadata(
        LastAccessTimeUtc: access, LastWriteTimeUtc: access.AddDays(2), StatusChangeTimeUtc: access.AddMinutes(15),
        CreationTimeUtc: access.AddDays(-30), UnixUserId: 1234, UnixGroupId: 4321, UnixMode: 0x1A0)),
      ("root.bin", new ArchiveEntryMetadata(
        LastAccessTimeUtc: new DateTimeOffset(2001, 9, 9, 1, 46, 40, TimeSpan.Zero),
        LastWriteTimeUtc: new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
        StatusChangeTimeUtc: new DateTimeOffset(2038, 1, 19, 3, 14, 8, TimeSpan.Zero),
        UnixUserId: 0, UnixGroupId: 100, UnixMode: 0x1ED)),
    };
    var writer = new BcacheFsWriter();
    foreach (var (name, metadata) in files) writer.AddFile(name, Payload(5_000, name.Length), metadata);
    var path = Write(Path.Combine(this._tmpDir, "metadata.img"), writer);
    AssertFsckClean(path);

    // name, octal mode, uid, gid, then atime, mtime and ctime in whole seconds and nanoseconds.
    var stdout = RunInsideFuseMount(RequireFuseChecker(), path,
      "stat -c '%n %a %u %g %X %Y %Z' *.bin; stat -c '%n %x|%y|%z' *.bin");
    Assert.Multiple(() => {
      foreach (var (name, metadata) in files) {
        var line = stdout.Split('\n').Single(l => l.StartsWith(name + " ", StringComparison.Ordinal) && !l.Contains('|'));
        var f = line.Split(' ');
        Assert.That(Convert.ToInt32(f[1], 8), Is.EqualTo(metadata.UnixMode), $"{name} mode");
        Assert.That(uint.Parse(f[2], CultureInfo.InvariantCulture), Is.EqualTo(metadata.UnixUserId), $"{name} owner");
        Assert.That(uint.Parse(f[3], CultureInfo.InvariantCulture), Is.EqualTo(metadata.UnixGroupId), $"{name} group");
        Assert.That(long.Parse(f[4], CultureInfo.InvariantCulture), Is.EqualTo(metadata.LastAccessTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} atime");
        Assert.That(long.Parse(f[5], CultureInfo.InvariantCulture), Is.EqualTo(metadata.LastWriteTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} mtime");
        Assert.That(long.Parse(f[6], CultureInfo.InvariantCulture), Is.EqualTo(metadata.StatusChangeTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} ctime");

        // The sub-second part survives too: stat prints nanoseconds, the volume keeps 100 ns.
        var exact = stdout.Split('\n').Single(l => l.StartsWith(name + " ", StringComparison.Ordinal) && l.Contains('|'));
        var mtime = exact.Split('|')[1];
        var fraction = (metadata.LastWriteTimeUtc!.Value.UtcTicks % TimeSpan.TicksPerSecond * 100).ToString("D9", CultureInfo.InvariantCulture);
        Assert.That(mtime, Does.Contain("." + fraction), $"{name} mtime nanoseconds");
      }
    });
  }

  /// <summary>
  /// Metadata given to a file added in place, and to one replaced in place over an
  /// inode that already carried some, is what the driver reports afterwards.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void GivenAVolume_WhenFilesAreAddedAndReplacedInPlaceWithMetadata_ThenFsckAndFuseAgree() {
    RequireBcachefsTools();
    var path = Path.Combine(this._tmpDir, "edited-metadata.img");
    var descriptor = new BcacheFsFormatDescriptor();
    var when = new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero).AddTicks(12_345);
    var replaced = new ArchiveEntryMetadata(LastAccessTimeUtc: when, LastWriteTimeUtc: when.AddHours(1),
      StatusChangeTimeUtc: when.AddHours(2), UnixUserId: 501, UnixGroupId: 20, UnixMode: 0x180);
    var added = new ArchiveEntryMetadata(LastAccessTimeUtc: when.AddDays(1), LastWriteTimeUtc: when.AddDays(2),
      StatusChangeTimeUtc: when.AddDays(3), UnixUserId: 65534, UnixGroupId: 65534, UnixMode: 0x124);
    using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) {
      descriptor.Create(image, [ArchiveInputInfo.InMemory("replaced.bin", Payload(10_000, 9),
        new ArchiveEntryMetadata(UnixUserId: 1, UnixGroupId: 1, UnixMode: 0x1FF))], new FormatCreateOptions());
      image.Position = 0;
      descriptor.Add(image, [
        ArchiveInputInfo.InMemory("replaced.bin", Payload(20_000, 10), replaced),
        ArchiveInputInfo.InMemory("added.bin", Payload(3_000, 11), added),
      ]);
    }
    AssertFsckClean(path);

    var stdout = RunInsideFuseMount(RequireFuseChecker(), path, "stat -c '%n %a %u %g %X %Y %Z' *.bin");
    Assert.Multiple(() => {
      foreach (var (name, metadata) in new[] { ("replaced.bin", replaced), ("added.bin", added) }) {
        var f = stdout.Split('\n').Single(l => l.StartsWith(name + " ", StringComparison.Ordinal)).Split(' ');
        Assert.That(Convert.ToInt32(f[1], 8), Is.EqualTo(metadata.UnixMode), $"{name} mode");
        Assert.That(uint.Parse(f[2], CultureInfo.InvariantCulture), Is.EqualTo(metadata.UnixUserId), $"{name} owner");
        Assert.That(uint.Parse(f[3], CultureInfo.InvariantCulture), Is.EqualTo(metadata.UnixGroupId), $"{name} group");
        Assert.That(long.Parse(f[4], CultureInfo.InvariantCulture), Is.EqualTo(metadata.LastAccessTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} atime");
        Assert.That(long.Parse(f[5], CultureInfo.InvariantCulture), Is.EqualTo(metadata.LastWriteTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} mtime");
        Assert.That(long.Parse(f[6], CultureInfo.InvariantCulture), Is.EqualTo(metadata.StatusChangeTimeUtc!.Value.ToUnixTimeSeconds()), $"{name} ctime");
      }
    });
  }
}
