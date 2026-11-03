using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Registry;
using FileFormat.Xar;

namespace Compression.Tests.Xar;

/// <summary>
/// XAR archives carry the SHA-1 TOC checksum xar and libarchive verify, keep folders and modes,
/// and stay valid through in-place edits — of our archives and of ones bsdtar wrote.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 91cf0b2, 6b43ce6, 0da99c9.</remarks>
[TestFixture]
public sealed class XarFolderAndChecksumTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_xarfold_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static MemoryStream Create(params ArchiveInputInfo[] inputs) {
    var stream = new MemoryStream();
    new XarFormatDescriptor().Create(stream, inputs, new FormatCreateOptions());
    stream.Position = 0;
    return stream;
  }

  private static string[] Names(Stream xar) {
    xar.Position = 0;
    return [.. new XarFormatDescriptor().List(xar, null).Select(e => e.Name).Order(StringComparer.Ordinal)];
  }

  /// <summary>True when the 20 bytes at heap offset 0 are the SHA-1 of the compressed TOC.</summary>
  private static bool TocChecksumIsValid(byte[] xar) {
    var tocLength = (int)BinaryPrimitives.ReadUInt64BigEndian(xar.AsSpan(8));
    var toc = xar.AsSpan(28, tocLength);
    return xar.AsSpan(28 + tocLength, 20).SequenceEqual(Sha1.Compute(toc));
  }

  [Test, Category("HappyPath")]
  public void GivenFoldersAndFiles_WhenCreated_ThenTheTreeAndTheChecksumAreThere() {
    using var xar = Create(
      new ArchiveInputInfo("", "pack/sub/empty/", true),
      ArchiveInputInfo.InMemory("pack/sub/b.txt", "deep"u8),
      ArchiveInputInfo.InMemory("top.txt", "top"u8));

    Assert.Multiple(() => {
      Assert.That(Names(xar), Is.EqualTo(new[] { "pack", "pack/sub", "pack/sub/b.txt", "pack/sub/empty", "top.txt" }));
      Assert.That(TocChecksumIsValid(xar.ToArray()), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnArchive_WhenFilesAndFoldersAreAddedAndRemoved_ThenTheTreeFollowsAndTheChecksumStaysValid() {
    using var xar = Create(
      new ArchiveInputInfo("", "pack/sub/empty/", true),
      ArchiveInputInfo.InMemory("pack/sub/b.txt", "deep"u8),
      ArchiveInputInfo.InMemory("top.txt", "top"u8));
    var d = new XarFormatDescriptor();

    d.Add(xar, [new ArchiveInputInfo("", "later/deeper/", true), ArchiveInputInfo.InMemory("pack/sub/added.txt", "added"u8), ArchiveInputInfo.InMemory("root2.txt", "r2"u8)]);
    d.Remove(xar, ["top.txt", "pack/sub/empty"]);

    Assert.Multiple(() => {
      Assert.That(Names(xar), Is.EqualTo(new[] { "later", "later/deeper", "pack", "pack/sub", "pack/sub/added.txt", "pack/sub/b.txt", "root2.txt" }));
      Assert.That(TocChecksumIsValid(xar.ToArray()), Is.True);
    });
    var outDir = Path.Combine(this._dir, "x");
    xar.Position = 0;
    d.Extract(xar, outDir, null, null);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pack", "sub", "b.txt")), Is.EqualTo("deep"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pack", "sub", "added.txt")), Is.EqualTo("added"));
      Assert.That(Directory.Exists(Path.Combine(outDir, "later", "deeper")), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenRemoved_ThenEverythingInItGoesAndItsDataIsWiped() {
    var secret = "secret payload that must not survive"u8.ToArray();
    using var xar = Create(ArchiveInputInfo.InMemory("pack/a.txt", secret), ArchiveInputInfo.InMemory("keep.txt", "k"u8));

    new XarFormatDescriptor().Remove(xar, ["pack"]);

    Assert.Multiple(() => {
      Assert.That(Names(xar), Is.EqualTo(new[] { "keep.txt" }));
      Assert.That(TocChecksumIsValid(xar.ToArray()), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenAFileWhereAFolderIsWanted_WhenSomethingIsAddedBelowIt_ThenTheEditIsRefused() {
    using var xar = Create(ArchiveInputInfo.InMemory("name", "file"u8));
    var before = xar.ToArray();

    Assert.Throws<IOException>(() => new XarFormatDescriptor().Add(xar, [ArchiveInputInfo.InMemory("name/inner.txt", "x"u8)]));
    Assert.That(xar.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("EdgeCase")]
  public void GivenANameThatIsNotThere_WhenRemoved_ThenTheArchiveIsUnchanged() {
    using var xar = Create(ArchiveInputInfo.InMemory("pack/a.txt", "a"u8));
    var before = xar.ToArray();

    new XarFormatDescriptor().Remove(xar, ["a.txt"]); // only the leaf of pack/a.txt

    Assert.That(xar.ToArray(), Is.EqualTo(before));
  }

  // ── oracle ───────────────────────────────────────────────────────────────────────────────

  private static void RequireBsdtar() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("bsdtar"))
      Assert.Ignore("bsdtar (libarchive-tools) in WSL is needed for this oracle.");
  }

  [Test, Category("ExternalInterop")]
  public void GivenOurEditedArchive_WhenBsdtarListsAndExtractsIt_ThenTheChecksumAndTheTreeAreAccepted() {
    RequireBsdtar();
    var path = Path.Combine(this._dir, "ours.xar");
    using (var xar = Create(new ArchiveInputInfo("", "pack/sub/empty/", true), ArchiveInputInfo.InMemory("pack/sub/b.txt", "deep"u8), ArchiveInputInfo.InMemory("top.txt", "top"u8))) {
      var d = new XarFormatDescriptor();
      d.Add(xar, [ArchiveInputInfo.InMemory("pack/sub/added.txt", "added"u8)]);
      d.Remove(xar, ["top.txt"]);
      File.WriteAllBytes(path, xar.ToArray());
    }
    var outDir = Directory.CreateDirectory(Path.Combine(this._dir, "bsdtar-out")).FullName;

    var list = FsInteropToolbox.RunWsl($"bsdtar -tvf {FsInteropToolbox.WinToWsl(path)}");
    var extract = FsInteropToolbox.RunWsl($"bsdtar -xf {FsInteropToolbox.WinToWsl(path)} -C {FsInteropToolbox.WinToWsl(outDir)}");

    Assert.Multiple(() => {
      Assert.That(list.ExitCode, Is.Zero, list.StdErr);
      Assert.That(list.StdOut, Does.Contain("pack/sub/added.txt"));
      Assert.That(list.StdOut, Does.Match(@"drwxr-xr-x .*pack/sub/empty"));
      Assert.That(extract.ExitCode, Is.Zero, extract.StdErr);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pack", "sub", "added.txt")), Is.EqualTo("added"));
      Assert.That(File.Exists(Path.Combine(outDir, "top.txt")), Is.False);
    });
  }

  [Test, Category("ExternalInterop")]
  public void GivenAnArchiveBsdtarWrote_WhenWeEditItInPlace_ThenBsdtarStillAcceptsIt() {
    RequireBsdtar();
    var tree = Directory.CreateDirectory(Path.Combine(this._dir, "tree", "docs", "deep")).FullName;
    File.WriteAllText(Path.Combine(tree, "guide.txt"), "guide");
    File.WriteAllText(Path.Combine(this._dir, "tree", "readme.txt"), "readme");
    var path = Path.Combine(this._dir, "theirs.xar");
    var made = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(Path.Combine(this._dir, "tree"))} && bsdtar -cf {FsInteropToolbox.WinToWsl(path)} --format xar docs readme.txt");
    Assert.That(made.ExitCode, Is.Zero, made.StdErr);

    using (var xar = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new XarFormatDescriptor();
      d.Add(xar, [ArchiveInputInfo.InMemory("docs/deep/new.txt", "new"u8)]);
      d.Remove(xar, ["readme.txt"]);
    }
    var outDir = Directory.CreateDirectory(Path.Combine(this._dir, "bsdtar-out")).FullName;
    var extract = FsInteropToolbox.RunWsl($"bsdtar -xf {FsInteropToolbox.WinToWsl(path)} -C {FsInteropToolbox.WinToWsl(outDir)}");

    Assert.Multiple(() => {
      Assert.That(extract.ExitCode, Is.Zero, extract.StdErr);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "docs", "deep", "guide.txt")), Is.EqualTo("guide"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "docs", "deep", "new.txt")), Is.EqualTo("new"));
      Assert.That(File.Exists(Path.Combine(outDir, "readme.txt")), Is.False);
    });
  }
}
