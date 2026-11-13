using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// StuffIt 5 password encryption (RC4 under MD5-derived keys). The real archives are
/// ssokolow/stuffit-test-files' password builds (CC0; the password is "password"). The Unarchiver's
/// unar must decrypt what we write, and a wrong password must be refused before any fork is
/// decoded.
/// </summary>
[TestFixture]
public class StuffIt5EncryptionTests {
  private const string Password = "password";

  private static readonly Dictionary<string, (string Data, string Rsrc)> Sources = new() {
    ["Test Image"] = ("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "4b8175653903645616d9e07627957ae0dba4c7ac3b3e9aa6afc8e07144dcfbb0"),
    ["Test Text"] = ("9734aef6d3788ba985e78f7b3785dc4817e770be92a4e5e57e64a92cc9c2fc25", "5f0c7e77ac2430be40532730665ea27f0cf1088ac049e0c06851d62085b87315"),
    ["testfile.PICT"] = ("318d71cd4d027c6bec6917af3ddc3b7df0ec8b07031045a9cdd9052b94c7782e", "011604ad448ef4451081d04bd395c2a974cab637877fb64b45e62ebe39bc452e"),
    ["testfile.txt"] = ("b645efee0ed710034959eae942277a750d08687c30bcf0e9ec6ea7641527462f", "f788dcd5313a531a27fc62a9b4c951a6653ef11b49f2262ee0796f72c5564b0a"),
    ["testfile.jpg"] = ("e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1", ""),
    ["testfile.png"] = ("fdda20984cc1591419ec4583e24e72e4dba39d0b96608253f853a2dfb238ad1a", ""),
  };

  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StuffItVectors." + name) ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  [Test, Category("ThirdPartyVector")]
  public void GivenThePassword_WhenTheCheckValueIsDerived_ThenItMatchesWhatStuffItWrote() {
    // Both StuffIt 6.5.1 (Mac) and 7 (Windows) store 76 35 B9 87 11 for "password".
    Assert.That(Convert.ToHexStringLower(StuffIt5Crypto.CheckValue(StuffIt5Crypto.KeyOf(Password))), Is.EqualTo("7635b98711"));
  }

  [TestCase("testfile.stuffit651_dlx.mac9.password.sit")]
  [TestCase("testfile.stuffit7.win.password.sit")]
  [Category("ThemVsUs")]
  public void GivenEncryptedArchiveMadeByStuffIt_WhenExtractedWithThePassword_ThenEveryForkMatchesItsSource(string vector) {
    using var r = new StuffItReader(new MemoryStream(Vector(vector)), Password);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Is.Not.Empty);
    Assert.That(files.All(e => e.IsEncrypted), Is.True);
    foreach (var e in files) {
      var (data, rsrc) = Sources[e.FileName[(e.FileName.LastIndexOf('/') + 1)..]];
      if (vector.Contains(".win.", StringComparison.Ordinal) && e.FileName.EndsWith("testfile.txt", StringComparison.Ordinal))
        data = "b2f51cd17b3cbe77f091f887d91110164a2cb5a5a9ebe828c44d655c83dca8eb"; // StuffIt for Windows stores CR as LF
      Assert.That(Sha(r.Extract(e)), Is.EqualTo(data), e.FileName);
      if (e.HasResourceFork) Assert.That(Sha(r.ExtractResourceFork(e)), Is.EqualTo(rsrc), e.FileName + " resource fork");
    }
  }

  [TestCase("testfile.stuffit651_dlx.mac9.password.sit")]
  [TestCase("testfile.stuffit7.win.password.sit")]
  [Category("Exception")]
  public void GivenEncryptedArchive_WhenThePasswordIsWrongOrMissing_ThenExtractionIsRefused(string vector) {
    using (var wrong = new StuffItReader(new MemoryStream(Vector(vector)), "Password")) {
      var e = wrong.Entries.First(x => !x.IsDirectory && x.DataForkSize > 0);
      var ex = Assert.Throws<ArchivePasswordException>(() => wrong.Extract(e))!;
      Assert.That(ex.PasswordWasSupplied, Is.True);
    }
    using var none = new StuffItReader(new MemoryStream(Vector(vector)));
    var f = none.Entries.First(x => !x.IsDirectory && x.DataForkSize > 0);
    Assert.That(Assert.Throws<ArchivePasswordException>(() => none.Extract(f))!.PasswordWasSupplied, Is.False);
  }

  private static (string Path, byte[] Data, byte[]? Resource)[] Files() => [
    ("notes.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("encrypted stuffit fork; ", 2_000))), Encoding.ASCII.GetBytes("resource bytes")),
    ("dir/noise.bin", RandomNumberGenerator.GetBytes(4_000), null),
    ("dir/empty.txt", [], null),
  ];

  private static byte[] Write((string Path, byte[] Data, byte[]? Resource)[] files, StuffItCompression compression, string? password) {
    using var ms = new MemoryStream();
    using (var w = new StuffIt5Writer(ms, compression, password, leaveOpen: true))
      foreach (var f in files) w.AddFile(f.Path, f.Data, f.Resource);
    return ms.ToArray();
  }

  [TestCase(StuffItCompression.Smallest), TestCase(StuffItCompression.LzHuffman), TestCase(StuffItCompression.Store)]
  [Category("RoundTrip")]
  public void GivenOurEncryptedArchive_WhenReadBack_ThenForksMatchAndTheWrongPasswordIsRefused(StuffItCompression compression) {
    var files = Files();
    var archive = Write(files, compression, Password);
    using (var r = new StuffItReader(new MemoryStream(archive), Password)) {
      foreach (var f in files) {
        var e = r.Entries.Single(x => x.FileName == f.Path);
        Assert.That(e.IsEncrypted, Is.True);
        Assert.That(r.Extract(e), Is.EqualTo(f.Data), f.Path);
        if (f.Resource != null) Assert.That(r.ExtractResourceFork(e), Is.EqualTo(f.Resource), f.Path);
      }
    }
    using var wrong = new StuffItReader(new MemoryStream(archive), "wrong");
    Assert.Throws<ArchivePasswordException>(() => wrong.Extract(wrong.Entries.Single(x => x.FileName == "notes.txt")));
    // the stored payload must not appear in clear
    Assert.That(archive.AsSpan().IndexOf("encrypted stuffit fork"u8), Is.EqualTo(-1));
  }

  [Test, Category("Exception")]
  public void GivenClassicTargetWithPassword_WhenCreating_ThenRefused()
    => Assert.That(() => new StuffItFormatDescriptor().Create(new MemoryStream(), [new ArchiveInputInfo("a", "a", false, [1])], new FormatCreateOptions { Password = Password }),
      Throws.InstanceOf<NotSupportedException>());

  // ── The Unarchiver ───────────────────────────────────────────────────────

  [TestCase(StuffItCompression.Smallest), TestCase(StuffItCompression.Store)]
  [Category("ExternalInterop")]
  public void GivenOurEncryptedArchive_WhenUnarExtractsWithThePassword_ThenEveryFileIsByteIdenticalAndAWrongPasswordFails(StuffItCompression compression) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var files = Files();
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sit5pw_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, "secret.sit");
      File.WriteAllBytes(path, Write(files, compression, Password));
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k visible -p {Password} -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdOut + run.StdErr);
      foreach (var f in files)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(f.Data), f.Path);

      var bad = FsInteropToolbox.RunWsl($"unar -q -f -p wrong -D -o '{FsInteropToolbox.WinToWsl(Path.Combine(tmp, "bad"))}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(bad.ExitCode, Is.Not.EqualTo(0), "unar must refuse the wrong password");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
