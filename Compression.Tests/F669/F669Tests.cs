#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.F669;

namespace Compression.Tests.F669;

[TestFixture]
public class F669Tests {

  private const int HeaderSize = 0x1F1;
  private const int SampleHeaderSize = 25;
  private const int PatternSize = 1536;

  // One sample (len 4), one pattern.
  private static byte[] Make669(out byte[] expectedPcm) {
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    expectedPcm = new byte[] { 128, 138, 118, 255 };

    const byte numSamples = 1;
    const byte numPatterns = 1;
    var total = HeaderSize + numSamples * SampleHeaderSize + numPatterns * PatternSize + signed.Length;
    var buf = new byte[total];

    buf[0] = 0x69; buf[1] = 0x66; // "if"
    buf[111] = numSamples;
    buf[112] = numPatterns;

    var shOff = HeaderSize;
    Encoding.ASCII.GetBytes("Kick").CopyTo(buf, shOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(shOff + 13), (uint)signed.Length);

    var patOff = HeaderSize + numSamples * SampleHeaderSize;
    buf[patOff] = 0x42; // some pattern data

    var dataOff = patOff + numPatterns * PatternSize;
    signed.CopyTo(buf, dataOff);
    return buf;
  }

  [Test]
  public void List_SurfacesFullMetadataPatternAndSample() {
    var blob = Make669(out _);
    using var ms = new MemoryStream(blob);
    var entries = new F669FormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.669").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin" && e.Kind == "Pattern"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/01_Kick.wav" && e.Kind == "Sample"), Is.True);
  }

  [Test]
  public void Sample_DecodesToRebiasedUnsigned8Wav() {
    var blob = Make669(out var expected);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new F669FormatDescriptor().ExtractEntry(ms, "samples/01_Kick.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void StructurallyInvalid_FallsBackToFullOnly() {
    // Valid magic but sample/pattern tables claim more than the file holds.
    var buf = new byte[HeaderSize];
    buf[0] = 0x69; buf[1] = 0x66;
    buf[111] = 10;  // claims 10 samples
    buf[112] = 10;  // claims 10 patterns → tables far exceed file
    using var ms = new MemoryStream(buf);
    var entries = new F669FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.669"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
    Assert.That(entries.Any(e => e.Kind == "Pattern"), Is.False);
  }
}
