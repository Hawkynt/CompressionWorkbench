using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Encrypted volumes this package writes: read back here; checked by <c>bcachefs fsck</c> (the
/// distribution's 1.3.4 and, where installed, v1.39.6) with the passphrase on standard input;
/// and mounted by the 6.8 kernel (key stored in the clear, since the appliance cannot be handed a
/// passphrase), which must show the same files and write into the volume in turn.
/// </summary>
[TestFixture]
public sealed class BcacheFsEncryptedWriterTests {
  private const string Passphrase = "Secr3t pass";
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_enc_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 13 + seed * 7 + i / 331);
    return result;
  }

  private static readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal) {
    ["hello.txt"] = Encoding.ASCII.GetBytes("enciphered by CompressionWorkbench\n"),
    ["empty.bin"] = [],
    ["dir/odd.bin"] = Payload(4_097, 1),
    ["dir/deeper/big.bin"] = Payload(300_123, 2),
  };

  private static BcacheFsWriter Writer(string? passphrase, bool wideMacs = false) {
    var writer = new BcacheFsWriter();
    writer.SetLabel("cwb-encrypted");
    writer.SetEncryption(passphrase, wideMacs);
    foreach (var (name, data) in Files) writer.AddFile(name, data);
    writer.AddSymlink("link", "dir/odd.bin");
    writer.AddDirectory("empty-dir");
    return writer;
  }

  private string Write(string name, string? passphrase, bool wideMacs = false) {
    var path = Path.Combine(this._tmpDir, name);
    using var stream = File.Create(path);
    Writer(passphrase, wideMacs).WriteTo(stream);
    return path;
  }

  private static void AssertReadsBack(string path, string? passphrase) {
    using var stream = File.OpenRead(path);
    using var reader = new BcacheFsReader(stream, passphrase);
    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.Encrypted, Is.True);
      foreach (var (name, data) in Files) {
        var entry = reader.Entries.SingleOrDefault(e => e.Name == name);
        Assert.That(entry, Is.Not.Null, name);
        if (entry != null) Assert.That(reader.Read(entry), Is.EqualTo(data), name);
      }
      Assert.That(reader.Entries.Single(e => e.Name == "link").LinkTarget, Is.EqualTo("dir/odd.bin"));
      Assert.That(reader.Directories, Does.Contain("empty-dir"));
    });
  }

  [TestCase(Passphrase, false)]
  [TestCase(null, false)]
  [TestCase(Passphrase, true)]
  [Category("RoundTrip")]
  public void GivenOurEncryptedVolume_WhenReadBack_ThenEveryFileMatches(string? passphrase, bool wideMacs)
    => AssertReadsBack(this.Write("rt.img", passphrase, wideMacs), passphrase);

  [Test, Category("HappyPath")]
  public void GivenOurEncryptedVolume_WhenInspected_ThenNoFileOrNameIsStoredInTheClear() {
    var bytes = File.ReadAllBytes(this.Write("clear.img", Passphrase));
    Assert.Multiple(() => {
      Assert.That(bytes.AsSpan().IndexOf("enciphered by CompressionWorkbench"u8), Is.EqualTo(-1), "file bytes");
      Assert.That(bytes.AsSpan().IndexOf("deeper"u8), Is.EqualTo(-1), "directory names");
      Assert.That(bytes.AsSpan().IndexOf(Payload(4_097, 1).AsSpan(100, 64)), Is.EqualTo(-1), "extent data");
    });
  }

  [Test, Category("Exception")]
  public void GivenOurPassphraseVolume_WhenOpenedWithAnotherPassphrase_ThenItIsRefused() {
    using var stream = File.OpenRead(this.Write("wrong.img", Passphrase));
    using var reader = new BcacheFsReader(stream, "Secr3t pasS");
    Assert.That(reader.NeedsPassphrase, Is.True);
  }

  [Test, Category("HappyPath")]
  public void GivenADescriptorCreateWithAPassword_WhenReadBack_ThenTheVolumeIsEncryptedUnderIt() {
    var descriptor = new BcacheFsFormatDescriptor();
    var path = Path.Combine(this._tmpDir, "descriptor.img");
    using (var stream = File.Create(path))
      descriptor.Create(stream, [ArchiveInputInfo.InMemory("a.txt", "secret text"u8.ToArray())],
        new FormatCreateOptions { Password = Passphrase });
    using var image = File.OpenRead(path);
    using var reader = new BcacheFsReader(image, Passphrase);
    Assert.Multiple(() => {
      Assert.That(reader.Valid && reader.Encrypted, Is.True, reader.Status);
      Assert.That(reader.Read(reader.Entries.Single(e => e.Name == "a.txt")), Is.EqualTo("secret text"u8.ToArray()));
    });
  }

  // ── The tools and the kernel as judges ────────────────────────────────────

  /// <summary>Every checker walks the whole volume, the passphrase on standard input, and finds nothing to repair.</summary>
  private static void AssertFsckClean(string path, string? passphrase) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var wsl = FsInteropToolbox.WinToWsl(path);
    var extra = Environment.GetEnvironmentVariable("CWB_BCACHEFS_EXTRA") ?? "";
    foreach (var checker in new[] { "bcachefs" }.Concat(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries))) {
      var input = passphrase == null ? "" : $"printf '%s\\n' '{passphrase}' | ";
      var result = FsInteropToolbox.RunWsl($"{input}{checker} fsck -n {wsl}");
      var output = result.StdOut + "\n" + result.StdErr;
      TestContext.Out.WriteLine($"$ {checker} fsck -n  (exit {result.ExitCode})\n{output}");
      Assert.That(result.ExitCode, Is.EqualTo(0), $"{checker} fsck rejected the volume:\n{output}");
      Assert.That(output, Does.Contain("check_nlinks"), $"{checker} fsck did not walk the whole volume:\n{output}");
      Assert.That(output, Does.Not.Contain("fixing"), $"{checker} fsck found something to repair:\n{output}");
    }
  }

  [TestCase(Passphrase, false)]
  [TestCase(null, false)]
  [TestCase(Passphrase, true)]
  [Category("ExternalFsInterop")]
  public void GivenOurEncryptedVolume_WhenBcachefsFsckChecksIt_ThenItIsClean(string? passphrase, bool wideMacs)
    => AssertFsckClean(this.Write("fsck.img", passphrase, wideMacs), passphrase);

  [Test, Category("ExternalFsInterop")]
  public void GivenOurPassphraseVolume_WhenTheToolsAreGivenTheWrongPassphrase_ThenTheyRefuseIt() {
    BcacheFsOracleGate.RequireBcachefsTools();
    var wsl = FsInteropToolbox.WinToWsl(this.Write("wrong-tools.img", Passphrase));
    var result = FsInteropToolbox.RunWsl($"printf 'not it\\n' | bcachefs unlock -k session {wsl}");
    Assert.That(result.ExitCode, Is.Not.EqualTo(0));
    Assert.That(result.StdOut + result.StdErr, Does.Contain("incorrect passphrase"));
    var right = FsInteropToolbox.RunWsl($"printf '%s\\n' '{Passphrase}' | bcachefs unlock -c {wsl}");
    Assert.That(right.StdOut + right.StdErr, Does.Not.Contain("not encrypted"));
  }

  private const string ListingScript = """
    cd /sysroot
    find . -mindepth 1 ! -path './lost+found' ! -path './lost+found/*' | LC_ALL=C sort | while IFS= read -r p; do
      if [ -L "$p" ]; then c="L:$(readlink "$p")"; elif [ -f "$p" ]; then c=$(sha256sum < "$p" | cut -c1-64); else c=D; fi
      printf 'E|%s|%s\n' "${p#./}" "$c"
    done
    """;

  private static Dictionary<string, string> Listing(string output) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var raw in output.Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (!line.StartsWith("E|", StringComparison.Ordinal)) continue;
      var f = line.Split('|');
      result[f[1]] = f[2];
    }
    return result;
  }

  private static Dictionary<string, string> Ours(string path, string? passphrase) {
    using var stream = File.OpenRead(path);
    using var reader = new BcacheFsReader(stream, passphrase);
    Assert.That(reader.Valid, Is.True, reader.Status);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var d in reader.Directories.Where(d => d != "lost+found" && !d.StartsWith("lost+found/", StringComparison.Ordinal)))
      result[d] = "D";
    foreach (var e in reader.Entries)
      result[e.Name] = e.LinkTarget is { } t ? "L:" + t : Convert.ToHexStringLower(SHA256.HashData(reader.Read(e)));
    return result;
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("ExternalFsInterop")]
  public void GivenOurEncryptedVolume_WhenTheKernelMountsIt_ThenItSeesOurFilesAndWhatItWritesReadsBackHere(bool wideMacs) {
    BcacheFsKernel.Require();
    var path = this.Write("kernel.img", null, wideMacs);
    var expected = Ours(path, null);

    var seen = Listing(BcacheFsKernel.Session(path, ListingScript));
    Assert.That(seen, Is.EquivalentTo(expected), "the kernel decrypts the tree and bytes we wrote");

    var after = Listing(BcacheFsKernel.Session(path, """
      set -e
      cd /sysroot
      mkdir -p kernel
      seq 1 40000 > kernel/seq.txt
      printf 'tiny' > kernel/tiny.txt
      rm dir/odd.bin
      printf 'appended by the kernel' >> hello.txt
      sync
      """ + "\n" + ListingScript));
    AssertFsckClean(path, null);
    Assert.That(Ours(path, null), Is.EquivalentTo(after), "our reader decrypts what the kernel wrote");
  }
}
