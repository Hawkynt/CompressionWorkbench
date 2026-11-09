using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Lfs;

namespace Compression.Tests.Lfs;

/// <summary>
/// LFS writer: volumes laid out as <c>newfs_lfs</c> lays them out, edits appended to the log.
/// These tests pin round trips, the append-only rule, the newfs geometry and the segment
/// accounting <c>fsck_lfs</c> checks; <see cref="LfsNetBsdTests"/> proves the result against
/// NetBSD itself.
/// </summary>
[TestFixture]
public class LfsWriterTests {
  private static readonly LfsFormatDescriptor Lfs = new();

  private static byte[] Bytes(int length, int seed) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  private static byte[] Create(params (string Name, byte[]? Data)[] entries) => CreateSized(null, entries);

  private static byte[] CreateSized(string? size, params (string Name, byte[]? Data)[] entries) {
    var inputs = entries.Select(e => e.Data is null
      ? new ArchiveInputInfo(e.Name, e.Name, IsDirectory: true)
      : ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions();
    if (size is not null) options.FormatSpecific["ImageSize"] = size;
    Lfs.Create(ms, inputs, options);
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    var r = new LfsReader(new MemoryStream(image, writable: false));
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, r.Extract);
  }

  private static void AssertTree(byte[] image, IReadOnlyDictionary<string, byte[]> files, params string[] dirs) {
    var r = new LfsReader(new MemoryStream(image, writable: false));
    Assert.That(r.Entries.Where(e => e.IsDirectory).Select(e => e.Path), Is.EquivalentTo(dirs), "directories");
    var actual = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, r.Extract);
    Assert.That(actual.Keys, Is.EquivalentTo(files.Keys), "files");
    foreach (var (k, v) in files) {
      var a = actual[k];
      var at = a.AsSpan().CommonPrefixLength(v);
      Assert.That(a.Length == v.Length && at == v.Length, Is.True, $"{k}: {a.Length} bytes read, {v.Length} written, first difference at {at}");
    }
    AssertAccounting(image);
  }

  /// <summary>
  /// The bookkeeping fsck_lfs pass 5 and pass 0 check, recomputed independently from the tree:
  /// live bytes per segment, clean count, free-list shape.
  /// </summary>
  private static void AssertAccounting(byte[] image) {
    var v = LfsVolume.Load(image);
    var live = new long[v.Nseg];
    foreach (var n in v.Inodes.Values) {
      live[v.Dtosn(n.InodeBlock)] += LfsVolume.DinodeSize;
      foreach (var (d, b) in n.Blocks) live[v.Dtosn(d)] += b;
    }
    Assert.Multiple(() => {
      for (var s = 0; s < v.Nseg; ++s) {
        Assert.That((long)v.Segs[s].NBytes, Is.EqualTo(live[s]), $"segment {s} live bytes");
        if ((v.Segs[s].Flags & LfsVolume.SegDirty) == 0) Assert.That(live[s], Is.Zero, $"clean segment {s} holds data");
      }
      var sb = new LfsReader(new MemoryStream(image, writable: false)).SuperblockBytes;
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(228)), Is.EqualTo((uint)v.Segs.Count(s => (s.Flags & LfsVolume.SegDirty) == 0)), "nclean");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32)), Is.EqualTo(v.FreeHead), "freehd");
      Assert.That(v.FreeHead, Is.Not.Zero, "free list never empty");
      var seen = new HashSet<uint>();
      uint last = 0;
      for (var ino = v.FreeHead; ino != 0; ino = v.Entries[(int)ino].NextFree) {
        Assert.That(seen.Add(ino), Is.True, "free-list cycle");
        Assert.That(v.Entries[(int)ino].Daddr, Is.Zero, $"free inode {ino} has an address");
        last = ino;
      }
      Assert.That(v.FreeTail, Is.EqualTo(last), "free tail");
      for (var ino = 3; ino < v.Entries.Count; ++ino)
        if (v.Entries[ino].Daddr == 0) Assert.That(seen, Does.Contain((uint)ino), $"free inode {ino} off the list");
    });
  }

  // ── create ──────────────────────────────────────────────────────────

  [Test]
  public void GivenFilesAndDirectories_WhenCreated_ThenEveryByteReadsBack() {
    var files = new Dictionary<string, byte[]> {
      ["a.txt"] = "hello lfs\n"u8.ToArray(),
      ["empty"] = [],
      ["d/one-block"] = Bytes(1024, 1),
      ["d/frag"] = Bytes(1500, 2),
      ["d/e/twelve"] = Bytes(12 * 1024, 3),          // exactly the direct blocks
      ["d/e/single"] = Bytes(12 * 1024 + 1, 4),      // first single-indirect block
      ["double"] = Bytes((12 + 256) * 1024 + 700, 5), // into the double-indirect range
    };
    var img = Create([.. files.Select(f => (f.Key, (byte[]?)f.Value)), ("lonely", null)]);
    AssertTree(img, files, "d", "d/e", "lonely");
  }

  [Test]
  public void GivenNoInputs_WhenCreated_ThenAnEmptyRootCheckpoints() {
    var img = Create();
    AssertTree(img, new Dictionary<string, byte[]>());
    Assert.That(Lfs.List(new MemoryStream(img), null), Is.Empty);
  }

  [TestCase(16, 1024, 512, 32768, TestName = "Lfs_Format_16MiB_SmallGeometry")]
  [TestCase(32, 1024, 512, 32768, TestName = "Lfs_Format_32MiB_LastSmallGeometry")]
  [TestCase(48, 8192, 1024, 524288, TestName = "Lfs_Format_48MiB_DefaultBlocksHalvedSegments")]
  [TestCase(256, 8192, 1024, 1048576, TestName = "Lfs_Format_256MiB_DefaultGeometry")]
  public void GivenVolumeSize_WhenFormatted_ThenNewfsGeometryIsChosen(int megabytes, int bsize, int fsize, int ssize) {
    var v = LfsVolume.Format(megabytes << 20, ident: 1, now: DateTimeOffset.FromUnixTimeSeconds(1));
    Assert.Multiple(() => {
      Assert.That(v.Bsize, Is.EqualTo(bsize));
      Assert.That(v.Fsize, Is.EqualTo(fsize));
      Assert.That(v.Fsbpseg * v.Fsize, Is.EqualTo(ssize));
      Assert.That(v.Nseg, Is.GreaterThanOrEqualTo(4 * v.MinFreeSeg));
      Assert.That(v.MinFreeSeg, Is.EqualTo(Math.Max(v.Nseg / 10, 20)));
      Assert.That(v.SbOffs[0] * v.Fsize, Is.EqualTo(8192));
      Assert.That(v.SbOffs[1], Is.EqualTo(Math.Max(v.Nseg / 10, 5) * v.Fsbpseg));
    });
  }

  /// <summary>Values newfs_lfs (NetBSD 11.1) wrote for a 16 MiB volume.</summary>
  [Test]
  public void GivenSixteenMebibytes_WhenFormatted_ThenSuperblockGeometryEqualsNewfsLfs() {
    var img = CreateSized("16 MB");
    var sb = new LfsReader(new MemoryStream(img, writable: false)).SuperblockBytes;
    int At(int o) => BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(o));
    Assert.Multiple(() => {
      Assert.That(At(8), Is.EqualTo(32768), "size");
      Assert.That(At(16), Is.EqualTo(29280), "dsize");
      Assert.That(At(116), Is.EqualTo(511), "nseg");
      Assert.That(At(328), Is.EqualTo(51), "minfreeseg");
      Assert.That(At(376), Is.EqualTo(26), "resvseg");
      Assert.That(At(128), Is.EqualTo(13), "segtabsz");
      Assert.That(At(104), Is.EqualTo(51), "ifpb");
      Assert.That(At(108), Is.EqualTo(42), "sepb");
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(88)), Is.EqualTo(17247252480UL), "maxfilesize");
      Assert.That(Enumerable.Range(0, 10).Select(i => At(188 + 4 * i)), Is.EqualTo(new[] { 16, 3264, 6528, 9792, 13056, 16320, 19584, 22848, 26112, 29376 }), "sboffs");
    });
  }

  [Test]
  public void GivenTooSmallSize_WhenFormatted_ThenRefused()
    => Assert.Throws<NotSupportedException>(() => LfsVolume.Format(1 << 20));

  [Test]
  public void GivenMoreDataThanFits_WhenCreatedAtFixedSize_ThenRefusedNotTruncated()
    => Assert.Throws<NotSupportedException>(() => CreateSized("16 MB", ("big", Bytes(15 << 20, 9))));

  [Test]
  public void GivenManyFiles_WhenCreated_ThenTheIfileGrowsAndSummariesSplit() {
    var files = Enumerable.Range(0, 300).ToDictionary(i => $"f/{i:D3}", i => Bytes(i % 7 * 300, i));
    var img = Create([.. files.Select(f => (f.Key, (byte[]?)f.Value))]);
    AssertTree(img, files, "f");
    Assert.That(LfsVolume.Load(img).Entries, Has.Count.GreaterThan(300));
  }

  [Test]
  public void GivenNameTooLong_WhenCreated_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Create((new string('n', 256), [1])));

  // ── modify ──────────────────────────────────────────────────────────

  private static byte[] Edit(byte[] image, Action<LfsFormatDescriptor, Stream> edit) {
    var ms = new MemoryStream();
    ms.Write(image);
    edit(Lfs, ms);
    return ms.ToArray();
  }

  [Test]
  public void GivenVolume_WhenFilesAddedReplacedAndRemoved_ThenTreeFollows() {
    var img = Create(("keep", Bytes(5000, 1)), ("dir/old", Bytes(100, 2)), ("dir/sub/deep", Bytes(3000, 3)));
    img = Edit(img, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("new/file", Bytes(20000, 4)), ArchiveInputInfo.InMemory("dir/old", Bytes(70, 5))]));
    img = Edit(img, (d, s) => d.Remove(s, ["dir/sub"]));
    AssertTree(img, new Dictionary<string, byte[]> { ["keep"] = Bytes(5000, 1), ["dir/old"] = Bytes(70, 5), ["new/file"] = Bytes(20000, 4) }, "dir", "new");
  }

  [Test]
  public void GivenVolume_WhenEdited_ThenOnlyTheLogHeadAndSuperblocksAreWritten() {
    var before = Create(("a", Bytes(40000, 1)), ("b", Bytes(3000, 2)));
    var v0 = LfsVolume.Load(before);
    var after = Edit(before, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("c", Bytes(9000, 3))]));
    var sbs = v0.SbOffs.Take(2).Select(o => (Start: v0.ByteOf(o), End: v0.ByteOf(o) + 8192)).ToList();
    var head = v0.ByteOf(v0.Offset);
    for (var i = 0; i < before.Length; ++i) {
      if (before[i] == after[i] || i >= head || sbs.Any(r => i >= r.Start && i < r.End)) continue;
      Assert.Fail($"byte {i} before the log head changed");
    }
    Assert.That(ReadAll(after)["a"], Is.EqualTo(Bytes(40000, 1)));
  }

  [Test]
  public void GivenRepeatedRewrites_WhenTheLogWraps_ThenEmptySegmentsAreReused() {
    var img = CreateSized("16 MB", ("seed", [1]));
    for (var round = 0; round < 12; ++round)
      img = Edit(img, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("big", Bytes(3 << 20, round))]));
    AssertTree(img, new Dictionary<string, byte[]> { ["seed"] = [1], ["big"] = Bytes(3 << 20, 11) });
  }

  [Test]
  public void GivenEveryFileRemoved_WhenPurged_ThenDirectoriesStayAndFilesGo() {
    var img = Create(("x/1", [1]), ("x/2", [2]), ("3", [3]));
    img = Edit(img, (d, s) => ((IArchivePurgeable)d).Purge(s));
    AssertTree(img, new Dictionary<string, byte[]>(), "x");
  }

  [Test]
  public void GivenKernelWrittenVolume_WhenEdited_ThenOurChangesAndItsDataBothReadBack() {
    var original = LfsTests.Image();
    var keep = ReadAll(original);
    var img = Edit(original, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("ours/new", Bytes(70000, 7))]));
    img = Edit(img, (d, s) => d.Remove(s, ["etc/motd-hardlink"]));
    keep.Remove("etc/motd-hardlink");
    keep["ours/new"] = Bytes(70000, 7);
    var actual = ReadAll(img);
    Assert.That(actual.Keys, Is.EquivalentTo(keep.Keys));
    foreach (var (k, v) in keep) Assert.That(actual[k], Is.EqualTo(v), k);
    AssertAccounting(img);
  }

  [Test]
  public void GivenMissingEntry_WhenRemoved_ThenNothingElseChanges() {
    var img = Create(("a", [1, 2, 3]));
    var after = Edit(img, (d, s) => d.Remove(s, ["nope"]));
    Assert.That(ReadAll(after)["a"], Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test]
  public void GivenFileWhereDirectoryIsNeeded_WhenAdded_ThenRefused() {
    var img = Create(("a", [1]));
    Assert.Throws<IOException>(() => Edit(img, (d, s) => d.Add(s, [ArchiveInputInfo.InMemory("a/b", [2])])));
  }
}
