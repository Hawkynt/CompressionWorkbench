#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Third-party proof for in-place editing inside ISO 9660 folders. Images we made and images
/// xorriso made are edited by <see cref="IsoFormatDescriptor"/> (files added into new and
/// existing folders, a folder grown past its sector, a file and a folder removed) and then:
/// the Linux kernel's isofs mounts them three ways — Rock Ridge, Joliet (<c>norock</c>) and
/// the bare ISO 9660 tree (<c>norock,nojoliet</c>) — and every tree must hold exactly the
/// expected files with the expected bytes; xorriso loads them without complaint and extracts
/// the same tree; <c>isoinfo</c> finds the new folders in both path tables.
/// </summary>
/// <remarks>Skips (never fails) when WSL, the tools or a working libguestfs appliance are missing.</remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
[Category("Slow")]
public sealed class IsoFolderEditOracleTests {

  private static readonly IsoFormatDescriptor Iso = new();
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_isoedit_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    for (var i = 0; i < size; ++i) d[i] = (byte)(i * 29 + seed + (i >> 11));
    return d;
  }

  /// <summary>The seed tree both kinds of image start from.</summary>
  private static Dictionary<string, byte[]> Seed() {
    var seed = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["README.TXT"] = "edited in place\n"u8.ToArray(),
      ["docs/guide.txt"] = Pattern(5000, 1),
      ["docs/api/reference.bin"] = Pattern(70_000, 2),
      ["old/inner/gone.bin"] = Pattern(9000, 3),
      ["old/gone.txt"] = "gone\n"u8.ToArray(),
    };
    for (var i = 0; i < 20; ++i) seed[$"big/file{i:D2}.dat"] = Encoding.ASCII.GetBytes($"content {i}\n");
    return seed;
  }

  /// <summary>Applies the edits to the image and to the expected tree.</summary>
  private static void Edit(string image, Dictionary<string, byte[]> expected) {
    using var fs = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
    var adds = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["docs/New Folder/Mixed Case Report.txt"] = Pattern(3000, 4),
      ["docs/api/added.bin"] = Pattern(40_000, 5),
      ["deep/l2/l3/l4/leaf.txt"] = "leaf\n"u8.ToArray(),
    };
    // 70 long names push "big" past one sector in both trees.
    for (var i = 0; i < 70; ++i) adds[$"big/A rather long added name {i:D3}.txt"] = Encoding.ASCII.GetBytes($"added {i}\n");
    Iso.Add(fs, [.. adds.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))]);
    Iso.Remove(fs, ["old", "docs/guide.txt"]);

    foreach (var (k, v) in adds) expected[k] = v;
    foreach (var gone in expected.Keys.Where(k => k.StartsWith("old/", StringComparison.Ordinal) || k == "docs/guide.txt").ToList())
      expected.Remove(gone);
  }

  [Test]
  public void GivenOurImage_WhenEditedInFolders_ThenIsofsXorrisoAndIsoinfoAgree() {
    RequireTools();
    var expected = Seed();
    var image = Path.Combine(this._dir, "ours.iso");
    using (var fs = File.Create(image))
      Iso.Create(fs, [.. expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], new FormatCreateOptions());

    Edit(image, expected);

    this.AssertOracles(image, expected);
  }

  [Test]
  public void GivenAnImageXorrisoMade_WhenEditedInFolders_ThenIsofsXorrisoAndIsoinfoAgree() {
    RequireTools();
    var expected = Seed();
    var src = Path.Combine(this._dir, "src");
    foreach (var (name, data) in expected) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    var image = Path.Combine(this._dir, "xorriso.iso");
    var made = FsInteropToolbox.RunWsl($"xorriso -as mkisofs -R -J -V SEED -o {Q(image)} {Q(src)} 2>&1");
    Assert.That(made.ExitCode, Is.Zero, made.StdOut + made.StdErr);

    Edit(image, expected);

    this.AssertOracles(image, expected);
  }

  private void AssertOracles(string image, Dictionary<string, byte[]> expected) {
    // Kernel isofs, three views of the same volume.
    var trees = new[] { ("rr", "ro"), ("joliet", "ro,norock"), ("primary", "ro,norock,nojoliet") };
    var script = new List<string> { "run" };
    foreach (var (view, options) in trees)
      script.AddRange([$"mount-vfs {options} iso9660 /dev/sda /", $"tar-out / {U(Path.Combine(this._dir, view + ".tar"))}", "umount /"]);
    var scriptPath = Path.Combine(this._dir, "gf.txt");
    File.WriteAllText(scriptPath, string.Join('\n', script) + "\n");
    var unpack = string.Join(" && ", trees.Select(t =>
      $"mkdir -p {Q(Path.Combine(this._dir, t.Item1))} && tar -xf {Q(Path.Combine(this._dir, t.Item1 + ".tar"))} -C {Q(Path.Combine(this._dir, t.Item1))}"));
    var kernel = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 900 guestfish -a {U(image)} -f {U(scriptPath)} 2>&1 && {unpack}");
    Assert.That(kernel.ExitCode, Is.Zero, $"isofs through guestfish:\n{kernel.StdOut}{kernel.StdErr}");

    Assert.Multiple(() => {
      AssertTree(Path.Combine(this._dir, "rr"), expected, exactNames: true, "isofs, Rock Ridge");
      AssertTree(Path.Combine(this._dir, "joliet"), expected, exactNames: true, "isofs, Joliet");
      AssertTree(Path.Combine(this._dir, "primary"), expected, exactNames: false, "isofs, ISO 9660 names");
    });

    // xorriso: loads the tree without a complaint and extracts the same files.
    var xout = Path.Combine(this._dir, "xorriso");
    var x = FsInteropToolbox.RunWsl($"xorriso -abort_on FAILURE -return_with SORRY 32 -osirrox on -indev {Q(image)} -extract / {Q(xout)} 2>&1");
    Assert.That(x.ExitCode, Is.Zero, x.StdOut + x.StdErr);
    Assert.That(x.StdOut + x.StdErr, Does.Not.Contain("SORRY").And.Not.Contain("FAILURE"));
    AssertTree(xout, expected, exactNames: true, "xorriso");

    // isoinfo: the descriptors are sound and both path tables carry the new folders.
    var d = FsInteropToolbox.RunWsl($"isoinfo -d -i {Q(image)} 2>&1");
    Assert.That(d.StdOut.ToLowerInvariant(), Does.Not.Contain("damaged").And.Contain("rock ridge"), d.StdOut);
    var primaryTable = FsInteropToolbox.RunWsl($"isoinfo -p -i {Q(image)} 2>&1");
    var jolietTable = FsInteropToolbox.RunWsl($"isoinfo -J -p -i {Q(image)} 2>&1");
    var listing = FsInteropToolbox.RunWsl($"isoinfo -R -l -i {Q(image)} 2>&1");
    Assert.Multiple(() => {
      Assert.That(primaryTable.StdOut, Does.Contain("L4").And.Contain("NEW_FOLDER").And.Not.Contain("INNER"), primaryTable.StdOut);
      Assert.That(jolietTable.StdOut, Does.Contain("New Folder").And.Contain("l4").And.Not.Contain("inner"), jolietTable.StdOut);
      Assert.That(listing.StdOut, Does.Contain("Mixed Case Report.txt").And.Contain("A rather long added name 069.txt"), listing.StdOut);
      Assert.That(listing.StdOut, Does.Not.Contain("gone.bin").And.Not.Contain("guide.txt"), listing.StdOut);
    });
  }

  /// <summary>
  /// Exactly the expected files with the expected bytes. Where the view shows ISO 9660
  /// identifiers rather than the real names, the files are matched by content.
  /// </summary>
  private static void AssertTree(string root, Dictionary<string, byte[]> expected, bool exactNames, string what) {
    var got = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    if (exactNames) {
      Assert.That(got.Keys, Is.EquivalentTo(expected.Keys), $"{what}: names");
      foreach (var (name, data) in expected)
        if (got.TryGetValue(name, out var bytes))
          Assert.That(bytes, Is.EqualTo(data), $"{what}: {name}");
    } else {
      static string Key(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
      Assert.That(got.Values.Select(Key), Is.EquivalentTo(expected.Values.Select(Key)), $"{what}: contents");
    }
  }

  private static string Q(string path) => FsInteropToolbox.WinToWsl(path);

  private static string U(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static bool? _capable;

  private static void RequireTools() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL not available.");
    foreach (var tool in new[] { "guestfish", "xorriso", "isoinfo" })
      if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"'{tool}' not available in WSL.");
    _capable ??= FsInteropToolbox.RunWsl(ApplianceEnv + " d=$(mktemp -d); truncate -s 1M $d/p.img; " +
      "printf 'run\\nmkfs ext2 /dev/sda\\nmount /dev/sda /\\nls /\\numount /\\n' | timeout 600 guestfish -a $d/p.img 2>&1; rm -rf $d")
      .StdOut.Contains("lost+found", StringComparison.Ordinal);
    if (_capable != true) Assert.Ignore("the libguestfs appliance does not start here (no kernel image or no KVM).");
  }
}
