using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Aff4;

namespace Compression.Tests.Aff4;

/// <summary>
/// Our reader against logical images that pyaff4 wrote, checked against pyaff4's own read-back
/// (<c>ReferenceVectors/pyaff4-expected.tsv</c>): the same files, the same sizes, the same bytes.
/// Provenance in <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class Aff4ReferenceImageTests {
  private static readonly string[] Images = ["dream.aff4", "pyaff4-logical-snappy.aff4", "pyaff4-logical-zlib-stored.aff4"];

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Aff4Vectors.{name}")
      ?? throw new InvalidOperationException($"Embedded AFF4 vector '{name}' is missing.");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  /// <summary>pyaff4's rows for <paramref name="image"/>, its names mapped the way pyaff4 extracts them.</summary>
  private static List<(string Name, long Size, string Sha256)> Expected(string image)
    => [.. System.Text.Encoding.UTF8.GetString(Vector("pyaff4-expected.tsv"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.TrimEnd('\r').Split('\t')) // a Windows checkout may turn the LFs into CRLFs
      .Where(cols => cols[0] == image)
      .Select(cols => (Aff4OracleNames.Normalize(cols[1]), long.Parse(cols[2]), cols[3]))];

  [TestCaseSource(nameof(Images))]
  [Category("HappyPath")]
  public void List_GivenPyaff4Image_ThenFilesAndSizesMatchPyaff4(string image) {
    using var ms = new MemoryStream(Vector(image));
    var listed = new Aff4FormatDescriptor().List(ms, null).Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e.OriginalSize);
    foreach (var (name, size, _) in Expected(image)) {
      Assert.That(listed, Does.ContainKey(name), $"{image}: {name} not listed; got {string.Join(", ", listed.Keys)}");
      Assert.That(listed[name], Is.EqualTo(size), $"{image}: {name}");
    }
  }

  [TestCaseSource(nameof(Images))]
  [Category("HappyPath")]
  public void Extract_GivenPyaff4Image_ThenEveryFileIsByteIdenticalToPyaff4(string image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_aff4ref_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(Vector(image)))
        new Aff4FormatDescriptor().Extract(ms, dir, null, null);
      foreach (var (name, size, sha256) in Expected(image)) {
        var path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"{image}: {name} not extracted");
        var data = File.ReadAllBytes(path);
        Assert.That(data.LongLength, Is.EqualTo(size), $"{image}: {name}");
        Assert.That(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), Is.EqualTo(sha256), $"{image}: {name}");
      }
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void List_GivenImageStreamFile_ThenBevySegmentsAreNotListedAsMembers() {
    using var ms = new MemoryStream(Vector("pyaff4-logical-snappy.aff4"));
    var names = new Aff4FormatDescriptor().List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("evidence/deep/stream.bin"));
    Assert.That(names.Where(n => n.Contains("stream.bin/", StringComparison.Ordinal)), Is.Empty);
    Assert.That(names, Does.Contain("information.turtle"), "metadata members stay visible");
  }

  [Test]
  public void List_GivenPyaff4Folders_ThenTheyAreDirectories() {
    using var ms = new MemoryStream(Vector("pyaff4-logical-zlib-stored.aff4"));
    var directories = new Aff4FormatDescriptor().List(ms, null).Where(e => e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(directories, Is.SupersetOf(new[] { "emptydir", "evidence", "evidence/deep" }));
  }

  [Test]
  public void Extract_GivenFilterOnOneLogicalFile_ThenOnlyThatFileIsWritten() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_aff4ref_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(Vector("pyaff4-logical-snappy.aff4")))
        new Aff4FormatDescriptor().Extract(ms, dir, null, ["evidence/deep/stream.bin"]);
      var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
      Assert.That(files, Has.Length.EqualTo(1));
      Assert.That(new FileInfo(files[0]).Length, Is.EqualTo(103240));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}

/// <summary>How pyaff4 turns a logical name into an extraction path, which our reader matches.</summary>
internal static class Aff4OracleNames {
  public static string Normalize(string name) {
    var path = name.Replace('\\', '/');
    while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
    return path.Trim('/');
  }
}
