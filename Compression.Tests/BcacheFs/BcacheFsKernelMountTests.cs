using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// The Linux kernel's bcachefs driver as the judge: it mounts what this package
/// writes read-write, writes into it, and this package then reads and edits what
/// the kernel left.
/// </summary>
/// <remarks>
/// <para>WSL's own kernel has no bcachefs, so the driver comes from the libguestfs
/// appliance, which boots the distribution's kernel (Ubuntu 24.04's 6.8 ships
/// bcachefs as a module) under KVM with the image as its disk. A shell inside the
/// appliance lists the mounted tree — kind, permission bits, owner, group, size,
/// atime/mtime/ctime to the nanosecond, a SHA-256 of every file, link targets and
/// every extended attribute — and that listing is compared with what went in.</para>
///
/// <para>The kernel upgrades the volume as it mounts it (1.3 to 1.4 on 6.8), so
/// after every session <c>bcachefs fsck -n</c> walks the volume, and so does any
/// newer build named in <c>CWB_BCACHEFS_EXTRA</c>.</para>
///
/// <para>Skips with an install hint where no appliance kernel has the driver; where
/// <c>CWB_REQUIRE_BCACHEFS_KERNEL=1</c> is set the missing driver fails the test
/// instead. A driver that refuses the volume, or a listing that differs, always
/// fails.</para>
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class BcacheFsKernelMountTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_kernel_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── What a volume looks like, from either side ───────────────────────────

  /// <summary>One node of a mounted tree as the kernel reports it.</summary>
  internal sealed record Node(
    string Path, char Kind, int Mode, uint Uid, uint Gid, long Size,
    (long Seconds, int Nanos) Atime, (long Seconds, int Nanos) Mtime, (long Seconds, int Nanos) Ctime,
    string Content, IReadOnlyDictionary<string, string> Xattrs);

  /// <summary>
  /// Lists every node below the mount point, one line each:
  /// <c>E|path|kind|mode|uid|gid|size|atime|mtime|ctime|sha256-or-target|name=0xhex ...</c>.
  /// </summary>
  private const string ListingScript = """
    cd /sysroot
    find . -mindepth 1 ! -path './lost+found' ! -path './lost+found/*' | LC_ALL=C sort | while IFS= read -r p; do
      s=$(stat -c '%F|%a|%u|%g|%s|%.9X|%.9Y|%.9Z' "$p")
      case "$s" in
        regular*) c=$(sha256sum < "$p" | cut -c1-64) ;;
        symbolic*) c=$(readlink "$p") ;;
        *) c=- ;;
      esac
      x=$(getfattr -h -d -m - -e hex "$p" 2>/dev/null | grep '=' | grep -v '^bcachefs' | LC_ALL=C sort | tr '\n' ' ')
      printf 'E|%s|%s|%s|%s\n' "${p#./}" "$s" "$c" "$x"
    done
    """;

  internal static IReadOnlyDictionary<string, Node> ParseListing(string output) {
    var result = new Dictionary<string, Node>(StringComparer.Ordinal);
    foreach (var raw in output.Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (!line.StartsWith("E|", StringComparison.Ordinal)) continue;
      var f = line.Split('|');
      // E, path, kind, mode, uid, gid, size, atime, mtime, ctime, content, xattrs
      var kind = f[2] switch {
        var k when k.StartsWith("regular", StringComparison.Ordinal) => 'f',
        "directory" => 'd',
        "symbolic link" => 'l',
        _ => '?',
      };
      var xattrs = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var pair in f[11].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
        var eq = pair.IndexOf('=');
        xattrs[pair[..eq]] = pair[(eq + 1)..].ToLowerInvariant();
      }
      result[f[1]] = new Node(f[1], kind, Convert.ToInt32(f[3], 8),
        uint.Parse(f[4], CultureInfo.InvariantCulture), uint.Parse(f[5], CultureInfo.InvariantCulture),
        long.Parse(f[6], CultureInfo.InvariantCulture), Time(f[7]), Time(f[8]), Time(f[9]), f[10], xattrs);
    }
    return result;

    static (long, int) Time(string text) {
      var dot = text.IndexOf('.');
      return dot < 0
        ? (long.Parse(text, CultureInfo.InvariantCulture), 0)
        : (long.Parse(text[..dot], CultureInfo.InvariantCulture), int.Parse(text[(dot + 1)..].PadRight(9, '0')[..9], CultureInfo.InvariantCulture));
    }
  }

  private static (long Seconds, int Nanos) Split(DateTimeOffset time) {
    var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
    var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var rest);
    if (rest < 0) { --seconds; rest += TimeSpan.TicksPerSecond; }
    return (seconds, (int)(rest * 100));
  }

  private static string Hex(byte[] value) => "0x" + Convert.ToHexStringLower(value);

  private static (long Seconds, int Nanos) Ticks((long Seconds, int Nanos) time) => (time.Seconds, time.Nanos / 100 * 100);

  /// <summary>The same view of a volume, taken by this package's reader.</summary>
  private static IReadOnlyDictionary<string, Node> ReadOurselves(params string[] paths) {
    var images = paths.Select(File.OpenRead).ToArray();
    try {
      return ReadOurselves(new BcacheFsReader(images));
    } finally {
      foreach (var image in images) image.Dispose();
    }
  }

  private static IReadOnlyDictionary<string, Node> ReadOurselves(BcacheFsReader reader) {
    Assert.That(reader.Valid, Is.True, $"our reader refused the volume: {reader.Status}");
    var result = new Dictionary<string, Node>(StringComparer.Ordinal);

    IReadOnlyDictionary<string, string> Xattrs(string name)
      => reader.ExtendedAttributes.TryGetValue(name, out var found)
        ? found.ToDictionary(kv => kv.Key, kv => Hex(kv.Value), StringComparer.Ordinal)
        : new Dictionary<string, string>();

    foreach (var directory in reader.Directories.Where(d => d != "lost+found" && !d.StartsWith("lost+found/", StringComparison.Ordinal)))
      result[directory] = new Node(directory, 'd', 0, 0, 0, 0, default, default, default, "-", Xattrs(directory));

    foreach (var entry in reader.Entries) {
      Assert.That(entry.Unreadable, Is.Null, $"{entry.Name}: {entry.Unreadable}");
      var m = entry.Metadata;
      var content = entry.LinkTarget ?? Convert.ToHexStringLower(SHA256.HashData(reader.Read(entry)));
      result[entry.Name] = new Node(entry.Name, entry.LinkTarget == null ? 'f' : 'l', m.UnixMode ?? 0,
        m.UnixUserId ?? 0, m.UnixGroupId ?? 0, entry.Size,
        Split(m.LastAccessTimeUtc ?? DateTimeOffset.UnixEpoch), Split(m.LastWriteTimeUtc ?? DateTimeOffset.UnixEpoch),
        Split(m.StatusChangeTimeUtc ?? DateTimeOffset.UnixEpoch), content, Xattrs(entry.Name));
    }
    return result;
  }

  /// <summary>
  /// What the kernel listed and what our reader sees agree on every node: kind,
  /// content, xattrs, and for files and links mode, owner, group, size and times.
  /// </summary>
  private static void AssertSameTree(IReadOnlyDictionary<string, Node> kernel, IReadOnlyDictionary<string, Node> ours) {
    Assert.Multiple(() => {
      Assert.That(ours.Keys.Order(StringComparer.Ordinal), Is.EqualTo(kernel.Keys.Order(StringComparer.Ordinal)), "the same tree");
      foreach (var (name, k) in kernel) {
        if (!ours.TryGetValue(name, out var o)) continue;
        Assert.That(o.Kind, Is.EqualTo(k.Kind), $"{name} kind");
        Assert.That(o.Content, Is.EqualTo(k.Content), $"{name} content");
        Assert.That(o.Xattrs, Is.EquivalentTo(k.Xattrs), $"{name} xattrs");
        if (k.Kind == 'd') continue;
        Assert.That(o.Size, Is.EqualTo(k.Size), $"{name} size");
        // A link's permission bits are always 0777 through the VFS, whatever is stored.
        if (k.Kind == 'f') Assert.That(o.Mode, Is.EqualTo(k.Mode), $"{name} mode");
        Assert.That(o.Uid, Is.EqualTo(k.Uid), $"{name} owner");
        Assert.That(o.Gid, Is.EqualTo(k.Gid), $"{name} group");
        // Our reader's times are DateTimeOffsets, 100 ns apart; a volume bcachefs
        // format made stores nanoseconds.
        Assert.That(o.Atime, Is.EqualTo(Ticks(k.Atime)), $"{name} atime");
        Assert.That(o.Mtime, Is.EqualTo(Ticks(k.Mtime)), $"{name} mtime");
        Assert.That(o.Ctime, Is.EqualTo(Ticks(k.Ctime)), $"{name} ctime");
      }
    });
  }

  // ── The volume this package writes ───────────────────────────────────────

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 31 + seed * 17 + i / 509);
    return result;
  }

  private static readonly DateTimeOffset When = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1_234_567);

  /// <summary>Files with their owners, modes and times, links, directories and xattrs.</summary>
  private static (BcacheFsWriter Writer, Dictionary<string, (byte[] Data, ArchiveEntryMetadata Metadata)> Files,
      Dictionary<string, string> Links, string[] Directories, Dictionary<string, Dictionary<string, byte[]>> Xattrs) Sampler() {
    var files = new Dictionary<string, (byte[], ArchiveEntryMetadata)>(StringComparer.Ordinal) {
      ["HELLO.TXT"] = (Encoding.ASCII.GetBytes("written by CompressionWorkbench\n"),
        new ArchiveEntryMetadata(LastAccessTimeUtc: When, LastWriteTimeUtc: When.AddDays(2), StatusChangeTimeUtc: When.AddMinutes(15),
          CreationTimeUtc: When.AddDays(-30), UnixUserId: 1234, UnixGroupId: 4321, UnixMode: 0x1A0)),
      ["empty.bin"] = ([], new ArchiveEntryMetadata(LastAccessTimeUtc: When, LastWriteTimeUtc: When, StatusChangeTimeUtc: When,
        UnixUserId: 0, UnixGroupId: 0, UnixMode: 0x1A4)),
      ["sub/deeper/NESTED.BIN"] = (Payload(9_000, 1), new ArchiveEntryMetadata(
        LastAccessTimeUtc: new DateTimeOffset(2001, 9, 9, 1, 46, 40, TimeSpan.Zero),
        LastWriteTimeUtc: new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
        StatusChangeTimeUtc: new DateTimeOffset(2038, 1, 19, 3, 14, 8, TimeSpan.Zero),
        UnixUserId: 0, UnixGroupId: 100, UnixMode: 0x1ED)),
      ["sub/odd.bin"] = (Payload(4_097, 2), new ArchiveEntryMetadata(LastAccessTimeUtc: When, LastWriteTimeUtc: When,
        StatusChangeTimeUtc: When, UnixUserId: 65534, UnixGroupId: 65534, UnixMode: 0x124)),
      // Many full extents and a short one.
      ["big.bin"] = (Payload(1_000_123, 3), new ArchiveEntryMetadata(LastAccessTimeUtc: When, LastWriteTimeUtc: When,
        StatusChangeTimeUtc: When, UnixUserId: 1000, UnixGroupId: 1000, UnixMode: 0x1B4)),
    };
    var links = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["link-to-hello"] = "HELLO.TXT",
      ["sub/up"] = "../HELLO.TXT",
    };
    string[] directories = ["sub", "sub/deeper", "empty", "empty/dir"];
    var xattrs = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal) {
      ["HELLO.TXT"] = new(StringComparer.Ordinal) {
        ["user.comment"] = Encoding.ASCII.GetBytes("hello"),
        ["user.binary"] = [0, 1, 2, 0xFF],
        ["trusted.origin"] = Encoding.ASCII.GetBytes("cwb"),
      },
      ["sub"] = new(StringComparer.Ordinal) { ["user.dir"] = Encoding.ASCII.GetBytes("a directory's attribute") },
      ["big.bin"] = new(StringComparer.Ordinal) { ["user.large"] = Payload(1_900, 9) },
    };

    var writer = new BcacheFsWriter();
    writer.SetLabel("cwb-kernel");
    foreach (var (name, (data, metadata)) in files) writer.AddFile(name, data, metadata);
    foreach (var (name, target) in links) writer.AddSymlink(name, target);
    foreach (var directory in directories) writer.AddDirectory(directory);
    foreach (var (name, attributes) in xattrs)
      foreach (var (key, value) in attributes) writer.AddExtendedAttribute(name, key, value);
    return (writer, files, links, directories, xattrs);
  }

  // ── What the kernel does to it ───────────────────────────────────────────

  /// <summary>
  /// Writes through the driver: new files of several sizes with owners, modes and
  /// times, an xattr, a link, a sparse file; a file of ours removed, one appended
  /// to, one overwritten in the middle (which splits its extents), an xattr of ours
  /// replaced and one removed.
  /// </summary>
  private const string KernelWrites = """
    set -e
    cd /sysroot
    mkdir -p kernel/deep
    seq 1 60000 > kernel/deep/seq.txt
    chown 4242:2424 kernel/deep/seq.txt
    chmod 0600 kernel/deep/seq.txt
    touch -m -d '2021-05-06 07:08:09.123456789 UTC' kernel/deep/seq.txt
    touch -a -d '2020-01-02 03:04:05.5 UTC' kernel/deep/seq.txt
    printf 'tiny' > kernel/tiny.txt
    setfattr -n user.origin -v kernel kernel/tiny.txt
    ln -s deep/seq.txt kernel/link
    truncate -s 3M kernel/sparse.bin
    printf 'tail' >> kernel/sparse.bin
    rm sub/deeper/NESTED.BIN
    printf 'appended by the kernel' >> sub/odd.bin
    dd if=/dev/zero of=big.bin bs=4096 seek=37 count=3 conv=notrunc status=none
    setfattr -n user.comment -v replaced HELLO.TXT
    setfattr -x user.binary HELLO.TXT
    sync
    """;

  // ── Tests ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenOurVolume_WhenTheKernelMountsIt_ThenTreeModesOwnersTimesXattrsLinksAndBytesMatch() {
    BcacheFsKernel.Require();
    var (writer, files, links, directories, xattrs) = Sampler();
    var path = Path.Combine(this._tmpDir, "ours.img");
    using (var stream = File.Create(path)) writer.WriteTo(stream);

    var listing = ParseListing(BcacheFsKernel.Session(path, ListingScript));

    Assert.Multiple(() => {
      Assert.That(listing.Keys.Order(StringComparer.Ordinal),
        Is.EqualTo(files.Keys.Concat(links.Keys).Concat(directories).Order(StringComparer.Ordinal)), "the kernel sees the tree");
      foreach (var (name, (data, m)) in files) {
        if (!listing.TryGetValue(name, out var n)) continue;
        Assert.That(n.Kind, Is.EqualTo('f'), $"{name} kind");
        Assert.That(n.Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(data))), $"{name} bytes");
        Assert.That(n.Size, Is.EqualTo(data.Length), $"{name} size");
        Assert.That(n.Mode, Is.EqualTo(m.UnixMode), $"{name} mode");
        Assert.That(n.Uid, Is.EqualTo(m.UnixUserId), $"{name} owner");
        Assert.That(n.Gid, Is.EqualTo(m.UnixGroupId), $"{name} group");
        Assert.That(n.Atime, Is.EqualTo(Split(m.LastAccessTimeUtc!.Value)), $"{name} atime");
        Assert.That(n.Mtime, Is.EqualTo(Split(m.LastWriteTimeUtc!.Value)), $"{name} mtime");
        Assert.That(n.Ctime, Is.EqualTo(Split(m.StatusChangeTimeUtc!.Value)), $"{name} ctime");
      }
      foreach (var (name, target) in links) {
        if (!listing.TryGetValue(name, out var n)) continue;
        Assert.That(n.Kind, Is.EqualTo('l'), $"{name} kind");
        Assert.That(n.Content, Is.EqualTo(target), $"{name} target");
      }
      foreach (var directory in directories) {
        if (!listing.TryGetValue(directory, out var n)) continue;
        Assert.That(n.Kind, Is.EqualTo('d'), $"{directory} kind");
        Assert.That(n.Mode, Is.EqualTo(0x1ED), $"{directory} mode");
      }
      foreach (var (name, n) in listing) {
        var want = xattrs.TryGetValue(name, out var found)
          ? found.ToDictionary(kv => kv.Key, kv => Hex(kv.Value), StringComparer.Ordinal)
          : new Dictionary<string, string>();
        Assert.That(n.Xattrs, Is.EquivalentTo(want), $"{name} xattrs");
      }
    });

    BcacheFsKernel.AssertFsckClean(path);
  }

  [Test, Category("RoundTrip")]
  public void GivenOurVolume_WhenTheKernelWritesIntoIt_ThenFsckIsCleanAndOurReaderSeesWhatTheKernelSees() {
    BcacheFsKernel.Require();
    var path = Path.Combine(this._tmpDir, "written.img");
    using (var stream = File.Create(path)) Sampler().Writer.WriteTo(stream);

    var kernel = ParseListing(BcacheFsKernel.Session(path, KernelWrites + "\n" + ListingScript));
    Assert.That(kernel.Keys, Does.Contain("kernel/deep/seq.txt").And.Not.Contain("sub/deeper/NESTED.BIN"),
      "precondition: the kernel's writes landed");

    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(kernel, ReadOurselves(path));
  }

  [Test, Category("RoundTrip")]
  public void GivenAVolumeTheKernelWroteInto_WhenWeAddRemoveAndDefragmentInPlace_ThenFsckIsCleanAndTheKernelAgrees() {
    BcacheFsKernel.Require();
    var path = Path.Combine(this._tmpDir, "edited.img");
    using (var stream = File.Create(path)) Sampler().Writer.WriteTo(stream);
    BcacheFsKernel.Session(path, KernelWrites);

    var added = Payload(200_000, 21);
    var addedMetadata = new ArchiveEntryMetadata(LastAccessTimeUtc: When, LastWriteTimeUtc: When.AddHours(1),
      StatusChangeTimeUtc: When.AddHours(2), UnixUserId: 501, UnixGroupId: 20, UnixMode: 0x180);
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      descriptor.Add(image, [
        ArchiveInputInfo.InMemory("ours/added.bin", added, addedMetadata),
        ArchiveInputInfo.InMemory("kernel/tiny.txt", Encoding.ASCII.GetBytes("replaced by us")),
      ]);
      image.Position = 0;
      descriptor.Remove(image, ["kernel/deep/seq.txt", "link-to-hello"]);
      image.Position = 0;
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    }
    BcacheFsKernel.AssertFsckClean(path);

    var ours = ReadOurselves(path);
    var kernel = ParseListing(BcacheFsKernel.Session(path, ListingScript));
    AssertSameTree(kernel, ours);
    Assert.Multiple(() => {
      Assert.That(kernel.Keys, Does.Not.Contain("kernel/deep/seq.txt").And.Not.Contain("link-to-hello"));
      Assert.That(kernel["ours/added.bin"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(added))));
      Assert.That(kernel["ours/added.bin"].Mode, Is.EqualTo(0x180));
      Assert.That(kernel["kernel/tiny.txt"].Content,
        Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("replaced by us")))));
      Assert.That(kernel["kernel/tiny.txt"].Xattrs["user.origin"], Is.EqualTo(Hex(Encoding.ASCII.GetBytes("kernel"))),
        "replacing a file's bytes keeps its xattrs");
      Assert.That(kernel["kernel/sparse.bin"].Size, Is.EqualTo(3 * 1024 * 1024 + 4));
    });

    // And the kernel can keep writing into what we left.
    var again = ParseListing(BcacheFsKernel.Session(path, "set -e; cd /sysroot; seq 1 1000 > ours/after.txt; rm -f big.bin; sync\n" + ListingScript));
    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(again, ReadOurselves(path));
  }

  /// <summary>
  /// Files the kernel cloned with <c>cp --reflink=always</c> — their data moved into the reflink
  /// tree, one clone partly overwritten — are read as the kernel reads them; after we add a file,
  /// replace one clone and remove another, fsck is clean and the kernel reads what we read, and
  /// after the last names of the shared data go, fsck is still clean.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void GivenFilesTheKernelReflinked_WhenWeEditAroundAndRemoveThemInPlace_ThenFsckIsCleanAndTheKernelAgrees() {
    BcacheFsKernel.Require();
    var path = Path.Combine(this._tmpDir, "reflinked.img");
    using (var stream = File.Create(path)) Sampler().Writer.WriteTo(stream);
    var kernel = ParseListing(BcacheFsKernel.Session(path, """
      set -e
      cd /sysroot
      mkdir -p shared
      cp --reflink=always big.bin shared/big-clone.bin
      cp --reflink=always big.bin shared/big-clone2.bin
      dd if=/dev/urandom of=shared/big-clone2.bin bs=4096 seek=5 count=2 conv=notrunc status=none
      seq 1 50000 > shared/seq.txt
      cp --reflink=always shared/seq.txt shared/seq-clone.txt
      sync
      """ + "\n" + ListingScript));
    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(kernel, ReadOurselves(path));

    var added = Payload(70_000, 41);
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      descriptor.Add(image, [
        ArchiveInputInfo.InMemory("shared/added.bin", added),
        ArchiveInputInfo.InMemory("shared/seq-clone.txt", Encoding.ASCII.GetBytes("replaced clone\n")),
      ]);
      image.Position = 0;
      descriptor.Remove(image, ["shared/big-clone.bin"]);
    }
    BcacheFsKernel.AssertFsckClean(path);
    var afterEdit = ParseListing(BcacheFsKernel.Session(path, ListingScript));
    AssertSameTree(afterEdit, ReadOurselves(path));
    Assert.Multiple(() => {
      Assert.That(afterEdit.Keys, Does.Not.Contain("shared/big-clone.bin"));
      Assert.That(afterEdit["shared/added.bin"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(added))));
      Assert.That(afterEdit["shared/big-clone2.bin"].Content, Is.EqualTo(kernel["shared/big-clone2.bin"].Content));
      Assert.That(afterEdit["big.bin"].Content, Is.EqualTo(kernel["big.bin"].Content));
    });

    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      descriptor.Remove(image, ["big.bin", "shared/big-clone2.bin", "shared/seq.txt"]);
    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(ParseListing(BcacheFsKernel.Session(path, ListingScript)), ReadOurselves(path));
  }

  /// <summary>
  /// Data the kernel stored compressed is carried through our in-place edits as it
  /// is: its accounting uses the compressed size, the kernel still reads it, and a
  /// defragmentation that would have to decode it to move it is refused with the
  /// volume left byte for byte as it was.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void GivenAVolumeTheKernelWroteCompressedDataInto_WhenWeEditInPlace_ThenTheKernelAgreesAndDefragmentIsRefusedUnchanged() {
    BcacheFsKernel.Require();
    var path = Path.Combine(this._tmpDir, "compressed.img");
    using (var stream = File.Create(path)) Sampler().Writer.WriteTo(stream);
    BcacheFsKernel.Session(path, """
      set -e
      cd /sysroot
      mkdir -p packed
      seq 1 80000 > packed/seq.txt
      yes 'a line the kernel compresses' | head -c 400000 > packed/yes.txt
      sync
      """, "noatime,compression=zstd");

    var ours = ReadOurselves(path);
    Assert.That(ours["packed/yes.txt"].Size, Is.EqualTo(400_000), "precondition: our reader decodes the kernel's zstd extents");

    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      descriptor.Add(image, [ArchiveInputInfo.InMemory("ours/added.bin", Payload(90_000, 31))]);
      image.Position = 0;
      descriptor.Remove(image, ["packed/seq.txt"]);
    }
    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(ParseListing(BcacheFsKernel.Session(path, ListingScript)), ReadOurselves(path));

    var before = File.ReadAllBytes(path);
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      Assert.Throws<NotSupportedException>(() =>
        descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }));
    Assert.That(File.ReadAllBytes(path).SequenceEqual(before), Is.True, "a refused defragmentation changes nothing");
  }

  /// <summary>
  /// A volume <c>bcachefs format</c> spread over several image files that the kernel mounted as
  /// one and wrote into: after we add files spanning many buckets, replace one of the kernel's
  /// and remove another through every member at once, fsck over all members is clean, the
  /// kernel mounting them together reads what we read, and keeps writing.
  /// </summary>
  [TestCase("", 2, TestName = "GivenTwoMembersTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [TestCase("--replicas=2", 2, TestName = "GivenTwoMirroredMembersTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [TestCase("--replicas=2", 3, TestName = "GivenThreeMembersTwoCopiesTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [Category("RoundTrip")]
  public void MultiDeviceKernelRoundTrip(string formatOptions, int memberCount) {
    BcacheFsKernel.Require();
    var paths = Enumerable.Range(0, memberCount).Select(i => Path.Combine(this._tmpDir, $"member{i}.img")).ToArray();
    var wsl = paths.Select(FsInteropToolbox.WinToWsl).ToArray();
    var format = FsInteropToolbox.RunWsl(string.Join(" && ", wsl.Select(p => $"truncate -s 96M {p}"))
      + $" && bcachefs format -q {formatOptions} {string.Join(" ", wsl)}");
    Assert.That(format.ExitCode, Is.Zero, format.StdOut + format.StdErr);
    BcacheFsKernel.Session(paths, """
      set -e
      cd /sysroot
      mkdir -p kernel/deep
      seq 1 60000 > kernel/deep/seq.txt
      printf 'tiny' > kernel/tiny.txt
      setfattr -n user.origin -v kernel kernel/tiny.txt
      ln -s tiny.txt kernel/link
      head -c 900000 /dev/urandom > big.bin
      for i in 1 2 3 4 5 6; do head -c 70000 /dev/urandom > kernel/r$i.bin; done
      sync
      """);
    BcacheFsKernel.AssertFsckClean(paths);

    var added = Payload(1_200_000, 51);
    var replacement = Payload(70_000, 52);
    var descriptor = new BcacheFsFormatDescriptor();
    var members = paths.Select(p => (Stream)new FileStream(p, FileMode.Open, FileAccess.ReadWrite)).ToArray();
    try {
      descriptor.AddToVolume(members, [
        ArchiveInputInfo.InMemory("ours/added.bin", added, new ArchiveEntryMetadata(LastAccessTimeUtc: When,
          LastWriteTimeUtc: When.AddHours(1), StatusChangeTimeUtc: When.AddHours(2), UnixUserId: 501, UnixGroupId: 20, UnixMode: 0x180)),
        ArchiveInputInfo.InMemory("kernel/tiny.txt", replacement),
      ]);
      descriptor.RemoveFromVolume(members, ["kernel/deep/seq.txt", "kernel/r3.bin"]);
    } finally {
      foreach (var member in members) member.Dispose();
    }
    BcacheFsKernel.AssertFsckClean(paths);

    var kernel = ParseListing(BcacheFsKernel.Session(paths, ListingScript));
    AssertSameTree(kernel, ReadOurselves(paths));
    Assert.Multiple(() => {
      Assert.That(kernel.Keys, Does.Not.Contain("kernel/deep/seq.txt").And.Not.Contain("kernel/r3.bin"));
      Assert.That(kernel["ours/added.bin"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(added))));
      Assert.That(kernel["ours/added.bin"].Mtime, Is.EqualTo(Split(When.AddHours(1))));
      Assert.That(kernel["kernel/tiny.txt"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(replacement))));
    });

    var again = ParseListing(BcacheFsKernel.Session(paths, "set -e; cd /sysroot; seq 1 1000 > ours/after.txt; rm -f big.bin; sync\n" + ListingScript));
    BcacheFsKernel.AssertFsckClean(paths);
    AssertSameTree(again, ReadOurselves(paths));
  }

  /// <summary>
  /// A volume <c>bcachefs format</c> laid out (its bucket size, 4 KiB blocks, its journal) that the
  /// kernel then wrote into: after we add files spanning several buckets, replace one of the
  /// kernel's and remove another, fsck is clean, the kernel reads what we read, and the kernel can
  /// keep writing into it.
  /// </summary>
  [TestCase("", TestName = "GivenADefaultFormatVolumeTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [TestCase("--bucket=32k --btree_node_size=32k", TestName = "GivenA32KBucketVolumeTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [TestCase("--bucket=256k --btree_node_size=256k", TestName = "GivenA256KBucketVolumeTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [TestCase("--bucket=256k --btree_node_size=64k", TestName = "GivenSmallerNodesThanBucketsTheKernelWroteInto_WhenWeEditInPlace_ThenFsckIsCleanAndTheKernelAgrees")]
  [Category("RoundTrip")]
  public void ForeignLayoutKernelRoundTrip(string formatOptions) {
    BcacheFsKernel.Require();
    var path = Path.Combine(this._tmpDir, "formatted.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var format = FsInteropToolbox.RunWsl($"truncate -s 128M {wsl} && bcachefs format -q {formatOptions} {wsl}");
    Assert.That(format.ExitCode, Is.Zero, format.StdOut + format.StdErr);
    BcacheFsKernel.Session(path, """
      set -e
      cd /sysroot
      mkdir -p kernel/deep
      seq 1 60000 > kernel/deep/seq.txt
      printf 'tiny' > kernel/tiny.txt
      setfattr -n user.origin -v kernel kernel/tiny.txt
      ln -s tiny.txt kernel/link
      truncate -s 3M kernel/sparse.bin
      printf 'tail' >> kernel/sparse.bin
      head -c 500000 /dev/urandom > big.bin
      sync
      """);

    var added = Payload(600_000, 41);
    var replacement = Payload(70_000, 42);
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      descriptor.Add(image, [
        ArchiveInputInfo.InMemory("ours/added.bin", added, new ArchiveEntryMetadata(LastAccessTimeUtc: When,
          LastWriteTimeUtc: When.AddHours(1), StatusChangeTimeUtc: When.AddHours(2), UnixUserId: 501, UnixGroupId: 20, UnixMode: 0x180)),
        ArchiveInputInfo.InMemory("kernel/tiny.txt", replacement),
      ]);
      image.Position = 0;
      descriptor.Remove(image, ["kernel/deep/seq.txt"]);
    }
    BcacheFsKernel.AssertFsckClean(path);

    var kernel = ParseListing(BcacheFsKernel.Session(path, ListingScript));
    AssertSameTree(kernel, ReadOurselves(path));
    Assert.Multiple(() => {
      Assert.That(kernel.Keys, Does.Not.Contain("kernel/deep/seq.txt"));
      Assert.That(kernel["ours/added.bin"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(added))));
      Assert.That(kernel["ours/added.bin"].Mtime, Is.EqualTo(Split(When.AddHours(1))), "times in the volume's own units");
      Assert.That(kernel["ours/added.bin"].Atime, Is.EqualTo(Split(When)));
      Assert.That(kernel["kernel/tiny.txt"].Content, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(replacement))));
    });

    var again = ParseListing(BcacheFsKernel.Session(path, "set -e; cd /sysroot; seq 1 1000 > ours/after.txt; rm -f big.bin; sync\n" + ListingScript));
    BcacheFsKernel.AssertFsckClean(path);
    AssertSameTree(again, ReadOurselves(path));

    // The kernel's own extents share buckets between files; defragmentation brings
    // them into whole buckets of their own and packs them, in this volume's layout.
    foreach (var (mode, zone) in new[] { (DefragMode.ConsolidateAtStart, MetadataZone.Unchanged), (DefragMode.ConsolidateAtEnd, MetadataZone.Back) }) {
      var length = new FileInfo(path).Length;
      using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        descriptor.Defragment(image, new DefragOptions { Mode = mode, MetadataZonePlacement = zone });
      Assert.That(new FileInfo(path).Length, Is.EqualTo(length), $"{mode}/{zone} keeps the volume's size");
      BcacheFsKernel.AssertFsckClean(path);
      AssertSameTree(ParseListing(BcacheFsKernel.Session(path, ListingScript)), ReadOurselves(path));
      AssertSameTree(again, ReadOurselves(path));
    }
  }
}

// (end of fixture)

/// <summary>
/// The libguestfs appliance with a kernel that has the bcachefs driver.
/// </summary>
internal static class BcacheFsKernel {
  private const string Sudo = "echo 1234 | sudo -S";

  /// <summary>Selects the direct backend and lets the appliance read the host kernel and use KVM.</summary>
  private static string Env => "export LIBGUESTFS_BACKEND=direct; " +
    $"{Sudo} chmod 0666 /dev/kvm 2>/dev/null; {Sudo} chmod 0644 /boot/vmlinuz-* 2>/dev/null; ";

  private static string? _kernel;
  private static string? _why;

  /// <summary>The appliance kernel's release, once it has been shown to have the driver.</summary>
  internal static string Kernel => _kernel ?? throw new InvalidOperationException("Require() first.");

  internal static void Require() {
    if (_kernel == null && _why == null) Probe();
    if (_kernel != null) return;
    BcacheFsOracleGate.Missing(BcacheFsOracleGate.RequireKernel, $"No kernel with bcachefs to mount the volume: {_why}");
  }

  private static void Probe() {
    if (!FsInteropToolbox.WslAvailable) { _why = "no Linux environment (WSL)."; return; }
    if (!FsInteropToolbox.WslHasTool("guestfish")) { _why = "guestfish is not installed: `sudo apt install -y libguestfs-tools`."; return; }
    if (!FsInteropToolbox.WslHasTool("bcachefs")) { _why = "bcachefs-tools is not installed: `sudo apt install -y bcachefs-tools`."; return; }
    var r = FsInteropToolbox.RunWsl(Env + "cd $(mktemp -d) && truncate -s 64M p.img && " +
      "printf 'run\\ndebug sh \"modprobe bcachefs; grep -qw bcachefs /proc/filesystems && echo HAS-BCACHEFS $(uname -r)\"\\n' | " +
      "timeout 600 guestfish -a p.img 2>&1; rm -f p.img");
    var line = (r.StdOut + "\n" + r.StdErr).Split('\n').FirstOrDefault(l => l.StartsWith("HAS-BCACHEFS ", StringComparison.Ordinal));
    if (line == null) {
      _why = "the libguestfs appliance kernel has no bcachefs module (Ubuntu 24.04's linux-image-generic has one).\n" + r.StdOut + r.StdErr;
      return;
    }
    _kernel = line["HAS-BCACHEFS ".Length..].Trim();
    TestContext.Progress.WriteLine($"bcachefs kernel: {_kernel}");
  }

  /// <summary>
  /// Mounts <paramref name="imagePath" /> read-write (noatime, so reading does not
  /// change what is being compared), runs <paramref name="script" /> in a shell at
  /// the mount point, unmounts, and returns what the script printed.
  /// </summary>
  internal static string Session(string imagePath, string script, string options = "noatime")
    => Session([imagePath], script, options);

  /// <summary>
  /// The same over a volume of several members: each image is one disk of the appliance, and
  /// the kernel mounts them together (<c>/dev/sda:/dev/sdb:…</c>).
  /// </summary>
  internal static string Session(IReadOnlyList<string> imagePaths, string script, string options = "noatime") {
    var imagePath = imagePaths[0];
    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")));
    var members = string.Join(":", imagePaths.Select((_, i) => $"/dev/sd{(char)('a' + i)}"));
    // guestfish takes one device to mount; several members are mounted by the kernel's own
    // mount from inside the appliance.
    var commands = imagePaths.Count == 1
      ? "run\n" +
        $"mount-options {options} {members} /\n" +
        $"debug sh \"echo {b64} | base64 -d > /tmp/s.sh; sh /tmp/s.sh; echo SCRIPT-EXIT=$?\"\n" +
        "umount-all\n" +
        "debug sh \"dmesg | grep -i bcachefs\"\n"
      : "run\n" +
        $"debug sh \"modprobe bcachefs; if mount -t bcachefs -o {options} {members} /sysroot; then echo {b64} | base64 -d > /tmp/s.sh; sh /tmp/s.sh; echo SCRIPT-EXIT=$?; umount /sysroot; else echo MOUNT-REFUSED; fi\"\n" +
        "debug sh \"dmesg | grep -i bcachefs\"\n";
    var commandFile = Path.Combine(Path.GetDirectoryName(imagePath)!, $"gf-{Guid.NewGuid():N}.txt");
    File.WriteAllText(commandFile, commands.Replace("\r\n", "\n"));
    try {
      var r = FsInteropToolbox.RunWsl(Env +
        $"timeout 900 guestfish {string.Join(" ", imagePaths.Select(p => "-a " + FsInteropToolbox.WinToWsl(p)))} -f {FsInteropToolbox.WinToWsl(commandFile)} 2>&1");
      var output = r.StdOut + "\n" + r.StdErr;
      TestContext.Out.WriteLine($"$ guestfish (kernel {_kernel}) exit {r.ExitCode}\n{output}");
      Assert.That(output, Does.Not.Contain("libguestfs: error"), $"the kernel refused the volume:\n{output}");
      Assert.That(output, Does.Contain("SCRIPT-EXIT=0"), $"the script inside the mount failed:\n{output}");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"guestfish failed:\n{output}");
      return output;
    } finally {
      File.Delete(commandFile);
    }
  }

  /// <summary>The distribution's bcachefs first, then any extra build named in the environment.</summary>
  private static IReadOnlyList<string> Checkers() {
    var extra = Environment.GetEnvironmentVariable("CWB_BCACHEFS_EXTRA") ?? "";
    return ["bcachefs", .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
  }

  /// <summary>Every checker walks the whole volume and finds nothing to repair.</summary>
  /// <summary>Every checker walks the volume on <paramref name="imagePaths" /> (all its members) and finds nothing to repair.</summary>
  internal static void AssertFsckClean(params string[] imagePaths) {
    var wsl = string.Join(" ", imagePaths.Select(FsInteropToolbox.WinToWsl));
    foreach (var checker in Checkers()) {
      var result = FsInteropToolbox.RunWsl($"{checker} fsck -n {wsl}");
      var output = result.StdOut + "\n" + result.StdErr;
      TestContext.Out.WriteLine($"$ {checker} fsck -n  (exit {result.ExitCode})\n{output}");
      Assert.That(result.ExitCode, Is.EqualTo(0), $"{checker} fsck rejected the volume:\n{output}");
      Assert.That(output, Does.Contain("check_nlinks... done"), $"{checker} fsck did not walk the whole volume:\n{output}");
      Assert.That(output, Does.Not.Contain("fixing"), $"{checker} fsck found something to repair:\n{output}");
    }
  }
}
