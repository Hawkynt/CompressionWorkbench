using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// The multi-level writer and the metadata-carrying rebuilds, judged by reiser4progs:
/// <c>fsck.reiser4 --check</c> must call each volume consistent, <c>debugfs.reiser4 -s</c>
/// reports the tree height and the volume's identity, and file bytes are taken where
/// <c>debugfs.reiser4 -i</c> says they are — reiser4progs resolves the path to its stat data
/// and extent items, and the test only copies the blocks it names. (<c>debugfs.reiser4 -k</c>
/// cannot be the byte oracle: it passes file contents to <c>printf</c> as the format string.)
/// No Reiser4-capable kernel is reachable, so volumes the tools themselves wrote come from
/// <c>mkfs.reiser4</c> and from <c>fsck.reiser4 --build-fs</c>, which inserts a lost+found.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Reiser4ProfileExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_r4prof_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireReiser4Progs() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    foreach (var tool in new[] { "mkfs.reiser4", "fsck.reiser4", "debugfs.reiser4" })
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"reiser4progs is not installed ('{tool}' missing; `sudo apt install -y reiser4progs`).");
  }

  private static byte[] Payload(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  /// <summary>A volume made by mkfs.reiser4 itself, with extra options.</summary>
  private byte[] Mkfs(string label, string options) {
    var path = Path.Combine(this._tmpDir, label + ".mkfs.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"truncate -s 64M {wsl} && mkfs.reiser4 -fffy -L {label} {options} {wsl}");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"mkfs.reiser4 failed:\n{run.StdOut}\n{run.StdErr}");
    return File.ReadAllBytes(path);
  }

  /// <summary>fsck.reiser4 --check on a copy, so the status it records does not touch the image under test.</summary>
  private static void AssertFsckConsistent(string path) {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode, Is.EqualTo(0), $"fsck.reiser4 rejected {Path.GetFileName(path)}:\n{text}");
    Assert.That(text, Does.Contain("FS is consistent"), $"fsck.reiser4 did not call {Path.GetFileName(path)} consistent:\n{text}");
  }

  /// <summary>The superblock fields debugfs.reiser4 -s prints, by name.</summary>
  private static Dictionary<string, string> Super(string path) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -s {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -s failed:\n{run.StdOut}\n{run.StdErr}");
    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in run.StdOut.Split('\n')) {
      var colon = line.IndexOf(':');
      if (colon <= 0) continue;
      fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
    }
    return fields;
  }

  /// <summary>The directory listing debugfs.reiser4 -k prints: one "[stat key] name" line per entry.</summary>
  private static string[] Listing(string path, string directory) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -k '{directory}' {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -k {directory} failed:\n{run.StdOut}\n{run.StdErr}");
    return [.. run.StdOut.Split('\n').Select(static l => l.Trim()).Where(static l => l.StartsWith('['))];
  }

  /// <summary>The owner, mode and timestamps debugfs.reiser4 -i prints for an object.</summary>
  private static string[] Ownership(string path, string member) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -i '{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -i {member} failed:\n{run.StdOut}\n{run.StdErr}");
    return [.. run.StdOut.Split('\n').Select(static l => l.Trim())
      .Where(static l => Regex.IsMatch(l, @"^(mode|uid|gid|atime|mtime|ctime):"))];
  }

  private static readonly Regex Run = new(@"(\d+)\((\d+)\)", RegexOptions.Compiled);

  /// <summary>A file's bytes from the blocks reiser4progs' own lookup names for it.</summary>
  private static byte[] ReadThroughProgs(string path, string member) {
    var run = FsInteropToolbox.RunWsl($"debugfs.reiser4 -i '/{member}' {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"debugfs.reiser4 -i /{member} failed:\n{run.StdOut}\n{run.StdErr}");
    var size = long.Parse(Regex.Match(run.StdOut, @"^size:\s+(\d+)", RegexOptions.Multiline).Groups[1].Value, CultureInfo.InvariantCulture);
    var image = File.ReadAllBytes(path);
    using var bytes = new MemoryStream();
    foreach (var line in run.StdOut.Split('\n').Where(static l => l.StartsWith("UNITS=", StringComparison.Ordinal)))
      foreach (Match unit in Run.Matches(line[line.IndexOf('[')..])) {
        var start = long.Parse(unit.Groups[1].Value, CultureInfo.InvariantCulture);
        var width = long.Parse(unit.Groups[2].Value, CultureInfo.InvariantCulture);
        bytes.Write(image, checked((int)(start * Reiser4Writer.BlockSize)), checked((int)(width * Reiser4Writer.BlockSize)));
      }
    Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(size), $"the extents reiser4progs found for /{member} are shorter than its size");
    return bytes.ToArray()[..checked((int)size)];
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeMkfsWrote_WhenAddingNestedFilesLongNamesAndAnEmptyDirectory_ThenFsckPassesItsIdentityStaysAndTheProgsReadOurBytes() {
    RequireReiser4Progs();
    var original = this.Mkfs("cwbmkfs", "");
    var files = new Dictionary<string, byte[]> {
      ["top.bin"] = Payload(4_096, 1),
      ["nested/deeper/edge.bin"] = Payload(4_097, 2),
      ["nested/a-name-longer-than-twenty-three.txt"] = Payload(4_095, 3),
      ["nested/" + new string('z', 255)] = Payload(1, 4),
    };
    using var image = new MemoryStream();
    image.Write(original);
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [.. files.Select(static f => ArchiveInputInfo.InMemory(f.Key, f.Value)),
      new ArchiveInputInfo("", "empty/inner", IsDirectory: true)]);
    var edited = this.Save("added.img", image.ToArray());
    var originalPath = this.Save("original.img", original);
    var before = Super(originalPath);
    var after = Super(edited);

    AssertFsckConsistent(edited);
    Assert.Multiple(() => {
      foreach (var field in new[] { "uuid", "label", "mkfs id", "tail policy", "key policy", "blksize" })
        Assert.That(after[field], Is.EqualTo(before[field]), field);
      foreach (var (name, data) in files)
        Assert.That(ReadThroughProgs(edited, name), Is.EqualTo(data), name);
      Assert.That(Listing(edited, "/empty/inner").Length, Is.EqualTo(2), "an empty directory holds only its dot entries");
      Assert.That(Ownership(edited, "/"), Is.EqualTo(Ownership(originalPath, "/")).And.Not.Empty, "the root directory mkfs wrote");
    });
  }

  [TestCase("-o hash=tea_hash", TestName = "GivenAVolumeMkfsWroteWithTheTeaHash_WhenAdding_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  [TestCase("-o formatting=tails", TestName = "GivenAVolumeMkfsWroteWithATailsOnlyPolicy_WhenAdding_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  [TestCase("-o fibration=lexic_fibre", TestName = "GivenAVolumeMkfsWroteWithLexicographicFibration_WhenAdding_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  public void VolumeWithAnotherPluginProfile_IsReadButNotRewritten(string options) {
    RequireReiser4Progs();
    var original = this.Mkfs("cwbprof", options);
    using var image = new MemoryStream();
    image.Write(original);
    using (var reader = new Reiser4Reader(image))
      Assert.That(reader.NativeTreeValid, Is.True, "an empty volume of another profile is still read");
    var error = Assert.Throws<NotSupportedException>(() => new Reiser4FormatDescriptor().Add(image,
      [ArchiveInputInfo.InMemory("a-name-longer-than-twenty-three.txt", Payload(100, 5))]));
    Assert.That(error!.Message, Does.Contain("refusing"));
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Boundary")]
  public void GivenTwelveThousandFiles_WhenWriting_ThenFsckAcceptsAFourLevelTreeAndTheProgsReadTheBoundaryFiles() {
    RequireReiser4Progs();
    var sizes = new Dictionary<string, int> {
      ["t/f000000"] = 1, ["t/f000001"] = 4_095, ["t/f000002"] = 4_096, ["t/f000003"] = 4_097, ["t/f011999"] = 2_000_000,
    };
    var writer = new Reiser4Writer();
    for (var i = 0; i < 12_000; ++i) {
      var name = $"t/f{i:D6}";
      writer.AddFile(name, Payload(sizes.GetValueOrDefault(name, 1 + i % 5_000), i));
    }
    var path = this.Save("tall.img", writer.Build());

    AssertFsckConsistent(path);
    var super = Super(path);
    Assert.Multiple(() => {
      Assert.That(super["tree height"], Is.EqualTo("4"));
      Assert.That(super["file count"], Is.EqualTo("12002"), "the files, t and the root");
      foreach (var (name, size) in sizes)
        Assert.That(ReadThroughProgs(path, name), Is.EqualTo(Payload(size, int.Parse(name[3..], CultureInfo.InvariantCulture))), name);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeFsckRebuiltWithALostAndFound_WhenReadingAndAddingIntoIt_ThenItsObjectIdAndOwnerSurvive() {
    RequireReiser4Progs();
    var writer = new Reiser4Writer();
    writer.AddFile("d/keep.bin", Payload(9_000, 6));
    var path = this.Save("rebuilt.img", writer.Build());
    var rebuild = FsInteropToolbox.RunWsl($"fsck.reiser4 --build-fs -y {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.That(rebuild.ExitCode, Is.EqualTo(0), $"fsck.reiser4 --build-fs failed:\n{rebuild.StdOut}\n{rebuild.StdErr}");
    var lostBefore = Listing(path, "/").Single(static l => l.EndsWith(" lost+found", StringComparison.Ordinal));
    var ownerBefore = Ownership(path, "/lost+found");

    using var image = new MemoryStream();
    image.Write(File.ReadAllBytes(path));
    using (var reader = new Reiser4Reader(image)) {
      var lost = reader.Entries.Single(static e => e.Name == "lost+found");
      Assert.Multiple(() => {
        Assert.That(reader.NativeTreeValid, Is.True);
        Assert.That(lost.IsDirectory, Is.True);
        Assert.That(lostBefore, Does.Contain($":{lost.Metadata!.ObjectId:x}:"), "our reader and debugfs agree on the object id");
        Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "d/keep.bin")), Is.EqualTo(Payload(9_000, 6)));
      });
    }
    new Reiser4FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("lost+found/found.bin", Payload(5_000, 7))]);
    var edited = this.Save("rebuilt-added.img", image.ToArray());

    AssertFsckConsistent(edited);
    Assert.Multiple(() => {
      Assert.That(Listing(edited, "/").Single(static l => l.EndsWith(" lost+found", StringComparison.Ordinal)), Is.EqualTo(lostBefore));
      Assert.That(Ownership(edited, "/lost+found"), Is.EqualTo(ownerBefore).And.Not.Empty, "fsck made lost+found with its own owner and times");
      Assert.That(ReadThroughProgs(edited, "lost+found/found.bin"), Is.EqualTo(Payload(5_000, 7)));
      Assert.That(ReadThroughProgs(edited, "d/keep.bin"), Is.EqualTo(Payload(9_000, 6)));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenALargeTimeStatExtension_WhenAddingAndDefragmenting_ThenFsckAcceptsItAndDebugfsStillShowsIt() {
    RequireReiser4Progs();
    var plain = new Reiser4Writer();
    plain.AddFile("timed.bin", Payload(6_000, 8));
    Reiser4Reader.FileMetadata metadata;
    using (var reader = new Reiser4Reader(new MemoryStream(plain.Build())))
      metadata = reader.Entries.Single().Metadata!;
    // Mask bit 2 is the large-time extension: three 32-bit nanosecond fields after the Unix ones.
    var raw = new byte[metadata.RawStatData.Length + 12];
    metadata.RawStatData.CopyTo(raw, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(raw, 0x7);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44), 111_111_111);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(48), 222_222_222);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(52), 333_333_333);
    var writer = new Reiser4Writer();
    writer.AddFile("timed.bin", Payload(6_000, 8), metadata with { RawStatData = raw });
    using var image = new MemoryStream(writer.Build());
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("other.bin", Payload(7_000, 9))]);
    descriptor.Defragment(image);
    var path = this.Save("timed.img", image.ToArray());

    AssertFsckConsistent(path);
    var info = FsInteropToolbox.RunWsl($"debugfs.reiser4 -i /timed.bin {FsInteropToolbox.WinToWsl(path)} </dev/null");
    Assert.Multiple(() => {
      Assert.That(info.StdOut, Does.Contain("sdext_lt"), info.StdOut);
      Assert.That(ReadThroughProgs(path, "timed.bin"), Is.EqualTo(Payload(6_000, 8)));
    });
    using var result = new Reiser4Reader(image);
    Assert.That(result.Entries.Single(static e => e.Name == "timed.bin").Metadata!.RawStatData, Is.EqualTo(raw));
  }
}
