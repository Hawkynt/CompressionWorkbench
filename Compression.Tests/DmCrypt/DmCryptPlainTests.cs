using System.Security.Cryptography;
using System.Text;
using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using Compression.Tests.Luks;
using FileFormat.DmCrypt;

namespace Compression.Tests.DmCrypt;

[TestFixture]
public sealed class DmCryptPlainTests {
  private const string Passphrase = "plain passphrase";

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  private static byte[] Create(byte[] payload, DmCryptPlainParameters? p = null) {
    using var ms = new MemoryStream();
    DmCryptPlainVolume.Write(ms, payload, Passphrase, p ?? new());
    return ms.ToArray();
  }

  private static byte[] Decrypt(byte[] image, DmCryptPlainParameters? p = null, string passphrase = Passphrase) {
    using var device = DmCryptPlainVolume.OpenBlockDevice(new MemoryStream(image), passphrase, p ?? new(), writable: false);
    var plain = new byte[device.Geometry.Length];
    device.ReadBlocks(0, plain);
    return plain;
  }

  [Test]
  public void GivenAKeyNoLongerThanTheDigest_WhenDerived_ThenItIsTheDigestPrefix()
    => Assert.That(DmCryptPlainVolume.DeriveKey("pw", "sha256", 32), Is.EqualTo(SHA256.HashData("pw"u8.ToArray())));

  [Test]
  public void GivenAKeyLongerThanTheDigest_WhenDerived_ThenEachRoundPrependsOneMoreA() {
    var expected = SHA256.HashData("pw"u8.ToArray()).Concat(SHA256.HashData("Apw"u8.ToArray())).ToArray();
    Assert.That(DmCryptPlainVolume.DeriveKey("pw", "sha256", 64), Is.EqualTo(expected));
    var rmd = DmCryptPlainVolume.DeriveKey("pw", "ripemd160", 32);
    Assert.That(rmd.AsSpan(0, 20).ToArray(), Is.EqualTo(Hawkynt.Algorithms.Hashing.Ripemd160.Compute("pw"u8)));
    Assert.That(rmd.AsSpan(20).ToArray(), Is.EqualTo(Hawkynt.Algorithms.Hashing.Ripemd160.Compute("Apw"u8).AsSpan(0, 12).ToArray()));
  }

  [Test]
  public void GivenAHashLength_WhenDerived_ThenTheRestOfTheKeyIsZero() {
    var key = DmCryptPlainVolume.DeriveKey("pw", "sha256:16", 32);
    Assert.That(key.AsSpan(0, 16).ToArray(), Is.EqualTo(SHA256.HashData("pw"u8.ToArray()).AsSpan(0, 16).ToArray()));
    Assert.That(key.AsSpan(16).ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test]
  public void GivenHashPlain_WhenDerived_ThenThePassphraseIsTheKey() {
    Assert.That(DmCryptPlainVolume.DeriveKey("0123456789abcdef", "plain", 16), Is.EqualTo(Encoding.ASCII.GetBytes("0123456789abcdef")));
    Assert.Throws<ArgumentException>(() => DmCryptPlainVolume.DeriveKey("short", "plain", 16));
  }

  [TestCase("aes-xts-plain64", "sha256", 256, 0, 0)]
  [TestCase("aes-xts-plain64", "sha512", 512, 8, 0)]
  [TestCase("aes-cbc-essiv:sha256", "ripemd160", 256, 0, 0)]
  [TestCase("aes-cbc-plain", "sha1", 128, 4, 100)]
  [TestCase("aes-xts-plain", "whirlpool", 256, 0, 7)]
  public void GivenEachParameterSet_WhenRoundTripped_ThenIdentical(string cipher, string hash, int bits, long offset, long skip) {
    var p = new DmCryptPlainParameters(cipher, hash, bits, offset, skip);
    var payload = LuksTests.SamplePayload(16, bits + (int)skip);
    var image = Create(payload, p);
    Assert.That(image.Length, Is.EqualTo(payload.Length + offset * 512));
    Assert.That(image.AsSpan((int)offset * 512).SequenceEqual(payload), Is.False);
    Assert.That(Decrypt(image, p), Is.EqualTo(payload));
  }

  [Test]
  public void GivenTheSkip_WhenDecryptingWithoutIt_ThenTheIvsDiffer() {
    var payload = LuksTests.SamplePayload(4, 2);
    var image = Create(payload, new(SkipSectors: 9));
    Assert.That(Decrypt(image, new(SkipSectors: 9)), Is.EqualTo(payload));
    Assert.That(Decrypt(image, new()), Is.Not.EqualTo(payload));
  }

  [Test]
  public void GivenAFatImage_WhenMountedWithTheParameters_ThenTheFileReadsBackAndWritesLand() {
    var content = Enumerable.Range(0, 3000).Select(static i => (byte)i).ToArray();
    var p = new DmCryptPlainParameters("aes-cbc-essiv:sha256", "ripemd160", 256, OffsetSectors: 16);
    using var image = new MemoryStream(Create(LuksMountTests.FatImage("A.TXT", content), p));
    var parameters = new Dictionary<string, string> { ["cipher"] = p.Cipher, ["hash"] = p.Hash, ["keysize"] = "256", ["offset"] = "16" };

    var probe = MountNamespaceResolver.Probe("DmCryptPlain", image, Passphrase, parameters);
    Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));

    using (var session = MountNamespaceResolver.Open("DmCryptPlain", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), Passphrase, parameters)) {
      var node = session.Lookup(session.RootNodeId, "A.TXT")!.Value;
      using (var h = session.OpenFile(node, FileAccess.Read)) {
        var buffer = new byte[h.Length];
        h.Read(0, buffer);
        Assert.That(buffer, Is.EqualTo(content));
      }
      var added = session.CreateFile(session.RootNodeId, "B.TXT");
      using (var h = session.OpenFile(added, FileAccess.ReadWrite))
        h.Write(0, "plain"u8);
      session.Flush();
    }

    using var fat = FormatRegistry.OpenFilesystem("Fat", new MemoryStream(Decrypt(image.ToArray(), p)), new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: false));
    Assert.That(fat.Lookup(fat.RootNodeId, "B.TXT"), Is.Not.Null);
  }

  [Test]
  public void GivenTheWrongPassphrase_WhenProbed_ThenNoFilesystemIsFound() {
    using var image = new MemoryStream(Create(LuksMountTests.FatImage("A.TXT", [1, 2, 3])));
    var probe = MountNamespaceResolver.Probe("DmCryptPlain", image, "not it");
    Assert.That(probe.Layers, Does.Not.Contain("filesystem:Fat"));
  }

  [Test]
  public void GivenNoPassphrase_WhenProbed_ThenItAsksForOne()
    => Assert.Throws<ArchivePasswordException>(() => MountNamespaceResolver.Probe("DmCryptPlain", new MemoryStream(new byte[4096])));

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndExtractingWithDefaults_ThenThePayloadComesBack() {
    var payload = LuksTests.SamplePayload(8, 5);
    var d = new DmCryptPlainFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions { Password = Passphrase });
    var dir = Path.Combine(Path.GetTempPath(), "cwb-dmplain-" + Guid.NewGuid().ToString("N"));
    try {
      d.Extract(new MemoryStream(ms.ToArray()), dir, Passphrase, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "volume.img")), Is.EqualTo(payload));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenAnUnknownHash_WhenDerived_ThenRefused()
    => Assert.Throws<NotSupportedException>(() => DmCryptPlainVolume.DeriveKey("pw", "md4", 32));
}

/// <summary>
/// cryptsetup as the oracle, both ways, for every parameter set: <c>cryptsetup open --type
/// plain</c> maps our volumes to our plaintext, and what it writes through its own mapping
/// decrypts with ours. Needs root for the mapping. Skipped without cryptsetup unless
/// <c>CWB_REQUIRE_CRYPTSETUP=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class DmCryptPlainExternalConformanceTests {
  private const string Passphrase = "cwb plain oracle";
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-dmplainx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireCryptsetup() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("cryptsetup"))
      return;
    const string hint = "cryptsetup not installed: `sudo apt-get install -y cryptsetup-bin`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_CRYPTSETUP") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static string Sudo(string command) => $"echo 1234 | sudo -S -p '' {command}";

  /// <summary>Maps <paramref name="image"/> with cryptsetup's plain mode and runs <paramref name="action"/> against it.</summary>
  private (string Log, int Exit) ThroughMapping(string image, string options, string action) {
    var pass = Path.Combine(this._tmp, "pass.txt");
    File.WriteAllText(pass, Passphrase);
    var name = "cwbp" + Guid.NewGuid().ToString("N")[..10];
    // Plain mode hashes the passphrase only when it is read as one, so it comes on stdin, not as a key file.
    var r = FsInteropToolbox.RunWsl(
      Sudo($"sh -c 'cryptsetup open --type plain {options} {FsInteropToolbox.WinToWsl(image)} {name} < {FsInteropToolbox.WinToWsl(pass)}'") +
      " && " + Sudo($"sh -c '{action.Replace("MAPPER", "/dev/mapper/" + name)}'") + "; rc=$?; " + Sudo($"cryptsetup close {name}") + "; exit $rc");
    TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return (r.StdOut + r.StdErr, r.ExitCode);
  }

  private static string Options(DmCryptPlainParameters p)
    => $"-c {p.Cipher} -h {p.Hash} -s {p.KeyBits} --offset {p.OffsetSectors} --skip {p.SkipSectors}";

  private static readonly object[] Cases = [
    new object[] { "aes-xts-plain64", "sha256", 256, 0L, 0L },
    new object[] { "aes-xts-plain64", "sha512", 512, 8L, 0L },
    new object[] { "aes-cbc-essiv:sha256", "ripemd160", 256, 0L, 0L },
    new object[] { "aes-xts-plain64", "ripemd160", 512, 0L, 0L },
    new object[] { "aes-cbc-plain", "sha1", 128, 4L, 100L },
    new object[] { "aes-xts-plain64", "sha256:20", 512, 0L, 33L },
  ];

  [TestCaseSource(nameof(Cases))]
  public void GivenOurVolume_WhenCryptsetupMapsIt_ThenTheMappingIsOurPlaintext(string cipher, string hash, int bits, long offset, long skip) {
    RequireCryptsetup();
    var p = new DmCryptPlainParameters(cipher, hash, bits, offset, skip);
    var payload = LuksTests.SamplePayload(64, bits + (int)skip);
    var image = Path.Combine(this._tmp, "ours.img");
    using (var fs = File.Create(image))
      DmCryptPlainVolume.Write(fs, payload, Passphrase, p);
    var outFile = Path.Combine(this._tmp, "plain.bin");
    var (log, exit) = this.ThroughMapping(image, Options(p), $"cat MAPPER > {FsInteropToolbox.WinToWsl(outFile)}");
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(payload));
  }

  [TestCaseSource(nameof(Cases))]
  public void GivenCryptsetupsVolume_WhenWeDecryptIt_ThenThePlaintextMatches(string cipher, string hash, int bits, long offset, long skip) {
    RequireCryptsetup();
    var p = new DmCryptPlainParameters(cipher, hash, bits, offset, skip);
    var payload = LuksTests.SamplePayload(64, bits * 3 + (int)skip);
    var image = Path.Combine(this._tmp, "theirs.img");
    File.WriteAllBytes(image, new byte[payload.Length + offset * 512]);
    var plain = Path.Combine(this._tmp, "plain.bin");
    File.WriteAllBytes(plain, payload);
    var (log, exit) = this.ThroughMapping(image, Options(p), $"dd if={FsInteropToolbox.WinToWsl(plain)} of=MAPPER bs=512 conv=notrunc,fsync status=none");
    Assert.That(exit, Is.EqualTo(0), log);

    using var device = DmCryptPlainVolume.OpenBlockDevice(new MemoryStream(File.ReadAllBytes(image)), Passphrase, p, writable: false);
    var ours = new byte[device.Geometry.Length];
    device.ReadBlocks(0, ours);
    Assert.That(ours, Is.EqualTo(payload));
  }
}
