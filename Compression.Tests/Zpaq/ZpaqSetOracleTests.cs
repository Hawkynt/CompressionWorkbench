using System.Reflection;
using Compression.Registry;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// zpaq 7.15 itself (in WSL, or natively on Linux) must take what we write beyond plain archives:
/// encrypted archives and their appended versions, the next part of a plain or encrypted multi-part
/// set, the next part written from an index alone together with the updated index, and the index
/// of an encrypted set, which must be the one zpaq writes. Without zpaq every test is ignored.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class ZpaqSetOracleTests {
  private const string Password = "pässwört 1";
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("zpaq"))
      Assert.Ignore("zpaq is not installed (apt install zpaq).");
    this._dir = Directory.CreateTempSubdirectory("cwb-zpaq-oracle-").FullName;
  }

  [TearDown]
  public void TearDown() {
    if (this._dir != null && Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private static byte[] Load(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZpaqSetVectors.{name}")!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private string Place(params string[] names) {
    foreach (var n in names)
      File.WriteAllBytes(Path.Combine(this._dir, n), Load(n));
    return Path.Combine(this._dir, names[0]);
  }

  /// <summary>Runs zpaq in the work directory; returns its output, failing on a non-zero exit.</summary>
  private string Zpaq(string arguments) {
    var (stdout, stderr, code) = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(this._dir)} && zpaq {arguments}");
    Assert.That(code, Is.Zero, $"zpaq {arguments}\n{stdout}{stderr}");
    return stdout + stderr;
  }

  private string Extracted(string name) => Path.Combine(this._dir, "out", name.Replace('/', Path.DirectorySeparatorChar));

  [Test]
  public void OurEncryptedArchive_ZpaqExtractsIt() {
    using (var file = File.Create(Path.Combine(this._dir, "ours.zpaq")))
      new ZpaqFormatDescriptor().Create(file, [
        ArchiveInputInfo.InMemory("d/a.txt", "first file, encrypted"u8.ToArray()),
        ArchiveInputInfo.InMemory("d/b.bin", [.. Enumerable.Range(0, 70000).Select(i => (byte)(i * 7 >> 3))]),
      ], new FormatCreateOptions { Password = Password });
    this.Zpaq($"x ours.zpaq -key '{Password}' -to out");
    Assert.That(File.ReadAllBytes(this.Extracted("d/a.txt")), Is.EqualTo("first file, encrypted"u8.ToArray()));
    Assert.That(File.ReadAllBytes(this.Extracted("d/b.bin")).Length, Is.EqualTo(70000));
  }

  [Test]
  public void OurVersionAppendedToZpaqsEncryptedArchive_ZpaqExtractsIt() {
    var path = this.Place("enc.zpaq");
    using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Add(file, [ArchiveInputInfo.InMemory("v/appended.txt", "appended by us"u8.ToArray())],
        new ArchiveMutationOptions { Password = Password });
    var listing = this.Zpaq($"l enc.zpaq -key '{Password}' -all");
    Assert.That(listing, Does.Contain("v/appended.txt"));
    this.Zpaq($"x enc.zpaq -key '{Password}' -to out");
    Assert.That(File.ReadAllBytes(this.Extracted("v/appended.txt")), Is.EqualTo("appended by us"u8.ToArray()));
  }

  [TestCase("part", false)]
  [TestCase("ep", true)]
  public void OurNextPart_ZpaqReadsTheWholeSet(string prefix, bool encrypted) {
    var first = this.Place($"{prefix}001.zpaq", $"{prefix}002.zpaq");
    using (var file = new FileStream(first, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Add(file, [ArchiveInputInfo.InMemory("v/next.txt", "in the next part"u8.ToArray())],
        new ArchiveMutationOptions { Password = encrypted ? Password : null });
    Assert.That(File.Exists(Path.Combine(this._dir, $"{prefix}003.zpaq")));
    var key = encrypted ? $" -key '{Password}'" : "";
    var listing = this.Zpaq($"l '{prefix}???.zpaq'{key} -all");
    Assert.That(listing, Does.Contain("v/next.txt"));
    this.Zpaq($"x '{prefix}???.zpaq'{key} -to out");
    Assert.That(File.ReadAllBytes(this.Extracted("v/next.txt")), Is.EqualTo("in the next part"u8.ToArray()));
  }

  [Test]
  public void OurPartFromAnIndex_ZpaqReadsTheSetAndAddsToOurIndex() {
    var index = this.Place("ix000.zpaq");
    using (var file = new FileStream(index, FileMode.Open, FileAccess.ReadWrite))
      new ZpaqFormatDescriptor().Add(file, [ArchiveInputInfo.InMemory("v/remote.txt", "written from the index"u8.ToArray())]);
    // zpaq continues the set from our updated index alone...
    File.WriteAllText(Path.Combine(this._dir, "later.txt"), "added by zpaq");
    this.Zpaq("a 'ix???.zpaq' later.txt -index ix000.zpaq");
    Assert.That(File.Exists(Path.Combine(this._dir, "ix004.zpaq")));
    // ...and with every part present, extracts all of it.
    this.Place("ix001.zpaq", "ix002.zpaq");
    this.Zpaq("x 'ix???.zpaq' -to out");
    Assert.That(File.ReadAllBytes(this.Extracted("v/remote.txt")), Is.EqualTo("written from the index"u8.ToArray()));
    Assert.That(File.ReadAllText(this.Extracted("later.txt")), Is.EqualTo("added by zpaq"));
  }

  [Test]
  public void OurIndexOfAnEncryptedSet_IsTheOneZpaqWrites() {
    var first = this.Place("ep001.zpaq", "ep002.zpaq");
    using (var file = File.OpenRead(first))
    using (var index = File.Create(Path.Combine(this._dir, "ours000.zpaq")))
      ZpaqIndex.Write(file, index, Password);
    this.Zpaq($"x 'ep???.zpaq' -key '{Password}' -index theirs000.zpaq");
    Assert.That(File.ReadAllBytes(Path.Combine(this._dir, "ours000.zpaq")),
      Is.EqualTo(File.ReadAllBytes(Path.Combine(this._dir, "theirs000.zpaq"))));
  }
}
