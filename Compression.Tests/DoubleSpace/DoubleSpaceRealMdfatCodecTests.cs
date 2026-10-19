using Compression.Core.FileSystems.Cvf;
using Compression.Tests.Cvf;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// The general <see cref="DoubleSpaceReader"/> (the one the DriveSpace descriptor uses) on a real
/// MSDBL6.0 volume must decode compressed clusters by their method header — DS-0-x, JM-0-x, SQ-0-0 —
/// exactly as <see cref="GenuineCvfReader"/> does, instead of handing back the compressed sectors.
/// </summary>
[TestFixture]
public sealed class DoubleSpaceRealMdfatCodecTests {

  private static (string Name, byte[] Data)[] Files() => [
    ("TEXT.TXT", CvfSamples.Get("text-8k")),
    ("CODE.BIN", CvfSamples.Get("x86-8k")),
    ("DIR.DAT", CvfSamples.Get("dir-8k")[..5000]),
  ];

  [Test, Category("Codec")]
  public void CompressedClusters_DecodeByteExact([Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq, CvfLzMethod.Auto)] CvfLzMethod method) {
    var writer = new GenuineCvfWriter { CompressionMethod = method };
    foreach (var (name, data) in Files()) writer.AddFile(name, data);
    var image = writer.Build();

    var reader = new DoubleSpaceReader(new MemoryStream(image));

    Assert.Multiple(() => {
      foreach (var (name, data) in Files()) {
        var entry = reader.Entries.Single(e => e.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(reader.Extract(entry), Is.EqualTo(data), name);
      }
    });
  }
}
