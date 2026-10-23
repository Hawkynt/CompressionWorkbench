using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// The Reiser4 kernel judges what the writer produces: it mounts each volume, resolves every path
/// and reads every byte, and its manifest must be what was written. Volumes it wrote itself are
/// edited and handed back to it, so carried objects, new names keyed under every hash and
/// fibration plugin, and compressed clusters written with lzo1, gzip1 and zstd1 are all read by the
/// implementation that owns the format. Wherever reiser4progs is installed, fsck.reiser4 checks the
/// same volumes.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Reiser4KernelOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4kernel_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Random(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static byte[] Text(int lines) {
    var text = new StringBuilder();
    for (var i = 0; i < lines; ++i) text.Append("line ").Append(i).Append(": compressible text for the cluster codecs\n");
    return Encoding.ASCII.GetBytes(text.ToString());
  }

  private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  /// <summary>fsck.reiser4 --check on a copy, when reiser4progs is reachable.</summary>
  private static void FsckIfAvailable(string path) {
    if (!FsInteropToolbox.WslHasTool("fsck.reiser4")) return;
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode == 0 && text.Contains("FS is consistent"), Is.True, $"fsck.reiser4 rejected {Path.GetFileName(path)}:\n{text}");
  }

  /// <summary>What a test expects the kernel to find at a path.</summary>
  private sealed record Expected(string Type, long Size, string Value, uint Links = 0, int Mode = -1, ulong Device = 0);

  private static void AssertKernelReads(string path, IReadOnlyDictionary<string, Expected> expected, IReadOnlySet<string>? unchecked_ = null) {
    var mount = Reiser4KernelOracle.Read(path);
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
        if (want.Mode >= 0) Assert.That(got.Mode, Is.EqualTo(want.Mode), name + " mode");
        if (want.Device != 0) Assert.That(got.Device, Is.EqualTo(want.Device), name + " device");
      }
    });
  }

  private static Dictionary<string, Expected> FromManifest(IReadOnlyDictionary<string, Reiser4KernelVolumeTests.ManifestEntry> manifest)
    => manifest.Where(static p => p.Key != ".").ToDictionary(static p => p.Key,
      static p => new Expected(p.Value.Type, p.Value.Size, p.Value.Value, p.Value.Links, p.Value.Mode, p.Value.Device), StringComparer.Ordinal);

  [TestCase(Reiser4FileBody.Extents)]
  [TestCase(Reiser4FileBody.Tails)]
  [TestCase(Reiser4FileBody.Smart)]
  [TestCase(Reiser4FileBody.Compressed)]
  [Category("HappyPath")]
  public void GivenEveryObjectKindTheWriterMakes_WhenTheKernelMountsTheVolume_ThenItReadsBackExactlyWhatWasWritten(Reiser4FileBody body) {
    Reiser4KernelOracle.Require();
    var writer = new Reiser4Writer { FileBody = body, BlockCount = 8192 };
    var expected = new Dictionary<string, Expected>(StringComparer.Ordinal);
    void File(string name, byte[] data) {
      writer.AddFile(name, data);
      expected[name] = new Expected(data.Length == 0 ? "regular empty file" : "regular file", data.Length, Md5(data));
    }
    void Directory(string name) => expected[name] = new Expected("directory", 0, "");
    var seed = 0;
    foreach (var size in new[] { 0, 1, 4_095, 4_096, 4_097, 16_384, 16_385, 70_000 }) File($"sizes/f{size}", Random(size, ++seed));
    File("sizes/text.c", Text(6_000));
    File("names/" + new string('n', 23), Random(10, ++seed));
    File("names/" + new string('n', 24), Random(10, ++seed));
    File("names/a-name-longer-than-twenty-three.txt", Random(10, ++seed));
    File("names/" + new string('x', 255), Random(10, ++seed));
    File("names/x.c", Random(10, ++seed));
    File("names/x.o", Random(10, ++seed));
    writer.AddDirectory("empty/inner");
    writer.AddSymlink("links/short", "../sizes/f1");
    var longTarget = string.Concat(Enumerable.Repeat("target/", 60)) + "end";
    writer.AddSymlink("links/long", longTarget);
    writer.AddHardLink("links/hard", "sizes/f4097");
    writer.AddSpecialFile("special/fifo", 0x11A4);
    writer.AddSpecialFile("special/char", 0x21A4, 1UL << 20 | 3);
    writer.AddSpecialFile("special/block", 0x61A4, 7UL << 20);
    writer.AddSpecialFile("special/socket", 0xC1ED);
    expected["sizes/f4097"] = expected["sizes/f4097"] with { Links = 2 };
    expected["links/hard"] = expected["sizes/f4097"];
    expected["links/short"] = new Expected("symbolic link", 11, "../sizes/f1");
    expected["links/long"] = new Expected("symbolic link", longTarget.Length, longTarget);
    expected["special/fifo"] = new Expected("fifo", 0, "", Mode: 0x1A4);
    expected["special/char"] = new Expected("character special file", 0, "", Device: 1UL << 20 | 3);
    expected["special/block"] = new Expected("block special file", 0, "", Device: 7UL << 20);
    expected["special/socket"] = new Expected("socket", 0, "", Mode: 0x1ED);
    foreach (var dir in new[] { "sizes", "names", "empty", "empty/inner", "links", "special" }) Directory(dir);

    var path = this.Save($"kinds-{body}.img", writer.Build());
    FsckIfAvailable(path);
    AssertKernelReads(path, expected);
  }

  [TestCaseSource(typeof(Reiser4KernelVolumeTests), nameof(Reiser4KernelVolumeTests.EditableVolumes)), Category("HappyPath")]
  public void GivenAVolumeTheKernelWrote_WhenAddingLinkingAndRemoving_ThenTheKernelReadsEveryCarriedAndAddedObject(string volume) {
    Reiser4KernelOracle.Require();
    var manifest = Reiser4KernelVolumeTests.Manifest(volume);
    var expected = FromManifest(manifest);
    using var image = new MemoryStream();
    image.Write(Reiser4KernelVolumeTests.Image(volume));
    var descriptor = new Reiser4FormatDescriptor();

    var text = Text(4_000);
    var noise = Random(30_000, 11);
    // Profile: on a cryptcompress volume the new files are compressed clusters made with the
    // volume's own compression plugin and cluster size.
    descriptor.Add(image, [
      ArchiveInputInfo.InMemory("added/compressible-text-with-a-long-name.txt", text),
      ArchiveInputInfo.InMemory("added/noise.bin", noise),
      new ArchiveInputInfo("", "added/empty", IsDirectory: true),
    ], Reiser4FileBody.Profile);
    descriptor.AddSymlink(image, "added/link", "../f_100");
    descriptor.AddHardLink(image, "added/hard", "f_4097");
    var removed = manifest.Keys.First(static k => k.StartsWith("many/", StringComparison.Ordinal));
    descriptor.Remove(image, [removed, "dir/foo.o"]);

    expected.Remove(removed);
    expected.Remove("dir/foo.o");
    expected["added"] = new Expected("directory", 0, "");
    expected["added/empty"] = new Expected("directory", 0, "");
    expected["added/compressible-text-with-a-long-name.txt"] = new Expected("regular file", text.Length, Md5(text));
    expected["added/noise.bin"] = new Expected("regular file", noise.Length, Md5(noise));
    expected["added/link"] = new Expected("symbolic link", 8, "../f_100");
    expected["f_4097"] = expected["f_4097"] with { Links = expected["f_4097"].Links + 1 };
    expected["added/hard"] = expected["f_4097"];

    var path = this.Save($"{volume}-edited.img", image.ToArray());
    FsckIfAvailable(path);
    AssertKernelReads(path, expected, new HashSet<string>(StringComparer.Ordinal) { "many", "dir" });
  }

  [Test, Category("HappyPath")]
  public void GivenATallTree_WhenDefragmentingInPlace_ThenTheKernelReadsEveryFileFromItsNewBlocks() {
    Reiser4KernelOracle.Require();
    var writer = new Reiser4Writer { BlockCount = 16_384 };
    var expected = new Dictionary<string, Expected>(StringComparer.Ordinal) { ["t"] = new("directory", 0, "") };
    for (var i = 0; i < 1_500; ++i) {
      var data = Random(1 + i % 9_000, i);
      writer.AddFile($"t/f{i:D5}", data);
      expected[$"t/f{i:D5}"] = new Expected("regular file", data.Length, Md5(data));
    }
    using var image = new MemoryStream(writer.Build());
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Remove(image, [.. Enumerable.Range(0, 1_500).Where(static i => i % 3 == 0).Select(static i => $"t/f{i:D5}")]);
    foreach (var i in Enumerable.Range(0, 1_500).Where(static i => i % 3 == 0)) expected.Remove($"t/f{i:D5}");
    // Removal by rebuild leaves the volume compact; scatter it again so the in-place pass has work.
    Reiser4TestLayouts.ScatterInPlace(image);
    byte[] before = image.ToArray();
    descriptor.Defragment(image);
    Assert.That(Reiser4TestLayouts.NodeBlocks(image), Is.EqualTo(Reiser4TestLayouts.NodeBlocks(new MemoryStream(before))),
      "an in-place pass leaves every tree node where it was");
    var path = this.Save("tall-defragmented.img", image.ToArray());
    FsckIfAvailable(path);
    AssertKernelReads(path, expected);
  }
}
