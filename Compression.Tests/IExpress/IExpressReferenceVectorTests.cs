using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Lib;
using FileFormat.IExpress;

namespace Compression.Tests.IExpress;

/// <summary>
/// A package built by Windows iexpress.exe (see ReferenceVectors/README.md), checked against what
/// Windows expand.exe extracts from its CABINET resource (oracle.tsv).
/// </summary>
[TestFixture]
public class IExpressReferenceVectorTests {

  private const string Fixture = "iexpress-lzx.exe";

  private static byte[] Load() {
    using var resource = typeof(IExpressReferenceVectorTests).Assembly.GetManifestResourceStream("IExpressVectors." + Fixture + ".gz")!;
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle() {
    using var resource = typeof(IExpressReferenceVectorTests).Assembly.GetManifestResourceStream("IExpressVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>();
    while (reader.ReadLine() is { } line)
      if (line.Length > 0 && !line.StartsWith('#') && line.Split('\t') is var f && f[0] == Fixture)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    return result;
  }

  [Test]
  public void GivenIExpressPackage_WhenListed_ThenTheCabinetFilesMatchExpand() {
    var oracle = Oracle();
    var entries = new IExpressFormatDescriptor().List(new MemoryStream(Load()), null).Where(e => e.Kind != "metadata").ToList();
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [Test]
  public void GivenIExpressPackage_WhenExtracted_ThenEveryFileMatchesExpand() {
    var oracle = Oracle();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-iex-" + Guid.NewGuid().ToString("N"));
    try {
      new IExpressFormatDescriptor().Extract(new MemoryStream(Load()), dir, null, null);
      foreach (var (name, (_, sha)) in oracle)
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))), Is.EqualTo(sha), name);
      var ini = File.ReadAllText(Path.Combine(dir, "iexpress.ini"), Encoding.UTF8);
      Assert.That(ini, Does.Contain("title=Fixture").And.Contain("parse_status=ok"));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenIExpressPackageOnDisk_WhenDetected_ThenIExpressIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-iex-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, Load());
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.IExpress));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenPeWithoutCabinet_WhenProbed_ThenItIsNotIExpress() {
    var data = Load();
    // Rename the resource: "CABINET" (UTF-16) becomes "CABINEX".
    var name = Encoding.Unicode.GetBytes("CABINET");
    var at = data.AsSpan().IndexOf(name);
    Assert.That(at, Is.GreaterThan(0));
    data[at + 12] = (byte)'X';
    Assert.That(IExpressFormatDescriptor.IsIExpress(new MemoryStream(data)), Is.False);
    var entries = new IExpressFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "iexpress.ini" }));
  }

  [Test]
  public void GivenNonPeBytes_WhenProbed_ThenItIsNotIExpress()
    => Assert.That(IExpressFormatDescriptor.IsIExpress(new MemoryStream(new byte[100])), Is.False);
}
