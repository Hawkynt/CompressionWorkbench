using System.Reflection;
using Compression.Core.FileSystems.Cvf;

namespace Compression.Tests.Cvf;

/// <summary>
/// Cluster payloads the dmsdos compressors wrote (see ReferenceVectors/README.md) for every CVF method:
/// DS-0-0, JM-0-0, JM-0-1 (DriveSpace 3 "High"), SQ-0-0 (DriveSpace 3 "Ultra"), Stacker SD-3 and SD-4.
/// Each must decode to the sample it was made from — through the same dispatch the CVF readers use.
/// </summary>
[TestFixture]
public sealed class CvfReferenceVectorTests {

  public sealed record Vector(string Sample, string Method) {
    public string Name => $"{this.Sample}.{this.Method}";
    public bool IsStacker => this.Method is "sd3" or "sd4";
    public override string ToString() => this.Name;
  }

  private static IEnumerable<Vector> Vectors() {
    foreach (var sample in CvfSamples.Names) {
      string[] methods = sample.EndsWith("-32k", StringComparison.Ordinal)
        ? ["jm00", "sq00"]
        : ["ds00", "jm00", "jm01", "sq00", "sd3", "sd4"];
      foreach (var method in methods)
        yield return new(sample, method);
    }
  }

  internal static byte[] Load(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CvfVectors.{name}.bin")
      ?? throw new FileNotFoundException($"Embedded CVF vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Vectors))]
  public void DmsdosCluster_DecodesToItsSample(Vector vector) {
    var payload = Load(vector.Name);
    var expected = CvfSamples.Get(vector.Sample);

    var actual = vector.IsStacker
      ? CvfLzCodec.DecompressStacker(payload, payload.Length, expected.Length)
      : CvfLzCodec.Decompress(payload, payload.Length, expected.Length);

    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Vectors))]
  public void DmsdosCluster_CarriesTheMethodHeaderItsFileNameClaims(Vector vector) {
    var payload = Load(vector.Name);
    var word = payload[0] | (payload[1] << 8);
    switch (vector.Method) {
      case "ds00": Assert.That(payload[..4], Is.EqualTo("DS\0\0"u8.ToArray())); break;
      case "jm00": Assert.That(payload[..4], Is.EqualTo("JM\0\0"u8.ToArray())); break;
      case "jm01": Assert.That(payload[..4], Is.EqualTo("JM\0\u0001"u8.ToArray())); break;
      case "sq00": Assert.That(payload[..4], Is.EqualTo("SQ\0\0"u8.ToArray())); break;
      case "sd4": Assert.That(word, Is.EqualTo(0x0081)); break;
      case "sd3": Assert.That(word, Is.Not.AnyOf(0x0081, 0x5344), "SD-3 has no header word"); break;
    }
  }

  [Test, Category("ThemVsUs")]
  public void DmsdosSd4Cluster_LargerThanTheData_LeavesTheTailZero() {
    var payload = Load("text-8k.sd4");
    var expected = CvfSamples.Get("text-8k");

    var actual = CvfLzCodec.DecompressStacker(payload, payload.Length, 16384);

    Assert.Multiple(() => {
      Assert.That(actual.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected));
      Assert.That(actual.AsSpan(expected.Length).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("ThemVsUs")]
  public void DmsdosSd3Cluster_WithACorruptedCheckByte_IsRejected() {
    var payload = Load("text-8k.sd3");
    var expected = CvfSamples.Get("text-8k");
    // The check byte follows the byte-aligned end marker; find it by decoding the token stream alone.
    var produced = Compression.Core.Dictionary.Lzs.LzsBuildingBlock.DecodeStream(payload, new byte[expected.Length], out var consumed);
    Assume.That(produced, Is.EqualTo(expected.Length));
    payload[consumed] ^= 0x01;

    Assert.That(() => CvfLzCodec.DecompressStacker(payload, payload.Length, expected.Length),
      Throws.InstanceOf<InvalidDataException>());
  }
}
