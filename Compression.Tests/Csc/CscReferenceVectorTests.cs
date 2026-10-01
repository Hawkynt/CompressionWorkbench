using System.Reflection;
using Compression.Core.Dictionary.Csc;

namespace Compression.Tests.Csc;

/// <summary>
/// Streams written by libcsc's own <c>csc</c> tool (see ReferenceVectors/README.md): we must decode
/// each to its input, and our encoder, given the same switches, must reproduce each byte for byte
/// apart from libcsc's uninitialised guard bytes.
/// </summary>
[TestFixture]
public sealed class CscReferenceVectorTests {
  private const uint Normal = 0x01, Text = 0x02, X86 = 0x03, Literals = 0x07, Stored = 0x08, EndOfStream = 0x09, Delta4 = 0x13;

  public sealed record Vector(string Name, Func<byte[]> Input, CscEncoderOptions Options, uint[] Blocks) {
    public override string ToString() => this.Name;
  }

  private static IEnumerable<Vector> Vectors() {
    yield return new("empty.m2", () => [], new(Level: 2), [EndOfStream]);
    yield return new("one.m2", () => [0x41], new(Level: 2), [Normal, EndOfStream]);
    for (var level = 1; level <= 5; ++level)
      yield return new($"text-20k.m{level}", () => CscSamples.Text(20000), new(Level: level), [Text, EndOfStream]);
    yield return new("x86-24k.m2", () => CscSamples.X86(24576), new(Level: 2), [X86, EndOfStream]);
    yield return new("table-64k.m2", () => CscSamples.Table(65536), new(Level: 2), [Delta4, EndOfStream]);
    yield return new("table-32k.m2.fdelta0", () => CscSamples.Table(32768), new(Level: 2, DeltaFilter: false), [Normal, EndOfStream]);
    yield return new("alphabet-16k.m2", () => CscSamples.SmallAlphabet(16384), new(Level: 2), [Literals, EndOfStream]);
    yield return new("mixed.m2", CscSamples.Mixed, new(Level: 2), [Text, Stored, Delta4, Literals, X86, EndOfStream]);
    yield return new("text-60k.m2.d32k", () => CscSamples.Text(60000, 5), new(Level: 2, DictionarySize: 32 * 1024), [Text, EndOfStream]);
    yield return new("text-60k.m5.d32k", () => CscSamples.Text(60000, 5), new(Level: 5, DictionarySize: 32 * 1024), [Text, EndOfStream]);
    yield return new("ramp-4500k.m2", () => CscSamples.Ramp(4_500_000), new(Level: 2), [Normal, Normal, Normal, EndOfStream]);
  }

  private static byte[] Load(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CscVectors.{name}.csc")
      ?? throw new FileNotFoundException($"Embedded CSC vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Vectors))]
  public void LibcscStream_DecodesToItsInput(Vector vector) {
    var blocks = new List<uint>();
    using var input = new MemoryStream(Load(vector.Name));
    using var output = new MemoryStream();

    CscCodec.Decompress(input, output, (type, _) => blocks.Add(type));

    Assert.Multiple(() => {
      Assert.That(output.ToArray(), Is.EqualTo(vector.Input()), "decoded bytes");
      Assert.That(blocks, Is.EqualTo(vector.Blocks), "block types prove which libcsc coder each stream exercises");
    });
  }

  [Test, Category("ThemVsUs"), TestCaseSource(nameof(Vectors))]
  public void OurEncoder_ReproducesLibcscStream(Vector vector) {
    var expected = Load(vector.Name);
    var actual = CscCodec.Compress(vector.Input(), vector.Options);

    Assert.That(CscGuardBytes.Mask(actual), Is.EqualTo(CscGuardBytes.Mask(expected)));
  }

  [Test, Category("Spec")]
  public void LibcscHeader_IsWindowPlus10KiB_64KiBBlocks_2MiBRawBlocks() {
    var properties = CscStreamProperties.Read(Load("mixed.m2"));

    Assert.That(properties, Is.EqualTo(new CscStreamProperties((uint)CscSamples.Mixed().Length + 10 * 1024, 64 * 1024, 2 * 1024 * 1024)));
  }
}
