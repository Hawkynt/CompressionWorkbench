using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// Classic StuffIt password encryption. The real archive is StuffIt Deluxe 4.5's password build
/// from ssokolow/stuffit-test-files (CC0, password "password"), in its MacBinary form because the
/// key check lives in the archive's resource fork. The Unarchiver's unar must decrypt what we write.
/// </summary>
[TestFixture]
public class StuffItClassicEncryptionTests {
  private const string Password = "password";
  private const string Vector = "testfile.stuffit45_dlx.mac9.password.sit.bin";

  private static readonly Dictionary<string, (string Data, string Rsrc)> Sources = new() {
    ["Test Image"] = ("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "4b8175653903645616d9e07627957ae0dba4c7ac3b3e9aa6afc8e07144dcfbb0"),
    ["Test Text"] = ("9734aef6d3788ba985e78f7b3785dc4817e770be92a4e5e57e64a92cc9c2fc25", "5f0c7e77ac2430be40532730665ea27f0cf1088ac049e0c06851d62085b87315"),
    ["testfile.PICT"] = ("318d71cd4d027c6bec6917af3ddc3b7df0ec8b07031045a9cdd9052b94c7782e", "011604ad448ef4451081d04bd395c2a974cab637877fb64b45e62ebe39bc452e"),
    ["testfile.txt"] = ("b645efee0ed710034959eae942277a750d08687c30bcf0e9ec6ea7641527462f", "f788dcd5313a531a27fc62a9b4c951a6653ef11b49f2262ee0796f72c5564b0a"),
    ["testfile.jpg"] = ("e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1", ""),
    ["testfile.png"] = ("fdda20984cc1591419ec4583e24e72e4dba39d0b96608253f853a2dfb238ad1a", ""),
  };

  private static byte[] Load(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StuffItVectors." + name) ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  [Test, Category("ThemVsUs")]
  public void GivenEncryptedArchiveMadeByStuffIt_WhenExtractedWithThePassword_ThenEveryForkMatchesItsSource() {
    using var r = new StuffItReader(new MemoryStream(Load(Vector)), Password);
    Assert.That(r.IsStuffIt5, Is.False);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Count(e => e.IsEncrypted), Is.EqualTo(6));
    foreach (var e in files) {
      var (data, rsrc) = Sources[e.FileName];
      Assert.That(Sha(r.Extract(e)), Is.EqualTo(data), e.FileName);
      if (e.HasResourceFork) Assert.That(Sha(r.ExtractResourceFork(e)), Is.EqualTo(rsrc), e.FileName + " resource fork");
    }
  }

  [Test, Category("Exception")]
  public void GivenEncryptedArchive_WhenThePasswordIsWrongOrMissing_ThenExtractionIsRefused() {
    using (var wrong = new StuffItReader(new MemoryStream(Load(Vector)), "passwort")) {
      var e = wrong.Entries.First(x => x.FileName == "testfile.txt");
      Assert.That(Assert.Throws<ArchivePasswordException>(() => wrong.Extract(e))!.PasswordWasSupplied, Is.True);
    }
    using var none = new StuffItReader(new MemoryStream(Load(Vector)));
    Assert.That(Assert.Throws<ArchivePasswordException>(() => none.Extract(none.Entries.First(x => x.FileName == "testfile.txt")))!.PasswordWasSupplied, Is.False);
  }

  [Test, Category("Exception")]
  public void GivenTheDataForkAlone_WhenAnEncryptedForkIsExtracted_ThenItIsRefusedForTheMissingKeyCheck() {
    // Strip the MacBinary wrapper: without the resource fork there is no MKey to check the password against.
    var bin = Load(Vector);
    var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bin.AsSpan(83));
    using var r = new StuffItReader(new MemoryStream(bin, 128, length), Password);
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries.First(x => x.FileName == "testfile.txt")));
  }

  [Test, Category("HappyPath")]
  public void GivenAnArchiveKey_WhenAnMKeyIsMadeForIt_ThenThePasswordCheckAcceptsItAndRejectsAnotherKey() {
    var key = StuffItClassicCrypto.ArchiveKey(Encoding.ASCII.GetBytes("open sesame"));
    var (mKey, iv) = StuffItClassicCrypto.NewMKey(key);
    Assert.That(StuffItClassicCrypto.ArchiveIv(key, mKey), Is.EqualTo(iv));
    Assert.That(StuffItClassicCrypto.ArchiveIv(StuffItClassicCrypto.ArchiveKey(Encoding.ASCII.GetBytes("Open sesame")), mKey), Is.Null);
  }

  [TestCase(1), TestCase(7), TestCase(8), TestCase(9), TestCase(1000), Category("Boundary")]
  public void GivenAForkKey_WhenAForkIsEncryptedAndDecrypted_ThenItRoundTrips(int blocks) {
    var data = RandomNumberGenerator.GetBytes(blocks * 8);
    var key = RandomNumberGenerator.GetBytes(16);
    var copy = (byte[])data.Clone();
    StuffItClassicCrypto.EncryptFork(copy, key);
    Assert.That(copy, Is.Not.EqualTo(data));
    StuffItClassicCrypto.DecryptFork(copy, key);
    Assert.That(copy, Is.EqualTo(data));
  }

  private static readonly byte[] Noise = RandomNumberGenerator.GetBytes(3_001);

  private static (string Path, byte[] Data, byte[]? Resource)[] Files() => [
    ("notes.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("classic stuffit secret; ", 1_500))), Encoding.ASCII.GetBytes("resource!")),
    ("folder/eight.bin", Encoding.ASCII.GetBytes("12345678"), null),
    ("folder/noise.bin", Noise, null),
    ("folder/empty.txt", [], null),
  ];

  private static byte[] Write(StuffItCompression compression, string? password, (string Path, byte[] Data, byte[]? Resource)[]? files = null) {
    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, compression, password, leaveOpen: true))
      foreach (var f in files ?? Files()) w.AddFileWithResourceFork(f.Path, f.Data, f.Resource ?? []);
    return ms.ToArray();
  }

  [TestCase(StuffItCompression.Smallest), TestCase(StuffItCompression.Store), TestCase(StuffItCompression.LzHuffman)]
  [Category("RoundTrip")]
  public void GivenOurEncryptedArchive_WhenReadBack_ThenForksMatchAndTheWrongPasswordIsRefused(StuffItCompression compression) {
    var archive = Write(compression, Password);
    Assert.That(archive[0], Is.EqualTo(0), "an encrypted classic archive is a MacBinary file");
    Assert.That(archive.AsSpan().IndexOf("classic stuffit secret"u8), Is.EqualTo(-1));
    using (var r = new StuffItReader(new MemoryStream(archive), Password)) {
      foreach (var f in Files()) {
        var e = r.Entries.Single(x => x.FileName == f.Path);
        Assert.That(r.Extract(e), Is.EqualTo(f.Data), f.Path);
        if (f.Resource != null) Assert.That(r.ExtractResourceFork(e), Is.EqualTo(f.Resource), f.Path);
      }
    }
    using var wrong = new StuffItReader(new MemoryStream(archive), "Password");
    Assert.Throws<ArchivePasswordException>(() => wrong.Extract(wrong.Entries.Single(x => x.FileName == "notes.txt")));
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptorWithPassword_WhenCreatingClassic_ThenTheArchiveOpensWithIt() {
    using var ms = new MemoryStream();
    new StuffItFormatDescriptor().Create(ms, [new ArchiveInputInfo("a.txt", "a.txt", false, "abc"u8.ToArray())], new FormatCreateOptions { Password = Password });
    using var r = new StuffItReader(new MemoryStream(ms.ToArray()), Password);
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo("abc"u8.ToArray()));
  }

  // ── The Unarchiver ───────────────────────────────────────────────────────

  [TestCase(StuffItCompression.Smallest), TestCase(StuffItCompression.Store)]
  [Category("ExternalInterop")]
  public void GivenOurEncryptedArchive_WhenUnarExtractsWithThePassword_ThenEveryFileIsByteIdenticalAndAWrongPasswordFails(StuffItCompression compression) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitdes_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, "secret.sit.bin");
      File.WriteAllBytes(path, Write(compression, Password));
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k visible -p {Password} -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdOut + run.StdErr);
      foreach (var f in Files())
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(f.Data), f.Path);

      var bad = FsInteropToolbox.RunWsl($"unar -q -f -p wrong -D -o '{FsInteropToolbox.WinToWsl(Path.Combine(tmp, "bad"))}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(bad.ExitCode, Is.Not.EqualTo(0), "unar must refuse the wrong password");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }

  /// <summary>Passwords around the 8-byte block boundary decide how many blocks enter the archive key.</summary>
  [TestCase("a"), TestCase("1234567"), TestCase("12345678"), TestCase("123456789"), TestCase("fifteen chars!!"), TestCase("sixteen chars!!!"), TestCase("seventeen chars!!"), TestCase("twenty-five characters!!!")]
  [Category("ExternalInterop")]
  public void GivenPasswordLength_WhenUnarExtractsOurArchive_ThenItAgreesOnTheArchiveKey(string password) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var data = Encoding.ASCII.GetBytes("password length probe " + password);
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitdes_pw_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, "probe.sit.bin");
      File.WriteAllBytes(path, Write(StuffItCompression.Store, password, [("probe.txt", data, null)]));
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -p '{password}' -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdOut + run.StdErr);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "probe.txt")), Is.EqualTo(data));
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
