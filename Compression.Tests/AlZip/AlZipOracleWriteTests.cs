using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.AlZip;
using FileFormat.Egg;

namespace Compression.Tests.AlZip;

/// <summary>
/// ALZip itself (ALZipCon.exe of ALZip 12.38, under wine; see <c>alzipcon-extract.sh</c>) extracts the
/// ALZ and EGG archives and split sets we write, to the bytes we put in.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class AlZipOracleWriteTests {

  private static string Script() => FsInteropToolbox.WinToWsl(Path.Combine(
    Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "AlZip", "alzipcon-extract.sh"));

  private static readonly Lazy<bool> Available = new(() =>
    (FsInteropToolbox.WslAvailable || !OperatingSystem.IsWindows()) && FsInteropToolbox.RunWsl($"bash {Script()} --check").ExitCode == 0);

  private static List<(string Name, byte[] Data)> Inputs() {
    var r = new Random(948);
    string[] words = ["alpha", "beta", "gamma", "archive", "volume", "split", "egg", "alz", "member", "block"];
    var text = new System.Text.StringBuilder();
    while (text.Length < 150000) text.Append(words[r.Next(words.Length)]).Append(r.Next(9) == 0 ? '\n' : ' ');
    var noise = new byte[70000];
    r.NextBytes(noise);
    return [
      ("text.txt", System.Text.Encoding.ASCII.GetBytes(text.ToString())),
      ("sub/noise.bin", noise),
      ("small.txt", "hello from our writer\n"u8.ToArray()),
    ];
  }

  /// <summary>Writes through the descriptor into a fresh directory; returns the directory.</summary>
  private static string Write(IArchiveCreatable descriptor, string firstVolume, FormatCreateOptions options, List<(string Name, byte[] Data)> files) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-alzw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    using (var fs = File.Create(Path.Combine(dir, firstVolume)))
      descriptor.Create(fs, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], options);
    return dir;
  }

  private static void AssertAlzipConExtracts(string dir, string firstVolume, List<(string Name, byte[] Data)> files, string? password = null) {
    var run = FsInteropToolbox.RunWsl($"bash {Script()} {FsInteropToolbox.WinToWsl(dir)} {firstVolume}" + (password != null ? $" {password}" : ""));
    Assert.That(run.ExitCode, Is.Zero, run.StdOut + run.StdErr);
    // ALZipCon puts everything under a folder named after the archive
    var got = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  ./", 2))
      .ToDictionary(p => p[1][(p[1].IndexOf('/') + 1)..], p => p[0], StringComparer.Ordinal);
    Assert.That(got.Keys, Is.EquivalentTo(files.Select(f => f.Name)), run.StdOut);
    foreach (var (name, data) in files)
      Assert.That(got[name], Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(data))), name);
  }

  private static FormatCreateOptions Options(string? method, string? volumes, string? password = null, string? cipher = null) {
    var o = new FormatCreateOptions(method) { Password = password, EncryptionMethod = cipher };
    if (volumes != null) o.FormatSpecific[FormatOptionKeys.VolumeSize] = volumes;
    return o;
  }

  [TestCase("deflate", "64k", 2)]
  [TestCase("store", "64k", 4)]
  [TestCase("deflate", null, 1)]
  public void GivenOurAlz_WhenAlzipConExtractsIt_ThenEveryFileComesBack(string method, string? volumes, int expectedVolumes) {
    if (!Available.Value) Assert.Ignore("ALZip is not installed in its wine prefix (see alzipcon-extract.sh).");
    var files = Inputs();
    var dir = Write(new AlZipFormatDescriptor(), "ours.alz", Options(method, volumes), files);
    try {
      Assert.That(Directory.GetFiles(dir), Has.Length.EqualTo(expectedVolumes));
      AssertAlzipConExtracts(dir, "ours.alz", files);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void GivenOurAlzBzip2_WhenAlzipConExtractsIt_ThenEveryFileComesBack() {
    if (!Available.Value) Assert.Ignore("ALZip is not installed in its wine prefix (see alzipcon-extract.sh).");
    var files = Inputs();
    var dir = Write(new AlZipFormatDescriptor(), "ours.alz", Options("bzip2", null), files);
    try {
      using (var r = AlZipReader.Open(File.OpenRead(Path.Combine(dir, "ours.alz"))))
        Assert.That(r.Entries.Single(e => e.FileName == "text.txt").Method, Is.EqualTo(1), "written as ALZ bzip2");
      AssertAlzipConExtracts(dir, "ours.alz", files);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  /// <summary>ALZipCon cut short the member after an ALZ bzip2 member in a split set, so we refuse to write one.</summary>
  [Test]
  public void GivenBzip2AndAVolumeSize_WhenAnAlzIsCreated_ThenItIsRefused() =>
    Assert.Throws<NotSupportedException>(() =>
      new AlZipFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1, 2, 3])], Options("bzip2", "64k")));

  [Test]
  public void GivenOurEncryptedSplitAlz_WhenAlzipConExtractsItWithThePassword_ThenEveryFileComesBack() {
    if (!Available.Value) Assert.Ignore("ALZip is not installed in its wine prefix (see alzipcon-extract.sh).");
    var files = Inputs();
    var dir = Write(new AlZipFormatDescriptor(), "ours.alz", Options("deflate", "64k", "secret42"), files);
    try {
      AssertAlzipConExtracts(dir, "ours.alz", files, "secret42");
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase("bzip2", null)]
  [TestCase("lzma", null)]
  [TestCase("lzma", "zip20")]
  [TestCase("deflate", "zip20")]
  [TestCase("deflate", "aes128")]
  [TestCase("bzip2", "aes256")]
  [TestCase("deflate", "lea128")]
  [TestCase("lzma", "lea256")]
  public void GivenOurEggWithAMethodOrCipher_WhenAlzipConExtractsIt_ThenEveryFileComesBack(string method, string? cipher) {
    if (!Available.Value) Assert.Ignore("ALZip is not installed in its wine prefix (see alzipcon-extract.sh).");
    var files = Inputs();
    var options = Options(method, cipher != null ? "64k" : null, cipher != null ? "secret42" : null, cipher);
    var first = cipher != null ? "ours.vol1.egg" : "ours.egg";
    var dir = Write(new EggFormatDescriptor(), first, options, files);
    try {
      AssertAlzipConExtracts(dir, first, files, cipher != null ? "secret42" : null);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase("deflate", "64k", 2)]
  [TestCase("store", "64k", 4)]
  [TestCase("deflate", null, 1)]
  public void GivenOurEgg_WhenAlzipConExtractsIt_ThenEveryFileComesBack(string method, string? volumes, int expectedVolumes) {
    if (!Available.Value) Assert.Ignore("ALZip is not installed in its wine prefix (see alzipcon-extract.sh).");
    var files = Inputs();
    var first = volumes != null ? "ours.vol1.egg" : "ours.egg";
    var dir = Write(new EggFormatDescriptor(), first, Options(method, volumes), files);
    try {
      Assert.That(Directory.GetFiles(dir), Has.Length.EqualTo(expectedVolumes));
      AssertAlzipConExtracts(dir, first, files);
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
