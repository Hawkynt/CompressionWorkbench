using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using Compression.Tests.Luks;
using FileFormat.VeraCrypt;

namespace Compression.Tests.VeraCrypt;

[TestFixture]
public sealed class VeraCryptTests {
  private const string Password = "vera password";

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  internal static byte[] Create(byte[] payload, TcCreateOptions options, string password = Password) {
    using var ms = new MemoryStream();
    TcVolumeWriter.Write(ms, payload, password, options);
    return ms.ToArray();
  }

  internal static byte[] Decrypt(byte[] container, string password = Password, TcUnlockOptions? unlock = null) {
    using var s = new MemoryStream(container);
    var v = TcVolume.Open(s, password, unlock) ?? throw new AssertionException("the header did not open");
    using var device = v.OpenBlockDevice(s, writable: false);
    var plain = new byte[device.Geometry.Length];
    device.ReadBlocks(0, plain);
    return plain;
  }

  // Cheap derivations keep the round trips fast: TrueCrypt's 1000–2000 iterations, VeraCrypt with PIM 1.
  private static TcCreateOptions Fast(string cipher, bool veraCrypt = true, string prf = "sha512")
    => new() { Cipher = cipher, VeraCrypt = veraCrypt, Prf = veraCrypt ? prf : "sha512", Pim = veraCrypt ? 1 : 0 };

  public static IEnumerable<string> XtsChains() => TcCipherChain.All.Where(static c => c.Mode == TcMode.Xts).Select(static c => c.Name);

  [TestCaseSource(nameof(XtsChains))]
  public void GivenEachXtsChain_WhenAVeraCryptVolumeIsWrittenAndOpened_ThenThePayloadComesBack(string chain) {
    var payload = LuksTests.SamplePayload(16, chain.Length);
    var container = Create(payload, Fast(chain));
    Assert.That(container.Length, Is.EqualTo(payload.Length + 2 * 131072));
    Assert.That(Decrypt(container, unlock: new() { Pim = 1 }), Is.EqualTo(payload));
  }

  [TestCase("sha512")]
  [TestCase("sha256")]
  [TestCase("whirlpool")]
  [TestCase("blake2s-256")]
  [TestCase("stribog512")]
  [TestCase("ripemd160")]
  public void GivenEachVeraCryptPrf_WhenWrittenAndOpened_ThenTheHeaderOpens(string prf) {
    var payload = LuksTests.SamplePayload(4, 3);
    var container = Create(payload, Fast("aes", prf: prf));
    using var s = new MemoryStream(container);
    var v = TcVolume.Open(s, Password, new() { Pim = 1, Prf = prf });
    Assert.That(v, Is.Not.Null);
    Assert.That((v!.VeraCrypt, v.Kdf.Prf, v.Chain.Name), Is.EqualTo((true, prf, "aes")));
  }

  [TestCase("ripemd160")]
  [TestCase("sha512")]
  [TestCase("whirlpool")]
  public void GivenTheTrueCryptFormat_WhenWrittenAndOpened_ThenItIsATrueCryptVolume(string prf) {
    var payload = LuksTests.SamplePayload(4, 4);
    var container = Create(payload, new() { VeraCrypt = false, Prf = prf, Cipher = "AES-Twofish-Serpent" });
    using var s = new MemoryStream(container);
    var v = TcVolume.Open(s, Password, new() { Prf = prf });
    Assert.That(v, Is.Not.Null);
    Assert.That((v!.VeraCrypt, v.Chain.Name), Is.EqualTo((false, "serpent-twofish-aes")));
    Assert.That(Decrypt(container, unlock: new() { Prf = prf }), Is.EqualTo(payload));
  }

  [Test]
  public void GivenTheWrongPassword_WhenOpened_ThenNoHeaderOpens() {
    var container = Create(LuksTests.SamplePayload(2, 1), Fast("aes"));
    Assert.That(TcVolume.Open(new MemoryStream(container), "not it", new() { Pim = 1, Prf = "sha512" }), Is.Null);
  }

  [Test]
  public void GivenADamagedPrimaryHeader_WhenOpened_ThenTheBackupHeaderIsUsed() {
    var payload = LuksTests.SamplePayload(8, 6);
    var container = Create(payload, Fast("serpent"));
    container.AsSpan(100, 50).Fill(0xEE);
    using var s = new MemoryStream(container);
    var v = TcVolume.Open(s, Password, new() { Pim = 1 });
    Assert.That(v!.HeaderOffset, Is.EqualTo(container.Length - 131072));
    Assert.That(Decrypt(container, unlock: new() { Pim = 1 }), Is.EqualTo(payload));
  }

  [Test]
  public void GivenKeyfiles_WhenWrittenAndOpened_ThenTheyAreNeededToo() {
    var payload = LuksTests.SamplePayload(4, 8);
    byte[][] keyfiles = [LuksTests.SamplePayload(1, 9), "second keyfile"u8.ToArray()];
    var container = Create(payload, Fast("twofish") with { Keyfiles = keyfiles });
    Assert.That(TcVolume.Open(new MemoryStream(container), Password, new() { Pim = 1, Prf = "sha512" }), Is.Null);
    Assert.That(Decrypt(container, unlock: new() { Pim = 1, Prf = "sha512", Keyfiles = keyfiles }), Is.EqualTo(payload));
  }

  [Test]
  public void GivenAVolumeLargerThanThePayload_WhenSparse_ThenTheUnusedAreaIsLeftUnwritten() {
    var payload = LuksTests.SamplePayload(8, 2);
    var path = Path.Combine(Path.GetTempPath(), "cwb-vc-sparse-" + Guid.NewGuid().ToString("N") + ".hc");
    try {
      using (var fs = File.Create(path))
        TcVolumeWriter.Write(fs, payload, Password, Fast("aes") with { VolumeSize = 64 << 20, Sparse = true });
      var bytes = File.ReadAllBytes(path);
      Assert.That(bytes.Length, Is.EqualTo((64 << 20) + 2 * 131072));
      Assert.That(bytes.AsSpan(131072 + payload.Length, 1 << 20).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "the unused area must be a hole");
      Assert.That(Decrypt(bytes, unlock: new() { Pim = 1 }).AsSpan(0, payload.Length).SequenceEqual(payload), Is.True);
      if (OperatingSystem.IsLinux())
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(AllocatedBytes(path) * 4), "the file should be sparse on disk");
    } finally {
      File.Delete(path);
    }
  }

  private static long AllocatedBytes(string path) {
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%b %B", path]) { RedirectStandardOutput = true })!;
    var parts = p.StandardOutput.ReadToEnd().Split(' ');
    p.WaitForExit();
    return long.Parse(parts[0]) * long.Parse(parts[1]);
  }

  [Test]
  public void GivenANonSparseVolume_WhenWritten_ThenTheUnusedAreaIsRandom() {
    var container = Create(LuksTests.SamplePayload(2, 2), Fast("aes") with { VolumeSize = 1 << 20 });
    Assert.That(container.AsSpan(131072 + 1024, 4096).IndexOfAnyExcept((byte)0), Is.Not.EqualTo(-1));
  }

  [Test]
  public void GivenAFatVolume_WhenMountedWithThePassword_ThenFilesReadAndWritesLand() {
    var content = Enumerable.Range(0, 4000).Select(static i => (byte)(i * 3)).ToArray();
    var container = new MemoryStream(Create(LuksMountTests.FatImage("VC.TXT", content), Fast("aes-twofish-serpent")));
    var parameters = new Dictionary<string, string> { ["Pim"] = "1", ["Prf"] = "sha512" };

    var probe = MountNamespaceResolver.Probe("VeraCrypt", container, Password, parameters);
    Assert.That(probe.Layers, Does.Contain("filesystem:Fat"));

    using (var session = MountNamespaceResolver.Open("VeraCrypt", container, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true), Password, parameters)) {
      var node = session.Lookup(session.RootNodeId, "VC.TXT")!.Value;
      using (var h = session.OpenFile(node, FileAccess.Read)) {
        var buffer = new byte[h.Length];
        h.Read(0, buffer);
        Assert.That(buffer, Is.EqualTo(content));
      }
      var added = session.CreateFile(session.RootNodeId, "NEW.TXT");
      using (var h = session.OpenFile(added, FileAccess.ReadWrite))
        h.Write(0, "veracrypt write"u8);
      session.Flush();
    }
    using var fat = FormatRegistry.OpenFilesystem("Fat", new MemoryStream(Decrypt(container.ToArray(), unlock: new() { Pim = 1 })), new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: false));
    Assert.That(fat.Lookup(fat.RootNodeId, "NEW.TXT"), Is.Not.Null);
  }

  [Test]
  public void GivenNoOrAWrongPassword_WhenMounted_ThenItAsksForOne() {
    var container = new MemoryStream(Create(LuksTests.SamplePayload(4, 1), Fast("aes")));
    var parameters = new Dictionary<string, string> { ["Pim"] = "1", ["Prf"] = "sha512" };
    Assert.That(Assert.Throws<ArchivePasswordException>(() => MountNamespaceResolver.Probe("VeraCrypt", container, null, parameters))!.PasswordWasSupplied, Is.False);
    Assert.That(Assert.Throws<ArchivePasswordException>(() => MountNamespaceResolver.Probe("VeraCrypt", container, "nope", parameters))!.PasswordWasSupplied, Is.True);
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingWithOptions_ThenListAndExtractSeeTheVolume() {
    var payload = LuksTests.SamplePayload(8, 7);
    var d = new VeraCryptFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("volume.img", payload)], new FormatCreateOptions {
      Password = Password,
      FormatSpecific = new Dictionary<string, string> { ["Format"] = "TrueCrypt", ["Cipher"] = "Serpent-AES", ["Prf"] = "whirlpool" },
    });
    var listing = d.List(new MemoryStream(ms.ToArray()), Password);
    Assert.That(listing.Select(static e => e.Name), Is.EqualTo(new[] { "metadata.ini", "volume.img" }));
    Assert.That(listing[1].OriginalSize, Is.EqualTo(payload.Length));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-vc-" + Guid.NewGuid().ToString("N"));
    try {
      d.Extract(new MemoryStream(ms.ToArray()), dir, Password, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "volume.img")), Is.EqualTo(payload));
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini")), Does.Contain("format=TrueCrypt").And.Contain("cipher=Serpent-AES").And.Contain("prf=whirlpool"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenAPimForTrueCrypt_WhenWriting_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Create(new byte[512], new() { VeraCrypt = false, Pim = 3 }));
}
