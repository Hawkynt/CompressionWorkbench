using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// reiser4progs judges every plugin profile, body type and object kind the writer handles:
/// <c>fsck.reiser4 --check</c> must call each volume consistent, <c>debugfs.reiser4</c> must resolve
/// the names (keyed with the volume's own hash and fibration) and show the bytes of plain files, and
/// the volume's plugin profile must survive every edit. Runs in the CI "Reiser4 oracle" gate.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Reiser4FeatureExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4feat_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Text(int lines, int salt) {
    var text = new StringBuilder();
    for (var i = 0; i < lines; ++i) text.Append("line ").Append(i).Append(" of ").Append(salt).Append(": plain words the printf in debugfs cannot misread\n");
    return Encoding.ASCII.GetBytes(text.ToString());
  }

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  private byte[] Mkfs(string name, string options) {
    var path = Path.Combine(this._tmpDir, name + ".mkfs.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"truncate -s 64M {wsl} && mkfs.reiser4 -fffy -L {name} {options} {wsl}");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"mkfs.reiser4 {options} failed:\n{run.StdOut}\n{run.StdErr}");
    return File.ReadAllBytes(path);
  }

  private static void AssertFsckConsistent(string path) {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode, Is.EqualTo(0), $"fsck.reiser4 rejected {Path.GetFileName(path)}:\n{text}");
    Assert.That(text, Does.Contain("FS is consistent"), $"fsck.reiser4 did not call {Path.GetFileName(path)} consistent:\n{text}");
  }

  /// <summary>
  /// The bytes of a plain-text file as debugfs.reiser4 -k reads them through its own path lookup and
  /// body plugins. reiser4progs keys a lookup below the root with its own default profile rather
  /// than the root's plugin set (a kernel-written volume behaves the same way), so the volume's
  /// mkfs options are handed to it as its profile.
  /// </summary>
  private static string Cat(string path, string member, string profile = "") {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 {profile} -k '/{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null 2>/dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -k /{member} failed:\n{run.StdOut}\n{run.StdErr}");
    return run.StdOut;
  }

  /// <summary>What debugfs.reiser4 -i prints about an object (its stat data and every item).</summary>
  private static string Items(string path, string member, string profile = "") {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 {profile} -i '/{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null 2>&1");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -i /{member} failed:\n{run.StdOut}\n{run.StdErr}");
    Assert.That(run.StdOut, Does.Not.Contain("Can't find"), $"debugfs.reiser4 cannot resolve /{member}:\n{run.StdOut}");
    return run.StdOut;
  }

  private static string RootProfile(string path) {
    var info = Items(path, "");
    return string.Join('\n', info.Split('\n').Select(static l => l.Trim()).Where(static l => Regex.IsMatch(l, @"^\w+ : id = ")));
  }

  public static readonly string[] Profiles = [
    "-o hash=tea_hash", "-o hash=fnv1_hash", "-o hash=deg_hash", "-o hash=r5_hash",
    "-o fibration=lexic_fibre", "-o fibration=dot_o_fibre", "-o fibration=ext_1_fibre", "-o fibration=ext_3_fibre",
    "-o formatting=tails", "-o formatting=extents", "-o formatting=smart", "-o create=reg40",
    "-o compress=gzip1", "-o compress=zstd1", "-o compressMode=force", "-o compressMode=none", "-o cluster=4K",
    "-o node=node41", "-o create=reg40,formatting=tails,hash=tea_hash,fibration=ext_3_fibre",
  ];

  [TestCaseSource(nameof(Profiles)), Category("HappyPath")]
  public void GivenAVolumeMkfsMadeWithAPluginProfile_WhenAddingFilesUnderEveryBodyType_ThenFsckPassesTheNamesResolveAndTheProfileStays(string options) {
    Reiser4Oracle.Require();
    var original = this.Mkfs("prof", options);
    var originalPath = this.Save("original.img", original);
    using var image = new MemoryStream();
    image.Write(original);
    var descriptor = new Reiser4FormatDescriptor();
    var names = new[] { "plain.txt", "a-name-longer-than-twenty-three-characters.txt", "source.c", "object.o", "x.abc", new string('z', 255) };
    var seed = 0;
    foreach (var body in new[] { Reiser4FileBody.Extents, Reiser4FileBody.Tails, Reiser4FileBody.Profile })
      descriptor.Add(image, [.. names.Select(n => ArchiveInputInfo.InMemory($"{body}/{n}", Text(300, ++seed)))], body);
    descriptor.AddSymlink(image, "links/to-plain", "../Extents/plain.txt");
    descriptor.AddHardLink(image, "links/hard", "Tails/source.c");
    descriptor.AddSpecialFile(image, "links/fifo", 0x11A4);
    var edited = this.Save("edited.img", image.ToArray());

    AssertFsckConsistent(edited);
    Assert.Multiple(() => {
      Assert.That(RootProfile(edited), Is.EqualTo(RootProfile(originalPath)).And.Not.Empty, "the root's plugin set");
      seed = 0;
      foreach (var body in new[] { Reiser4FileBody.Extents, Reiser4FileBody.Tails, Reiser4FileBody.Profile })
        foreach (var name in names) {
          var text = Encoding.ASCII.GetString(Text(300, ++seed));
          var items = Items(edited, $"{body}/{name}", options);
          // reiser4progs has no decompressor: a cryptcompress file's bytes are the kernel oracle's to judge.
          if (!items.Contains("ccreg40", StringComparison.Ordinal))
            Assert.That(Cat(edited, $"{body}/{name}", options), Does.Contain(text), $"{options}: {body}/{name}");
        }
      Assert.That(Items(edited, "links/to-plain", options), Does.Contain("../Extents/plain.txt"));
      Assert.That(Items(edited, "links/hard", options), Does.Contain("nlink:\t\t2"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheRupasovHash_WhenAddingNames_ThenShortNamesAreAddedAndLongOnesRefusedWithTheImageUntouched() {
    Reiser4Oracle.Require();
    var original = this.Mkfs("rupasov", "-o hash=rupasov_hash");
    using var image = new MemoryStream();
    image.Write(original);
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("short-name.txt", Text(10, 1))]);
    var afterShort = image.ToArray();
    var error = Assert.Throws<NotSupportedException>(() => descriptor.Add(image,
      [ArchiveInputInfo.InMemory("a-name-longer-than-twenty-three-characters.txt", Text(10, 2))]));
    var path = this.Save("rupasov.img", image.ToArray());
    Assert.Multiple(() => {
      Assert.That(error!.Message, Does.Contain("Rupasov"));
      Assert.That(image.ToArray(), Is.EqualTo(afterShort), "a refused edit leaves the image byte-identical");
    });
    AssertFsckConsistent(path);
    Assert.That(Cat(path, "short-name.txt"), Does.Contain(Encoding.ASCII.GetString(Text(10, 1))));
  }

  [TestCaseSource(typeof(Reiser4KernelVolumeTests), nameof(Reiser4KernelVolumeTests.EditableVolumes)), Category("HappyPath")]
  public void GivenAVolumeTheKernelWrote_WhenEditing_ThenFsckCallsItConsistent(string volume) {
    Reiser4Oracle.Require();
    using var image = new MemoryStream();
    image.Write(Reiser4KernelVolumeTests.Image(volume));
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("added/a-new-name-longer-than-twenty-three.txt", Text(500, 3))], Reiser4FileBody.Profile);
    descriptor.AddSymlink(image, "added/link", "../f_100");
    descriptor.Remove(image, ["dir/foo.o"]);
    AssertFsckConsistent(this.Save($"{volume}.img", image.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenATallTree_WhenScatteringAndDefragmentingInPlace_ThenFsckCallsEveryStepConsistentAndNoNodeMoves() {
    Reiser4Oracle.Require();
    var writer = new Reiser4Writer { BlockCount = 16_384 };
    for (var i = 0; i < 1_500; ++i) writer.AddFile($"t/f{i:D5}", Text(1 + i % 60, i));
    using var image = new MemoryStream(writer.Build());
    var nodes = Reiser4TestLayouts.NodeBlocks(image);
    Assert.That(Reiser4TestLayouts.NodeBlocks(image).Length, Is.GreaterThan(20), "a tree of several twigs");
    Reiser4TestLayouts.ScatterInPlace(image);
    AssertFsckConsistent(this.Save("scattered.img", image.ToArray()));
    new Reiser4FormatDescriptor().Defragment(image);
    var path = this.Save("defragmented.img", image.ToArray());
    AssertFsckConsistent(path);
    Assert.That(Reiser4TestLayouts.NodeBlocks(image), Is.EqualTo(nodes), "the in-place pass moved data, not nodes");
    Assert.That(Cat(path, "t/f01499"), Does.Contain(Encoding.ASCII.GetString(Text(1 + 1_499 % 60, 1_499))));
  }
}
