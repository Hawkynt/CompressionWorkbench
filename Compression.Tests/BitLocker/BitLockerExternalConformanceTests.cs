using System.Globalization;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.BitLocker;

namespace Compression.Tests.BitLocker;

/// <summary>
/// cryptsetup as the oracle for the volumes we create: <c>bitlkDump</c> parses them, the user
/// password and the recovery password both open them, the volume key cryptsetup recovers is our
/// FVEK, and its device-mapper view is our input followed by zeros. Mapping needs the dm-crypt
/// and dm-zero targets; a host without dm-zero (a kernel whose modules are missing) still checks
/// every crypt segment through dm-crypt directly, and fails only when <c>CWB_REQUIRE_DM_ZERO=1</c>
/// demands the full mapping. Skipped unless cryptsetup is installed or
/// <c>CWB_REQUIRE_CRYPTSETUP=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class BitLockerExternalConformanceTests {
  private const string Password = "cwb oracle password";
  private const string Recovery = "462803-034177-217030-618585-386133-002101-470206-112178";

  private static void RequireCryptsetup() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("cryptsetup"))
      return;
    const string hint = "cryptsetup not installed: `sudo apt-get install -y cryptsetup-bin`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_CRYPTSETUP") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  private static string Sudo(string command) => $"echo 1234 | sudo -S -p '' {command}";

  private static bool HasDmZero() {
    Run(Sudo("modprobe dm-zero") + " 2>/dev/null; true");
    return Run(Sudo("dmsetup targets")).StdOut.Split('\n').Any(static l => l.StartsWith("zero", StringComparison.Ordinal));
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-bitlkx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private string KeyFile(string name, string content) {
    var p = Path.Combine(this._tmp, name);
    File.WriteAllText(p, content);
    return FsInteropToolbox.WinToWsl(p);
  }

  /// <summary>Opens <paramref name="image"/> with cryptsetup and runs <paramref name="action"/> on the mapping (MAPPER).</summary>
  private static (string Out, int Exit) ThroughMapping(string image, string keyFile, string action, bool readOnly = true) {
    var name = "cwb" + Guid.NewGuid().ToString("N")[..10];
    var r = Run(Sudo($"cryptsetup bitlkOpen {(readOnly ? "-r " : "")}--key-file {keyFile} {image} {name}") + " && " +
                Sudo($"sh -c '{action.Replace("MAPPER", "/dev/mapper/" + name)}'") + "; rc=$?; " +
                Sudo($"cryptsetup close {name}") + "; exit $rc");
    return (r.StdOut + r.StdErr, r.ExitCode);
  }

  private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase((ushort)0x8004)]
  [TestCase((ushort)0x8005)]
  public void GivenOurVolume_WhenCryptsetupOpensIt_ThenBothPasswordsWorkAndTheMappingIsOurPlaintext(ushort method) {
    RequireCryptsetup();
    var payload = BitLockerTests.Payload(2048, method);
    var image = Path.Combine(this._tmp, "ours.bitlk");
    BitLockerCreateResult result;
    using (var fs = File.Create(image))
      result = BitLockerWriter.Write(fs, payload, new BitLockerCreateOptions { Password = Password, RecoveryPassword = Recovery, EncryptionMethod = method });
    var wslImage = FsInteropToolbox.WinToWsl(image);
    var expected = new byte[result.VolumeSize];
    payload.CopyTo(expected, 0);

    var dump = Run($"cryptsetup bitlkDump {wslImage}");
    Assert.That(dump.ExitCode, Is.EqualTo(0), "bitlkDump refused our metadata");
    Assert.That(dump.StdOut, Does.Contain(result.VolumeGuid.ToString()).And.Contain("xts-plain64")
      .And.Contain("VMK protected with passphrase").And.Contain("VMK protected with recovery passphrase")
      .And.Contain(result.VolumeHeaderOffset.ToString(CultureInfo.InvariantCulture)));
    var pw = this.KeyFile("pw.txt", Password);
    var rp = this.KeyFile("rp.txt", Recovery);
    Assert.That(Run($"cryptsetup bitlkOpen --test-passphrase --key-file {pw} {wslImage}").ExitCode, Is.EqualTo(0), "cryptsetup does not accept our password protector");
    Assert.That(Run($"cryptsetup bitlkOpen --test-passphrase --key-file {rp} {wslImage}").ExitCode, Is.EqualTo(0), "cryptsetup does not accept our recovery-password protector");

    var vkFile = Path.Combine(this._tmp, "vk.bin");
    Assert.That(Run($"cryptsetup bitlkDump -q --dump-volume-key --key-file {pw} --volume-key-file {FsInteropToolbox.WinToWsl(vkFile)} {wslImage}").ExitCode, Is.EqualTo(0));
    byte[] fvek;
    using (var fs = File.OpenRead(image)) {
      var v = BitLockerVolume.Read(fs);
      fvek = BitLockerFormatDescriptor.Unlock(v, new(Password));
    }
    Assert.That(File.ReadAllBytes(vkFile), Is.EqualTo(fvek), "cryptsetup recovers a different volume key");

    if (!HasDmZero()) {
      this.CheckCryptSegmentsThroughDmCrypt(image, fvek, method, expected);
      const string hint = "the host kernel has no dm-zero target, so cryptsetup cannot map BITLK volumes here; every crypt segment was checked through dm-crypt instead.";
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_DM_ZERO") == "1") Assert.Fail(hint);
      Assert.Ignore(hint);
    }
    foreach (var key in new[] { pw, rp }) {
      var outFile = Path.Combine(this._tmp, "plain.bin");
      var (log, exit) = ThroughMapping(wslImage, key, $"cat MAPPER > {FsInteropToolbox.WinToWsl(outFile)}");
      Assert.That(exit, Is.EqualTo(0), log);
      Assert.That(Sha256Hex(File.ReadAllBytes(outFile)), Is.EqualTo(Sha256Hex(expected)));
    }
  }

  /// <summary>
  /// Without dm-zero, the crypt segments alone go through the kernel: each is mapped with
  /// dmsetup as cryptsetup would map it (cipher, key, IV offset = device offset) and compared
  /// with the expected plaintext of that range.
  /// </summary>
  private void CheckCryptSegmentsThroughDmCrypt(string image, byte[] fvek, ushort method, byte[] expected) {
    IReadOnlyList<BitLockerSegment> segments;
    using (var fs = File.OpenRead(image))
      segments = BitLockerLayout.Segments(BitLockerVolume.Read(fs), fs.Length);
    var wslImage = FsInteropToolbox.WinToWsl(image);
    var cipher = "aes-" + BitLockerVolume.ModeName(BitLockerVolume.Method(method)!.Value.Mode);
    foreach (var s in segments.Where(static s => !s.IsZero)) {
      var name = "cwb" + Guid.NewGuid().ToString("N")[..10];
      var outFile = Path.Combine(this._tmp, name + ".bin");
      var table = FsInteropToolbox.WinToWsl(Path.Combine(this._tmp, name + ".table"));
      var r = Run($"loop=$({Sudo($"losetup -f --show -r {wslImage}")}) && " +
                  $"echo \"0 {s.Length} crypt {cipher} {Convert.ToHexStringLower(fvek)} {s.IvOffset} $loop {s.IvOffset}\" > {table} && " +
                  Sudo($"dmsetup create --readonly {name} {table}") + " && " +
                  Sudo($"cat /dev/mapper/{name}") + $" > {FsInteropToolbox.WinToWsl(outFile)}; rc=$?; " +
                  Sudo($"dmsetup remove {name}") + "; " + Sudo("losetup -d $loop") + "; exit $rc");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
      var range = expected.AsSpan((int)(s.Offset * 512), (int)(s.Length * 512)).ToArray();
      Assert.That(Sha256Hex(File.ReadAllBytes(outFile)), Is.EqualTo(Sha256Hex(range)), $"dm-crypt disagrees on segment {s}");
    }
  }

  /// <summary>
  /// The kernel as the filesystem oracle through BitLocker: we encrypt a FAT image mkfs.vfat made,
  /// the kernel mounts it through cryptsetup's mapping and stores a file; we mount it with the
  /// password, read that file and write ours; the kernel reads ours back after fsck.vfat finds
  /// nothing to repair.
  /// </summary>
  [Test]
  public void GivenAKernelWrittenFilesystemInOurVolume_WhenWeMountItWritable_ThenBothSidesSeeEachOthersFiles() {
    RequireCryptsetup();
    if (!HasDmZero()) {
      const string hint = "the host kernel has no dm-zero target, so cryptsetup cannot map BITLK volumes here.";
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_DM_ZERO") == "1") Assert.Fail(hint);
      Assert.Ignore(hint);
    }
    var fat = Path.Combine(this._tmp, "fat.img");
    Assert.That(Run($"truncate -s 16M {FsInteropToolbox.WinToWsl(fat)} && mkfs.vfat {FsInteropToolbox.WinToWsl(fat)} >/dev/null").ExitCode, Is.EqualTo(0));
    var image = Path.Combine(this._tmp, "fs.bitlk");
    using (var fs = File.Create(image))
      BitLockerWriter.Write(fs, File.ReadAllBytes(fat), new BitLockerCreateOptions { Password = Password, RecoveryPassword = Recovery });
    var wslImage = FsInteropToolbox.WinToWsl(image);
    var theirs = Path.Combine(this._tmp, "theirs.txt");
    File.WriteAllText(theirs, "written by the kernel");
    var pw = this.KeyFile("pw.txt", Password);
    var mnt = "/tmp/cwb-mnt-" + Guid.NewGuid().ToString("N")[..8];
    var (log, exit) = ThroughMapping(wslImage, pw,
      $"mkdir -p {mnt} && mount MAPPER {mnt} && cp {FsInteropToolbox.WinToWsl(theirs)} {mnt}/THEIRS.TXT && umount {mnt} && rmdir {mnt}", readOnly: false);
    Assert.That(exit, Is.EqualTo(0), log);

    var ours = "written through our BitLocker mount"u8.ToArray();
    using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite)) {
      Compression.Lib.FormatRegistration.EnsureInitialized();
      using var session = Compression.Mounting.MountNamespaceResolver.Open("BitLocker", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), null,
        new Dictionary<string, string> { [BitLockerFormatDescriptor.RecoveryPasswordName] = Recovery });
      var their = session.Lookup(session.RootNodeId, "THEIRS.TXT");
      Assert.That(their, Is.Not.Null, "the kernel's file is missing from our view");
      using (var handle = session.OpenFile(their!.Value, FileAccess.Read)) {
        var buffer = new byte[handle.Length];
        handle.Read(0, buffer);
        Assert.That(System.Text.Encoding.ASCII.GetString(buffer), Is.EqualTo("written by the kernel"));
      }
      var node = session.CreateFile(session.RootNodeId, "OURS.TXT");
      using (var handle = session.OpenFile(node, FileAccess.ReadWrite))
        handle.Write(0, ours);
      session.Flush();
    }

    var back = Path.Combine(this._tmp, "back.txt");
    (log, exit) = ThroughMapping(wslImage, this.KeyFile("rp.txt", Recovery),
      $"fsck.vfat -n MAPPER && mkdir -p {mnt} && mount -o ro MAPPER {mnt} && cp {mnt}/OURS.TXT {FsInteropToolbox.WinToWsl(back)}; rc=$?; umount {mnt}; rmdir {mnt}; exit $rc");
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(back), Is.EqualTo(ours));
  }
}
