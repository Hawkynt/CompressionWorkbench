using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DriveSpace;

[TestFixture]
public class DriveSpaceSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesMethodSchema() {
    var d = new DriveSpaceFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    var method = schema.FirstOrDefault(o => o.Key == "Method");
    Assert.That(method, Is.Not.Null);
    Assert.That(method!.AllowedValues, Is.EquivalentTo(new[] { "stored", "ds-lz77", "ds-lz77+", "ds-lz77++" }));
  }

  /// <summary>Long, very compressible payload spanning several 4 KiB CVF clusters.</summary>
  private static byte[] CompressiblePayload() {
    var phrase = "DriveSpace LZ77 packs sectors in 4 KiB chunks; repeated phrases compress hard. ";
    var sb = new StringBuilder(phrase.Length * 400);
    for (var i = 0; i < 400; ++i) sb.Append(phrase);
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] CreateCvf(string method, byte[] data) {
    var d = new DriveSpaceFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(
      ms, [ArchiveInputInfo.InMemory("DATA.BIN", data)],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["Method"] = method },
      });
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Create_Method_DrivesCodecAndRoundTrips() {
    var payload = CompressiblePayload();
    var stored = CreateCvf("stored", payload);
    var lz = CreateCvf("ds-lz77", payload);

    // The CVF writer pre-allocates a fixed-size DATA window, so total image
    // length does not track compression; what changes is the on-disk encoding
    // of the DATA region (stored runs vs compressed runs). The schema knob
    // genuinely reaches the codec, so the two images must differ byte-for-byte.
    Assert.That(lz, Is.Not.EqualTo(stored), "Method knob did not change the on-disk encoding");

    // ds-lz77 uses fewer physical sectors for compressible data: with a fixed
    // window the trailing sectors stay zero, so the LZ image has a longer
    // all-zero tail than the stored image.
    Assert.That(TrailingZeros(lz), Is.GreaterThan(TrailingZeros(stored)),
      "ds-lz77 did not pack the compressible payload into fewer sectors than stored");

    // Both encodings recover the exact bytes.
    foreach (var cvf in new[] { stored, lz }) {
      using var r = new DoubleSpaceReader(new MemoryStream(cvf));
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
    }
  }

  private static int TrailingZeros(byte[] data) {
    var n = 0;
    for (var i = data.Length - 1; i >= 0 && data[i] == 0; i--) n++;
    return n;
  }
}
