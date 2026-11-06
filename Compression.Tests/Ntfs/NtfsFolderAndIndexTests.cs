using Compression.Lib;

namespace Compression.Tests.Ntfs;

/// <summary>
/// NTFS images keep folders (new images flattened every name to its leaf), every INDX block
/// has the 4 KiB size the boot sector announces (a long name or a big folder used to give a
/// folder 64 KiB blocks, which Linux's ntfs3 cannot read), index keys carry the folder flag,
/// times and sizes of their records, and $LogFile is big enough for ntfs3 to mount read-write.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 4e74b13, f88a20e, 458c95e, f4f771c, 19a944c, 23ee0d2.</remarks>
[TestFixture]
public sealed class NtfsFolderAndIndexTests {
  private const uint DirectoryFlag = 0x10000000;
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_ntfsfold_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static List<string> Names(string image)
    => [.. ArchiveOperations.List(image, null).Select(e => e.Name.Replace('\\', '/').Trim('/'))];

  /// <summary>The block sizes (from the update-sequence count) of every INDX block in the image.</summary>
  private static HashSet<int> IndxBlockSizes(string image) {
    var bytes = File.ReadAllBytes(image);
    var sizes = new HashSet<int>();
    for (var off = 0; off + 8 <= bytes.Length; off += 512)
      if (bytes[off] == 'I' && bytes[off + 1] == 'N' && bytes[off + 2] == 'D' && bytes[off + 3] == 'X')
        sizes.Add((BitConverter.ToUInt16(bytes, off + 6) - 1) * 512);
    return sizes;
  }

  /// <summary>The file-attribute flags of every $FILE_NAME of <paramref name="name"/>: in its record and in index keys.</summary>
  private static List<uint> FileNameFlags(string image, string name) {
    var bytes = File.ReadAllBytes(image);
    var utf16 = System.Text.Encoding.Unicode.GetBytes(name);
    var flags = new List<uint>();
    for (var i = 66; i + 2 + utf16.Length <= bytes.Length; i++) {
      if (bytes[i] != name.Length || !bytes.AsSpan(i + 2, utf16.Length).SequenceEqual(utf16)) continue;
      flags.Add(BitConverter.ToUInt32(bytes, i - 8)); // $FILE_NAME: flags @56, name length @64
    }
    return flags;
  }

  private string BuildEditedImage() {
    Directory.CreateDirectory(Path.Combine(this._dir, "pack", "sub", "empty"));
    File.WriteAllText(Path.Combine(this._dir, "pack", "sub", "Long Name.txt"), "deep");
    File.WriteAllBytes(Path.Combine(this._dir, "pack", "big.bin"), [.. Enumerable.Range(0, 20000).Select(i => (byte)i)]);
    File.WriteAllText(Path.Combine(this._dir, "top.txt"), "top");
    var image = Path.Combine(this._dir, "n.ntfs");
    ArchiveOperations.Create(image, [
      .. ArchiveInput.Resolve([Path.Combine(this._dir, "pack")]),
      new ArchiveInput(Path.Combine(this._dir, "top.txt"), "top.txt"),
    ], new CompressionOptions(), FormatDetector.Format.Ntfs);
    ArchiveOperations.Add(image, [new ArchiveInput("", "later/deeper/")]);
    ArchiveOperations.Add(image, [new ArchiveInput(Path.Combine(this._dir, "top.txt"), "pack/sub/added.txt")]);
    ArchiveOperations.Add(image, [new ArchiveInput(Path.Combine(this._dir, "top.txt"), "root2.txt")]);
    ArchiveOperations.Remove(image, ["top.txt"]);
    ArchiveOperations.Remove(image, ["pack/sub/empty"]);
    return image;
  }

  [Test, Category("HappyPath")]
  public void GivenFolders_WhenCreatedAddedAndRemoved_ThenTheTreeIsKept() {
    var image = this.BuildEditedImage();

    var names = Names(image);
    Assert.Multiple(() => {
      Assert.That(names, Is.SupersetOf(new[] { "pack", "pack/sub", "pack/sub/Long Name.txt", "pack/sub/added.txt", "pack/big.bin", "later", "later/deeper", "root2.txt" }));
      Assert.That(names, Does.Not.Contain("top.txt").And.Not.Contain("pack/sub/empty"));
    });
    var outDir = Path.Combine(this._dir, "x");
    ArchiveOperations.Extract(image, outDir, null, null);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pack", "sub", "Long Name.txt")), Is.EqualTo("deep"));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "pack", "big.bin")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._dir, "pack", "big.bin"))));
      Assert.That(Directory.Exists(Path.Combine(outDir, "later", "deeper")), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenRemoved_ThenItsContentsGoWithIt() {
    var image = this.BuildEditedImage();

    ArchiveOperations.Remove(image, ["pack"]);

    var names = Names(image);
    Assert.Multiple(() => {
      Assert.That(names.Where(n => n == "pack" || n.StartsWith("pack/", StringComparison.Ordinal)), Is.Empty);
      Assert.That(names, Does.Contain("root2.txt"));
    });
  }

  private string BuildBigFolders(IReadOnlyDictionary<string, string>? formatSpecific = null) {
    var src = Path.Combine(this._dir, "src");
    var many = Directory.CreateDirectory(Path.Combine(src, "long names")).FullName;
    for (var i = 0; i < 3000; i++)
      File.WriteAllText(Path.Combine(many, $"{i:D5} {new string('x', 194)}"), i.ToString());
    File.WriteAllText(Path.Combine(src, new string('n', 200) + ".txt"), "long");
    for (var i = 0; i < 8; i++)
      File.WriteAllText(Path.Combine(src, $"small{i}.txt"), $"small {i}");
    var image = Path.Combine(this._dir, "big.ntfs");
    ArchiveOperations.Create(image, ArchiveInput.Resolve([src]), new CompressionOptions(), FormatDetector.Format.Ntfs, formatSpecific);
    return image;
  }

  [Test, Category("Boundary")]
  public void GivenAFolderOfThousandsOfLongNames_WhenCreated_ThenEveryIndexBlockIs4KiBAndEveryFileIsListed() {
    var image = this.BuildBigFolders();

    var files = ArchiveOperations.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name.Replace('\\', '/').Trim('/')).ToList();
    Assert.Multiple(() => {
      Assert.That(IndxBlockSizes(image), Is.EquivalentTo(new[] { 4096 }));
      Assert.That(FileNameFlags(image, "long names"), Has.Count.GreaterThanOrEqualTo(2).And.All.EqualTo(DirectoryFlag));
      Assert.That(files.Count(n => n.StartsWith("src/long names/", StringComparison.Ordinal)), Is.EqualTo(3000));
      Assert.That(files, Does.Contain("src/" + new string('n', 200) + ".txt"));
    });
    var outDir = Path.Combine(this._dir, "x");
    ArchiveOperations.Extract(image, outDir, null, null);
    Assert.That(File.ReadAllText(Path.Combine(outDir, "src", "long names", $"02999 {new string('x', 194)}")), Is.EqualTo("2999"));
  }

  [Test, Category("HappyPath")]
  public void GivenANewImage_WhenItsLogFileIsMeasured_ThenItHoldsTwoRestartPagesAndFortyEightRecordPages() {
    var file = Path.Combine(this._dir, "a.txt");
    File.WriteAllText(file, "a");
    var image = Path.Combine(this._dir, "j.ntfs");
    ArchiveOperations.Create(image, [new ArchiveInput(file, "a.txt")], new CompressionOptions(), FormatDetector.Format.Ntfs);

    int longest = 0, run = 0;
    foreach (var b in File.ReadAllBytes(image)) {
      run = b == 0xFF ? run + 1 : 0;
      longest = Math.Max(longest, run);
    }
    Assert.That(longest, Is.GreaterThanOrEqualTo(50 * 4096), "an unwritten $LogFile is all 0xFF");
  }

  [Test, Category("Boundary")]
  public void GivenTheRootGrownPastOneIndexBlockInPlace_WhenMostFilesAreRemovedAgain_ThenBlocksStay4KiBAndNothingIsLost() {
    var first = Path.Combine(this._dir, "first.txt");
    File.WriteAllText(first, "first");
    Directory.CreateDirectory(Path.Combine(this._dir, "kept folder"));
    var image = Path.Combine(this._dir, "root.ntfs");
    ArchiveOperations.Create(image, [new ArchiveInput(first, "first.txt"), new ArchiveInput("", "kept folder/")], new CompressionOptions(), FormatDetector.Format.Ntfs);
    static string Name(int i) => $"{i:D3} {new string('r', 150)}.txt";
    for (var i = 0; i < 40; i++) {
      var f = Path.Combine(this._dir, "f.txt");
      File.WriteAllText(f, $"added {i}");
      ArchiveOperations.Add(image, [new ArchiveInput(f, Name(i))]);
    }

    Assert.Multiple(() => {
      Assert.That(IndxBlockSizes(image), Is.EquivalentTo(new[] { 4096 }));
      Assert.That(FileNameFlags(image, "kept folder"), Has.Count.GreaterThanOrEqualTo(2).And.All.EqualTo(DirectoryFlag));
      Assert.That(Names(image), Is.SupersetOf(Enumerable.Range(0, 40).Select(Name).Append("first.txt")));
    });

    ArchiveOperations.Remove(image, [.. Enumerable.Range(0, 30).Select(Name)]);

    var names = Names(image);
    Assert.Multiple(() => {
      Assert.That(IndxBlockSizes(image), Is.SubsetOf(new[] { 4096 }));
      Assert.That(names, Is.SupersetOf(Enumerable.Range(30, 10).Select(Name).Append("first.txt")));
      Assert.That(names.Intersect(Enumerable.Range(0, 30).Select(Name)), Is.Empty);
      Assert.That(FileNameFlags(image, "kept folder"), Has.Count.GreaterThanOrEqualTo(2).And.All.EqualTo(DirectoryFlag));
    });
    var outDir = Path.Combine(this._dir, "x");
    ArchiveOperations.Extract(image, outDir, null, null);
    Assert.That(File.ReadAllText(Path.Combine(outDir, Name(39))), Is.EqualTo("added 39"));
  }

  // ── oracles ──────────────────────────────────────────────────────────────────────────────

  private static void RequireWslTool(string tool) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} in WSL is needed for this oracle.");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenOurEditedAndBigImages_WhenNtfsfixAndNtfslsReadThem_ThenTheyAreCleanAndListTheFolders() {
    RequireWslTool("ntfsfix");
    RequireWslTool("ntfsls");
    var edited = this.BuildEditedImage();
    var big = this.BuildBigFolders();

    var fixEdited = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(edited)}");
    var fixBig = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(big)}");
    var lsLater = FsInteropToolbox.RunWsl($"ntfsls -l -p /later {FsInteropToolbox.WinToWsl(edited)}");
    var lsMany = FsInteropToolbox.RunWsl($"ntfsls -p '/src/long names' {FsInteropToolbox.WinToWsl(big)} | grep -c 'xxxxxxxxxx$'");

    Assert.Multiple(() => {
      Assert.That(fixEdited.ExitCode, Is.Zero, fixEdited.StdOut + fixEdited.StdErr);
      Assert.That(fixBig.ExitCode, Is.Zero, fixBig.StdOut + fixBig.StdErr);
      Assert.That(lsLater.StdOut, Does.Contain("deeper"));
      Assert.That(lsMany.StdOut.Trim(), Is.EqualTo("3000"));
    });
  }

  private static string ApplianceEnv {
    get {
      var password = Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";
      return "export LIBGUESTFS_BACKEND=direct; " +
        $"echo {password} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
        $"echo {password} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";
    }
  }

  private (int ExitCode, string Output) Guestfish(string image, params string[] commands) {
    var script = Path.Combine(this._dir, $"gf_{Guid.NewGuid():N}.txt");
    File.WriteAllText(script, string.Join('\n', ["run", .. commands]) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {FsInteropToolbox.WinToWsl(image).Trim('\'')} -f {FsInteropToolbox.WinToWsl(script).Trim('\'')} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static bool? _ntfs3Capable;

  /// <summary>Skips unless the libguestfs appliance mounts a volume mkntfs made with ntfs3.</summary>
  private void RequireNtfs3() {
    RequireWslTool("guestfish");
    RequireWslTool("mkntfs");
    _ntfs3Capable ??= ProbeNtfs3();
    if (_ntfs3Capable != true)
      Assert.Ignore("the libguestfs appliance cannot mount an mkntfs volume with ntfs3 here (no module, no kernel image or no KVM).");
  }

  private bool ProbeNtfs3() {
    var probe = Path.Combine(this._dir, "probe.ntfs");
    var made = FsInteropToolbox.RunWsl($"truncate -s 64M {FsInteropToolbox.WinToWsl(probe)} && mkntfs -Q -F {FsInteropToolbox.WinToWsl(probe)} >/dev/null 2>&1");
    if (made.ExitCode != 0) return false;
    var (exitCode, _) = this.Guestfish(probe, "mount-vfs \"\" ntfs3 /dev/sda /", "ls /", "umount /");
    return exitCode == 0;
  }

  [Test, Category("KernelMount")]
  public void GivenARootGrownInPlaceIntoAMultiLevelIndex_WhenNtfs3AndNtfsfixReadIt_ThenEveryFileIsThere() {
    this.RequireNtfs3();
    var first = Path.Combine(this._dir, "first.txt");
    File.WriteAllText(first, "first");
    var image = Path.Combine(this._dir, "root.ntfs");
    ArchiveOperations.Create(image, [new ArchiveInput(first, "first.txt")], new CompressionOptions(), FormatDetector.Format.Ntfs,
      new Dictionary<string, string> { ["ImageSize"] = "16 MB" });
    static string Name(int i) => $"{i:D3} {new string('r', 150)}.txt";
    for (var i = 0; i < 60; i++) {
      var f = Path.Combine(this._dir, "f.txt");
      File.WriteAllText(f, $"added {i}");
      ArchiveOperations.Add(image, [new ArchiveInput(f, Name(i))]);
    }
    ArchiveOperations.Remove(image, [.. Enumerable.Range(0, 60).Where(i => i % 3 == 0).Select(Name)]);
    var expected = Enumerable.Range(0, 60).Where(i => i % 3 != 0).Select(Name).Append("first.txt").ToList();

    var (exitCode, output) = this.Guestfish(image, "mount-vfs \"ro\" ntfs3 /dev/sda /", "ls /", $"cat \"/{Name(59)}\"", "umount /");
    var fix = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(image)}");

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, output);
      Assert.That(output.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(".txt", StringComparison.Ordinal)), Is.EquivalentTo(expected));
      Assert.That(output, Does.Contain("added 59"));
      Assert.That(fix.ExitCode, Is.Zero, fix.StdOut + fix.StdErr);
    });
  }

  [Test, Category("KernelMount")]
  public void GivenOurImage_WhenLinuxNtfs3MountsItReadWrite_ThenItListsEverythingAndTakesAWrite() {
    this.RequireNtfs3();
    // A volume with room to spare: an auto-sized one is laid out to fit, and the kernel
    // then has no free MFT record or cluster to write into.
    var image = this.BuildBigFolders(new Dictionary<string, string> { ["ImageSize"] = "16 MB" });

    var (exitCode, output) = this.Guestfish(image,
      "mount-vfs \"\" ntfs3 /dev/sda /",
      "is-dir \"/src/long names\"",
      "ls \"/src/long names\"",
      "cat /src/small7.txt",
      "write /kernel.txt \"from the kernel\"",
      "umount /");
    if (exitCode != 0)
      output += "\n--- kernel log ---\n" + this.Guestfish(image, "-mount-vfs \"\" ntfs3 /dev/sda /", "dmesg").Output;

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, output);
      Assert.That(output, Does.Contain("true"));
      Assert.That(output.Split('\n').Count(l => l.EndsWith(new string('x', 194), StringComparison.Ordinal)), Is.EqualTo(3000), "ntfs3 lists every file of the big folder");
      Assert.That(output, Does.Contain("small 7"));
    });
    Assert.That(ArchiveOperations.ExtractEntry(image, "kernel.txt", null), Is.EqualTo("from the kernel"u8.ToArray()), "what the kernel wrote reads back");
  }

  [TestCase(1)]
  [TestCase(3000)]
  [Category("KernelMount")]
  public void GivenAnAutoSizedImage_WhenNtfs3MountsItReadWrite_ThenItTakesNewFilesAndFolders(int files) {
    this.RequireNtfs3();
    var src = Directory.CreateDirectory(Path.Combine(this._dir, "auto")).FullName;
    for (var i = 0; i < files; i++)
      File.WriteAllText(Path.Combine(src, $"{i:D5}.txt"), i.ToString());
    var image = Path.Combine(this._dir, "auto.ntfs");
    ArchiveOperations.Create(image, ArchiveInput.Resolve([src]), new CompressionOptions(), FormatDetector.Format.Ntfs);

    var commands = new List<string> { "mount-vfs \"\" ntfs3 /dev/sda /", "statvfs /", "mkdir /newdir" };
    for (var i = 0; i < 40; i++) commands.Add($"write /newdir/k{i:D2}.txt \"kernel {i}\"");
    commands.Add("fallocate64 /newdir/big.bin 262144");
    commands.Add("umount /");
    var (exitCode, output) = this.Guestfish(image, [.. commands]);
    var fix = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(image)}");

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.Zero, output);
      Assert.That(fix.ExitCode, Is.Zero, fix.StdOut + fix.StdErr);
    });
    Assert.That(ArchiveOperations.ExtractEntry(image, "newdir/k39.txt", null), Is.EqualTo("kernel 39"u8.ToArray()));
  }
}
