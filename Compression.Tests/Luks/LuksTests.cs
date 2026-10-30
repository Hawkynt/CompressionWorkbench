using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Luks;

namespace Compression.Tests.Luks;

[TestFixture]
public class LuksTests {
  internal static byte[] SamplePayload(int sectors, int seed) {
    var data = new byte[sectors * 512];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static byte[] Create(byte[] payload, string password, int iterations = 1000) {
    using var ms = new MemoryStream();
    LuksVolume.WriteLuks1(ms, payload, password, iterations);
    return ms.ToArray();
  }

  internal static byte[] Decrypt(byte[] image, string password) {
    using var s = new MemoryStream(image);
    var v = LuksVolume.Read(s);
    var key = v.Unlock(s, password);
    using var o = new MemoryStream();
    v.DecryptPayload(s, key, o);
    return o.ToArray();
  }

  [Test]
  public void GivenIeee1619Vector1_WhenXtsEncrypts_ThenTheCiphertextMatches() {
    // XTS-AES-128, key1 = key2 = 0, data unit 0, 32 zero bytes (IEEE P1619/D16 Annex B, vector 1).
    var sector = new byte[512];
    using (var c = new LuksCipher("aes", "xts-plain64", new byte[32]))
      c.Encrypt(sector, 0);
    Assert.That(Convert.ToHexStringLower(sector.AsSpan(0, 32)),
      Is.EqualTo("917cf69ebd68b2ec9b9fe9a3eadda692cd43d2f59598ed858c02c2652fbf922e"));
  }

  [TestCase("xts-plain64", 64)]
  [TestCase("xts-plain", 32)]
  [TestCase("cbc-essiv:sha256", 32)]
  [TestCase("cbc-plain64", 16)]
  public void GivenEachSupportedMode_WhenRoundTripped_ThenIdentical(string mode, int keyBytes) {
    var data = SamplePayload(4, 1);
    var copy = (byte[])data.Clone();
    var key = new byte[keyBytes];
    new Random(2).NextBytes(key);
    using var c = new LuksCipher("aes", mode, key);
    c.Encrypt(copy, 77);
    Assert.That(copy, Is.Not.EqualTo(data));
    c.Decrypt(copy, 77);
    Assert.That(copy, Is.EqualTo(data));
  }

  [Test]
  public void GivenOurLuks1Volume_WhenOpenedWithThePassphrase_ThenThePayloadComesBack() {
    var payload = SamplePayload(64, 3);
    var image = Create(payload, "correct horse");
    Assert.That(image.AsSpan(0, 6).SequenceEqual(LuksVolume.Magic), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(104)), Is.EqualTo(4096));
    Assert.That(image.Length, Is.EqualTo(4096 * 512 + payload.Length));
    Assert.That(Decrypt(image, "correct horse"), Is.EqualTo(payload));
  }

  [Test]
  public void GivenTheWrongPassphrase_WhenUnlocking_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => Decrypt(Create(SamplePayload(2, 4), "right"), "wrong"));

  [Test]
  public void GivenNoPassphrase_WhenExtracting_ThenItAsksForOneButMetadataIsWritten() {
    var image = Create(SamplePayload(2, 5), "pw");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-luks-" + Guid.NewGuid().ToString("N"));
    try {
      Assert.Throws<InvalidOperationException>(() => new LuksFormatDescriptor().Extract(new MemoryStream(image), dir, null, null));
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini")), Does.Contain("version=1").And.Contain("cipher=aes-xts-plain64"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingWithOptions_ThenListAndExtractWork() {
    var payload = SamplePayload(8, 6);
    var path = Path.GetTempFileName();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-luks-" + Guid.NewGuid().ToString("N"));
    try {
      File.WriteAllBytes(path, payload);
      using var ms = new MemoryStream();
      var d = new LuksFormatDescriptor();
      d.Create(ms, [new ArchiveInputInfo(path, "volume.img", false)], new FormatCreateOptions {
        Password = "pw", FormatSpecific = new() { ["Iterations"] = "1000" },
      });
      var listed = d.List(new MemoryStream(ms.ToArray()), null);
      Assert.That(listed.Single(e => e.Name == "volume.img").IsEncrypted, Is.True);
      d.Extract(new MemoryStream(ms.ToArray()), dir, "pw", null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "volume.img")), Is.EqualTo(payload));
    } finally {
      File.Delete(path);
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void GivenNoPassphrase_WhenCreating_ThenRefused()
    => Assert.Throws<ArgumentException>(() => new LuksFormatDescriptor().Create(new MemoryStream(), [], new FormatCreateOptions()));

  [Test]
  public void GivenAPartialSector_WhenCreating_ThenRefused()
    => Assert.Throws<ArgumentException>(() => LuksVolume.WriteLuks1(new MemoryStream(), new byte[100], "pw", 1000));

  [Test]
  public void GivenTooFewIterations_WhenCreating_ThenRefused()
    => Assert.Throws<ArgumentOutOfRangeException>(() => LuksVolume.WriteLuks1(new MemoryStream(), new byte[512], "pw", 10));

  [Test]
  public void GivenACorruptKeySlotState_WhenRead_ThenInvalidDataAndListFallsBack() {
    var image = Create(SamplePayload(1, 7), "pw");
    image[208] = 0x12;
    Assert.Throws<InvalidDataException>(() => LuksVolume.Read(new MemoryStream(image)));
    Assert.That(new LuksFormatDescriptor().List(new MemoryStream(image), null).Single().Name, Is.EqualTo("FULL.luks"));
  }

  [Test]
  public void GivenADamagedVolumeKeyDigest_WhenUnlocking_ThenNoSlotOpens() {
    var image = Create(SamplePayload(1, 8), "pw");
    image[112] ^= 1;
    Assert.Throws<InvalidDataException>(() => Decrypt(image, "pw"));
  }

  [Test]
  public void GivenNoMagic_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => LuksVolume.Read(new MemoryStream(new byte[1024])));

  [Test]
  public void GivenAnUnsupportedCipher_WhenConstructing_ThenNotSupported()
    => Assert.Throws<NotSupportedException>(() => new LuksCipher("serpent", "xts-plain64", new byte[64]));
}

/// <summary>
/// cryptsetup as the oracle, both ways: it opens our LUKS1 volumes (header, key slot, volume-key
/// digest) and its device-mapper view of them is our plaintext; and volumes it formats — LUKS1
/// and LUKS2, XTS and CBC-ESSIV, PBKDF2 key slots — decrypt with our reader to what was written
/// through its mapping. Needs root for the mapping (sudo). Skipped when cryptsetup is absent
/// unless <c>CWB_REQUIRE_CRYPTSETUP=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class LuksExternalConformanceTests {
  private const string Passphrase = "cwb oracle passphrase";

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

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-luksx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Opens <paramref name="image"/> through device-mapper and copies the plaintext out (or in).</summary>
  private static (string Out, int Exit) ThroughMapping(string image, string passFile, string action) {
    var name = "cwb" + Guid.NewGuid().ToString("N")[..10];
    var r = Run(Sudo($"cryptsetup open --type luks --key-file {passFile} {image} {name}") + " && " +
                Sudo($"sh -c '{action.Replace("MAPPER", "/dev/mapper/" + name)}'") + "; rc=$?; " +
                Sudo($"cryptsetup close {name}") + "; exit $rc");
    return (r.StdOut + r.StdErr, r.ExitCode);
  }

  private string PassFile() {
    var p = Path.Combine(this._tmp, "pass.txt");
    File.WriteAllText(p, Passphrase);
    return FsInteropToolbox.WinToWsl(p);
  }

  [Test]
  public void GivenOurLuks1Volume_WhenCryptsetupOpensIt_ThenItsMappingIsOurPlaintext() {
    RequireCryptsetup();
    var payload = LuksTests.SamplePayload(256, 11);
    var image = Path.Combine(this._tmp, "ours.luks");
    File.WriteAllBytes(image, LuksTests.Create(payload, Passphrase));
    var wslImage = FsInteropToolbox.WinToWsl(image);

    var dump = Run($"cryptsetup luksDump {wslImage}");
    Assert.That(dump.ExitCode, Is.EqualTo(0), "luksDump refused our header");
    Assert.That(dump.StdOut, Does.Contain("Version:").And.Contain("aes"));
    var test = Run($"cryptsetup open --test-passphrase --key-file {this.PassFile()} {wslImage}");
    Assert.That(test.ExitCode, Is.EqualTo(0), "cryptsetup does not accept our key slot");

    var outFile = Path.Combine(this._tmp, "plain.bin");
    var (_, exit) = ThroughMapping(wslImage, this.PassFile(), $"cat MAPPER > {FsInteropToolbox.WinToWsl(outFile)}");
    Assert.That(exit, Is.EqualTo(0));
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(payload));
  }

  [TestCase("luks1", "aes-xts-plain64", 512)]
  [TestCase("luks1", "aes-cbc-essiv:sha256", 256)]
  [TestCase("luks2", "aes-xts-plain64", 512)]
  [TestCase("luks2", "aes-xts-plain64", 256)]
  [TestCase("luks2-argon2id", "aes-xts-plain64", 512)]
  [TestCase("luks2-argon2i", "aes-xts-plain64", 256)]
  public void GivenACryptsetupVolume_WhenWeOpenIt_ThenThePlaintextMatches(string type, string cipher, int keyBits) {
    RequireCryptsetup();
    var image = Path.Combine(this._tmp, "theirs.luks");
    var wslImage = FsInteropToolbox.WinToWsl(image);
    var payload = LuksTests.SamplePayload(1024, keyBits + type.Length);
    var plain = Path.Combine(this._tmp, "plain.bin");
    File.WriteAllBytes(plain, payload);

    var pbkdf = type switch {
      "luks2" => "--type luks2 --pbkdf pbkdf2 --pbkdf-force-iterations 1000 ",
      "luks2-argon2id" => "--type luks2 --pbkdf argon2id --pbkdf-memory 1024 --pbkdf-parallel 2 --pbkdf-force-iterations 4 ",
      "luks2-argon2i" => "--type luks2 --pbkdf argon2i --pbkdf-memory 2048 --pbkdf-parallel 1 --pbkdf-force-iterations 4 ",
      _ => "--type luks1 --pbkdf-force-iterations 1000 ",
    };
    Assert.That(Run($"truncate -s 20M {wslImage} && cryptsetup luksFormat -q {pbkdf}" +
                    $"-c {cipher} -s {keyBits} --key-file {this.PassFile()} {wslImage}").ExitCode, Is.EqualTo(0), "luksFormat failed");
    var (_, exit) = ThroughMapping(wslImage, this.PassFile(), $"dd if={FsInteropToolbox.WinToWsl(plain)} of=MAPPER bs=512 conv=notrunc status=none");
    Assert.That(exit, Is.EqualTo(0), "writing through the mapping failed");

    var image2 = File.ReadAllBytes(image);
    var ours = LuksTests.Decrypt(image2, Passphrase);
    Assert.That(ours.AsSpan(0, payload.Length).SequenceEqual(payload), Is.True, "our decryption differs from what cryptsetup encrypted");
  }
}
