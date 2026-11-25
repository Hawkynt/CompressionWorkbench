using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// Short-key volumes judged in both directions by <see cref="Reiser4ShortKeyOracle" />: a kernel
/// built with <c>REISER4_LARGE_KEY=0</c> mounts what the writer and the editor make and reads
/// every object back, the fixed <c>fsck.reiser4</c> calls it consistent, and what that kernel
/// writes into our volumes is what the reader reads.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Reiser4ShortKeyOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4short_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Names on both sides of what a short key holds whole (15 bytes) and a large key would (23).</summary>
  private static readonly string[] Names = [
    "s", "seven-7", "eight-08", "fifteen-bytes-x", "fifteen-bytes-y", "sixteen-bytes-xy", "sixteen-bytes-yz",
    "twenty-three-bytes-xyz1", "twenty-four-bytes-xyz123", "x.c", "x.o", "name.abc", new string('L', 200) + ".tar.gz", new string('z', 255),
  ];

  private static byte[] Random(int size, int seed) {
    var bytes = new byte[size];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  private sealed record Expected(string Type, long Size, string Value, uint Links = 0);

  private static Reiser4KernelOracle.Mount AssertKernelReads(string path, IReadOnlyDictionary<string, Expected> expected,
      IReadOnlySet<string>? unchecked_ = null, string? prelude = null) {
    var mount = Reiser4KernelOracle.Read(path, Reiser4ShortKeyOracle.KernelImage, prelude);
    Assert.That(mount.Complaints, Is.Empty, "the kernel logged complaints:\n" + string.Join('\n', mount.Complaints));
    var seen = mount.Manifest.Keys.Where(static k => k != ".").Order(StringComparer.Ordinal).ToArray();
    Assert.That(seen, Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)), "the namespace the kernel resolves");
    Assert.Multiple(() => {
      foreach (var (name, want) in expected) {
        if (unchecked_?.Contains(name) == true) continue;
        var got = mount.Manifest[name];
        Assert.That(got.Type, Is.EqualTo(want.Type), name + " type");
        if (want.Type != "directory") Assert.That(got.Size, Is.EqualTo(want.Size), name + " size");
        if (want.Value.Length > 0) Assert.That(got.Value, Is.EqualTo(want.Value), name + " bytes or target");
        if (want.Links != 0) Assert.That(got.Links, Is.EqualTo(want.Links), name + " links");
      }
    });
    return mount;
  }

  [TestCase(Reiser4FileBody.Extents)]
  [TestCase(Reiser4FileBody.Smart)]
  [Category("HappyPath")]
  public void GivenAShortKeyVolumeTheWriterMade_WhenTheShortKeyKernelMountsIt_ThenItReadsBackEveryObjectAndFsckAgrees(Reiser4FileBody body) {
    Reiser4ShortKeyOracle.Require();
    var writer = new Reiser4Writer { ShortKeys = true, FileBody = body, BlockCount = 8192 };
    var expected = new Dictionary<string, Expected>(StringComparer.Ordinal);
    var seed = 0;
    void Add(string name, byte[] data) {
      writer.AddFile(name, data);
      expected[name] = new Expected(data.Length == 0 ? "regular empty file" : "regular file", data.Length, Md5(data));
    }
    foreach (var size in new[] { 0, 1, 4_095, 4_096, 4_097, 70_000 }) Add($"sizes/f{size}", Random(size, ++seed));
    foreach (var name in Names) Add("names/" + name, Random(10 + seed, ++seed));
    for (var i = 0; i < 300; ++i) Add($"many/m{i}", Random(i % 50, ++seed));
    writer.AddDirectory("empty/inner");
    writer.AddSymlink("links/short", "../sizes/f1");
    writer.AddHardLink("links/hard", "sizes/f4097");
    writer.AddSpecialFile("links/fifo", 0x11A4);
    expected["sizes/f4097"] = expected["sizes/f4097"] with { Links = 2 };
    expected["links/hard"] = expected["sizes/f4097"];
    expected["links/short"] = new Expected("symbolic link", 11, "../sizes/f1");
    expected["links/fifo"] = new Expected("fifo", 0, "");
    foreach (var dir in new[] { "sizes", "names", "many", "empty", "empty/inner", "links" }) expected[dir] = new Expected("directory", 0, "");

    var path = this.Save($"short-{body}.img", writer.Build());
    Reiser4ShortKeyOracle.AssertFsckConsistent(path);
    AssertKernelReads(path, expected);
  }

  [TestCaseSource(typeof(Reiser4KernelVolumeTests), nameof(Reiser4KernelVolumeTests.ShortKeyVolumes)), Category("HappyPath")]
  public void GivenAShortKeyVolumeTheKernelWrote_WhenEditing_ThenTheKernelReadsEveryCarriedAndAddedObjectAndFsckAgrees(string volume) {
    Reiser4ShortKeyOracle.Require();
    var manifest = Reiser4KernelVolumeTests.Manifest(volume);
    var expected = manifest.Where(static p => p.Key != ".").ToDictionary(static p => p.Key,
      static p => new Expected(p.Value.Type, p.Value.Size, p.Value.Value, p.Value.Links), StringComparer.Ordinal);
    using var image = new MemoryStream();
    image.Write(Reiser4KernelVolumeTests.Image(volume));
    var descriptor = new Reiser4FormatDescriptor();
    var seed = 100;
    var inputs = new List<ArchiveInputInfo>();
    foreach (var name in Names) {
      var data = Random(50 + seed, ++seed);
      inputs.Add(ArchiveInputInfo.InMemory("added/" + name, data));
      expected["added/" + name] = new Expected("regular file", data.Length, Md5(data));
    }
    descriptor.Add(image, inputs, Reiser4FileBody.Profile);
    descriptor.AddSymlink(image, "added/link", "../f_100");
    descriptor.AddHardLink(image, "lengths/another-name-for-f4097", "f_4097");
    descriptor.Remove(image, ["dir/foo.o", "lengths/sixteen-bytes-xy"]);
    expected.Remove("dir/foo.o");
    expected.Remove("lengths/sixteen-bytes-xy");
    expected["added"] = new Expected("directory", 0, "");
    expected["added/link"] = new Expected("symbolic link", 8, "../f_100");
    expected["f_4097"] = expected["f_4097"] with { Links = expected["f_4097"].Links + 1 };
    expected["lengths/another-name-for-f4097"] = expected["f_4097"];

    var path = this.Save($"{volume}-edited.img", image.ToArray());
    Reiser4ShortKeyOracle.AssertFsckConsistent(path);
    AssertKernelReads(path, expected, new HashSet<string>(StringComparer.Ordinal) { "dir", "lengths" });
  }

  [Test, Category("HappyPath")]
  public void GivenAShortKeyVolumeMkfsMade_WhenTheEditorAddsFiles_ThenTheKernelAndFsckAgree() {
    Reiser4ShortKeyOracle.Require();
    var image = new MemoryStream();
    image.Write(Reiser4ShortKeyOracle.Mkfs(Path.Combine(this._tmpDir, "mkfs.img"), ",create=reg40"));
    var expected = new Dictionary<string, Expected>(StringComparer.Ordinal) { ["d"] = new("directory", 0, "") };
    var seed = 200;
    var inputs = new List<ArchiveInputInfo>();
    foreach (var name in Names) {
      var data = Random(3_000 + seed, ++seed);
      inputs.Add(ArchiveInputInfo.InMemory("d/" + name, data));
      expected["d/" + name] = new Expected("regular file", data.Length, Md5(data));
    }
    new Reiser4FormatDescriptor().Add(image, inputs, Reiser4FileBody.Profile);
    var path = this.Save("mkfs-edited.img", image.ToArray());
    Reiser4ShortKeyOracle.AssertFsckConsistent(path);
    AssertKernelReads(path, expected);
  }

  /// <summary>
  /// The other direction: the kernel adds, links and removes names in a volume the writer made,
  /// and the reader must then see exactly what the kernel reads back — names keyed by the kernel
  /// under the rules the writer follows, found only through those keys.
  /// </summary>
  [Test, Category("HappyPath")]
  public void GivenAShortKeyVolumeTheWriterMade_WhenTheKernelWritesIntoIt_ThenTheReaderSeesWhatTheKernelReadsBack() {
    Reiser4ShortKeyOracle.Require();
    var writer = new Reiser4Writer { ShortKeys = true, BlockCount = 8192 };
    writer.AddFile("ours/keep.bin", Random(5_000, 1));
    writer.AddFile("ours/drop-this-one-please.bin", Random(10, 2));
    var path = this.Save("kernel-writes.img", writer.Build());
    var prelude = string.Join('\n',
      "mount -t reiser4 /dev/vda /mnt || { echo MOUNTFAIL; exit 1; }",
      "mkdir /mnt/kernel",
      $"for n in {string.Join(' ', Names)}; do echo \"$n\" > /mnt/kernel/$n; done",
      "i=0; while [ $i -lt 200 ]; do echo \"file $i\" > /mnt/kernel/k$i; i=$((i+1)); done",
      "dd if=/dev/urandom of=/mnt/ours/kernel-wrote-this-big-file bs=4096 count=20 2>/dev/null",
      "ln -s ../ours/keep.bin /mnt/kernel/link",
      "ln /mnt/ours/keep.bin /mnt/kernel/second-name-of-keep.bin",
      "rm /mnt/ours/drop-this-one-please.bin",
      "umount /mnt || echo UMOUNTFAIL");
    var mount = Reiser4KernelOracle.Read(path, Reiser4ShortKeyOracle.KernelImage, prelude);
    Assert.That(mount.Complaints, Is.Empty, "the kernel logged complaints:\n" + string.Join('\n', mount.Complaints));
    Assert.That(mount.Manifest.ContainsKey("kernel/k199") && mount.Manifest.ContainsKey("kernel/" + new string('z', 255)), Is.True,
      "the kernel wrote what it was asked to:\n" + mount.Log);

    Reiser4ShortKeyOracle.AssertFsckConsistent(path);
    using var reader = new Reiser4Reader(new MemoryStream(File.ReadAllBytes(path)));
    Assert.Multiple(() => Reiser4KernelVolumeTests.AssertMatches(reader, mount.Manifest, "kernel-writes"));
  }
}
