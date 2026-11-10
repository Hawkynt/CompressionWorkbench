using Compression.Tests.Luks;
using FileFormat.VeraCrypt;

namespace Compression.Tests.VeraCrypt;

/// <summary>
/// VeraCrypt's own CLI and cryptsetup as oracles, both ways: <c>veracrypt --create</c> makes
/// containers for every cipher, cascade and PRF it offers, and we decrypt them to what VeraCrypt
/// wrote through its mapping; and our containers — VeraCrypt and TrueCrypt format, sparse ones
/// included — are mounted by <c>veracrypt --mount</c> and <c>cryptsetup tcryptOpen</c>, whose
/// device-mapper views are our plaintext. Needs root for the mappings. Skipped without the tools
/// unless <c>CWB_REQUIRE_VERACRYPT=1</c> / <c>CWB_REQUIRE_CRYPTSETUP=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
[NonParallelizable]
public sealed class VeraCryptExternalConformanceTests {
  private const string Password = "cwb veracrypt oracle password";
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-vcx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void Require(string tool, string variable) {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool)) return;
    var hint = $"{tool} is not installed.";
    if (Environment.GetEnvironmentVariable(variable) == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string Out, int Exit) Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command}\n-> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return (r.StdOut + r.StdErr, r.ExitCode);
  }

  private static string Sudo(string command) => $"echo 1234 | sudo -S -p '' {command}";

  private static string W(string path) => FsInteropToolbox.WinToWsl(path);

  private string RandomSource() {
    var p = Path.Combine(this._tmp, "random.bin");
    if (!File.Exists(p)) File.WriteAllBytes(p, LuksTests.SamplePayload(2048, 99));
    return W(p);
  }

  /// <summary>Maps a container with VeraCrypt itself (no filesystem) and runs <paramref name="action"/> against the mapping.</summary>
  private static (string Out, int Exit) ThroughVeraCrypt(string container, int pim, string action, string hash = "") {
    var slot = 40 + Random.Shared.Next(24);
    // VeraCrypt maps through device-mapper when the kernel has the ciphers and through a FUSE-backed
    // loop device otherwise (Kuznyechik); its listing names whichever block device it used.
    const string device = "$(" + "echo 1234 | sudo -S -p '' veracrypt --text --list --slot=SLOT | awk '{print $3}')";
    return Run(Sudo($"veracrypt --text --non-interactive --mount {W(container)} --slot={slot} --filesystem=none --password='{Password}' --pim={pim} {hash} -k '' --protect-hidden=no") +
               $" && dev={device.Replace("SLOT", slot.ToString(System.Globalization.CultureInfo.InvariantCulture))} && test -b \"$dev\" && " +
               Sudo($"sh -c '{action.Replace("MAPPER", "$dev")}'").Replace("sh -c '", "env dev=\"$dev\" sh -c '") +
               "; rc=$?; " + Sudo($"veracrypt --text --non-interactive --unmount {W(container)}") + "; exit $rc");
  }

  /// <summary>Maps a container with cryptsetup's TCRYPT support and runs <paramref name="action"/>.</summary>
  private (string Out, int Exit) ThroughCryptsetup(string container, int pim, string action, bool veraCrypt = true) {
    var pass = Path.Combine(this._tmp, "pass.txt");
    File.WriteAllText(pass, Password);
    var name = "cwbt" + Guid.NewGuid().ToString("N")[..10];
    var options = veraCrypt ? $"--veracrypt{(pim > 0 ? $" --veracrypt-pim {pim}" : "")}" : "--disable-veracrypt";
    return Run(Sudo($"sh -c 'cryptsetup tcryptOpen {options} {W(container)} {name} < {W(pass)}'") +
               " && " + Sudo($"sh -c '{action.Replace("MAPPER", "/dev/mapper/" + name)}'") + "; rc=$?; " + Sudo($"cryptsetup close {name}") + "; exit $rc");
  }

  private static readonly string[] VeraCryptCiphers = [
    "AES", "Serpent", "Twofish", "Camellia", "Kuznyechik", "AES-Twofish", "AES-Twofish-Serpent", "Serpent-AES",
    "Serpent-Twofish-AES", "Twofish-Serpent", "Camellia-Kuznyechik", "Camellia-Serpent", "Kuznyechik-AES",
    "Kuznyechik-Serpent-Camellia", "Kuznyechik-Twofish",
  ];

  private static readonly (string VeraCrypt, string Ours)[] VeraCryptHashes = [
    ("SHA-512", "sha512"), ("SHA-256", "sha256"), ("Whirlpool", "whirlpool"), ("BLAKE2s-256", "blake2s-256"), ("Streebog", "stribog512"), ("Argon2", "argon2id"),
  ];

  public static IEnumerable<TestCaseData> TheirCases() {
    foreach (var c in VeraCryptCiphers) yield return new TestCaseData(c, "SHA-512", "sha512").SetName($"VeraCryptCreated_{c.Replace('-', '_')}_SHA512");
    foreach (var (h, ours) in VeraCryptHashes.Skip(1)) yield return new TestCaseData("Serpent-Twofish-AES", h, ours).SetName($"VeraCryptCreated_SerpentTwofishAES_{h.Replace("-", "")}");
  }

  [TestCaseSource(nameof(TheirCases))]
  public void GivenAVeraCryptCreatedContainer_WhenWeOpenIt_ThenItsMappingIsOurPlaintext(string cipher, string hash, string prf) {
    Require("veracrypt", "CWB_REQUIRE_VERACRYPT");
    var container = Path.Combine(this._tmp, "theirs.hc");
    var (log, exit) = Run($"veracrypt --text --non-interactive --create {W(container)} --size=1M --password='{Password}' --encryption={cipher} --hash={hash} --pim=1 --filesystem=none --volume-type=normal -k '' --random-source={this.RandomSource()}");
    Assert.That(exit, Is.EqualTo(0), log);

    var payload = LuksTests.SamplePayload(1024 - 512, cipher.Length + hash.Length);  // 1 MiB minus VeraCrypt's 256 KiB of headers
    var plain = Path.Combine(this._tmp, "plain.bin");
    File.WriteAllBytes(plain, payload);
    (log, exit) = ThroughVeraCrypt(container, 1, $"dd if={W(plain)} of=MAPPER bs=512 conv=notrunc,fsync status=none", $"--hash={hash}");
    Assert.That(exit, Is.EqualTo(0), log);

    using var s = File.OpenRead(container);
    var v = TcVolume.Open(s, Password, new() { Pim = 1, Prf = prf });
    Assert.That(v, Is.Not.Null, "we could not open VeraCrypt's header");
    Assert.That(v!.Chain.DisplayName, Is.EqualTo(cipher));
    using var device = v.OpenBlockDevice(s, writable: false);
    var ours = new byte[device.Geometry.Length];
    device.ReadBlocks(0, ours);
    Assert.That(ours.AsSpan(0, payload.Length).SequenceEqual(payload), Is.True, "our decryption differs from what VeraCrypt encrypted");
  }

  public static IEnumerable<TestCaseData> OurCases() {
    foreach (var c in VeraCryptCiphers) yield return new TestCaseData(c, "sha512", false).SetName($"OurContainer_{c.Replace('-', '_')}_SHA512");
    foreach (var (_, prf) in VeraCryptHashes.Skip(1)) yield return new TestCaseData("AES-Twofish-Serpent", prf, false).SetName($"OurContainer_AESTwofishSerpent_{prf.Replace("-", "")}");
    yield return new TestCaseData("AES", "sha512", true).SetName("OurContainer_AES_SHA512_Sparse");
  }

  [TestCaseSource(nameof(OurCases))]
  public void GivenOurContainer_WhenVeraCryptMountsIt_ThenItsMappingIsOurPlaintext(string cipher, string prf, bool sparse) {
    Require("veracrypt", "CWB_REQUIRE_VERACRYPT");
    var payload = LuksTests.SamplePayload(512, cipher.Length * 7 + prf.Length);
    var container = Path.Combine(this._tmp, "ours.hc");
    using (var fs = File.Create(container))
      TcVolumeWriter.Write(fs, payload, Password, new TcCreateOptions { Cipher = cipher, Prf = prf, Pim = 1, Sparse = sparse, VolumeSize = sparse ? 4 << 20 : 0 });
    var outFile = Path.Combine(this._tmp, "plain.bin");
    var (log, exit) = ThroughVeraCrypt(container, 1, $"dd if=MAPPER of={W(outFile)} bs=512 count={payload.Length / 512} status=none");
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(payload));
  }

  [TestCase(true, "aes", "sha512", 1)]
  [TestCase(true, "serpent-twofish-aes", "whirlpool", 1)]
  [TestCase(false, "aes-twofish-serpent", "ripemd160", 0)]
  [TestCase(false, "twofish", "sha512", 0)]
  public void GivenOurContainer_WhenCryptsetupOpensIt_ThenItsMappingIsOurPlaintext(bool veraCrypt, string cipher, string prf, int pim) {
    Require("cryptsetup", "CWB_REQUIRE_CRYPTSETUP");
    var payload = LuksTests.SamplePayload(256, cipher.Length + prf.Length);
    var container = Path.Combine(this._tmp, veraCrypt ? "ours.hc" : "ours.tc");
    using (var fs = File.Create(container))
      TcVolumeWriter.Write(fs, payload, Password, new TcCreateOptions { VeraCrypt = veraCrypt, Cipher = cipher, Prf = prf, Pim = pim });
    var outFile = Path.Combine(this._tmp, "plain.bin");
    var (log, exit) = this.ThroughCryptsetup(container, pim, $"cat MAPPER > {W(outFile)}", veraCrypt);
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(payload));
  }
}
