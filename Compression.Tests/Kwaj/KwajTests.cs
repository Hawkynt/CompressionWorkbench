using FileFormat.Kwaj;

namespace Compression.Tests.Kwaj;

[TestFixture]
public sealed class KwajTests {
  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static byte[] Compress(byte[] data, int method = KwajConstants.MethodMsZip,
    string? filename = null) {
    using var input  = new MemoryStream(data);
    using var output = new MemoryStream();
    KwajStream.Compress(input, output, method, filename);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] kwajData) {
    using var input  = new MemoryStream(kwajData);
    using var output = new MemoryStream();
    KwajStream.Decompress(input, output);
    return output.ToArray();
  }

  // -------------------------------------------------------------------------
  // Round-trip tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Store() {
    var original = "Hello, KWAJ store method!"u8.ToArray();

    var compressed   = Compress(original, KwajConstants.MethodStore);
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Xor() {
    var original = "XOR obfuscation round-trip test data."u8.ToArray();

    var compressed   = Compress(original, KwajConstants.MethodXor);
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Mszip() {
    // Highly compressible data to exercise MSZIP code paths.
    var original = new byte[4096];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 13);

    var compressed   = Compress(original, KwajConstants.MethodMsZip);
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Mszip_Empty() {
    var original = Array.Empty<byte>();

    var compressed   = Compress(original, KwajConstants.MethodMsZip);
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Store_Binary() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i)
      original[i] = (byte)i;

    var compressed   = Compress(original, KwajConstants.MethodStore);
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Header structure tests
  // -------------------------------------------------------------------------

  [Category("ThemVsUs")]
  [Test]
  public void Compress_WritesCorrectMagic() {
    var data = Compress("test"u8.ToArray());

    // Bytes 0-3: "KWAJ"
    Assert.That(data[0], Is.EqualTo(0x4B));
    Assert.That(data[1], Is.EqualTo(0x57));
    Assert.That(data[2], Is.EqualTo(0x41));
    Assert.That(data[3], Is.EqualTo(0x4A));

    // Bytes 4-7: 0x88, 0xF0, 0x27, 0xD1
    Assert.That(data[4], Is.EqualTo(0x88));
    Assert.That(data[5], Is.EqualTo(0xF0));
    Assert.That(data[6], Is.EqualTo(0x27));
    Assert.That(data[7], Is.EqualTo(0xD1));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compress_MethodField_ReflectsChosenMethod() {
    var storeData  = Compress("a"u8.ToArray(), KwajConstants.MethodStore);
    var xorData    = Compress("a"u8.ToArray(), KwajConstants.MethodXor);
    var mszipData  = Compress("a"u8.ToArray(), KwajConstants.MethodMsZip);

    ushort storeMethod = (ushort)(storeData[8] | (storeData[9] << 8));
    ushort xorMethod   = (ushort)(xorData[8]   | (xorData[9]   << 8));
    ushort mszipMethod = (ushort)(mszipData[8]  | (mszipData[9] << 8));

    Assert.That(storeMethod, Is.EqualTo(KwajConstants.MethodStore));
    Assert.That(xorMethod,   Is.EqualTo(KwajConstants.MethodXor));
    Assert.That(mszipMethod, Is.EqualTo(KwajConstants.MethodMsZip));
  }

  // -------------------------------------------------------------------------
  // Original filename tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void GetOriginalFilename_ReturnsName() {
    var data = Compress("content"u8.ToArray(), KwajConstants.MethodStore, filename: "readme.txt");

    using var ms = new MemoryStream(data);
    var name = KwajStream.GetOriginalFilename(ms);

    Assert.That(name, Is.EqualTo("readme.txt"));
  }

  [Category("EdgeCase")]
  [Test]
  public void GetOriginalFilename_ReturnsNull_WhenAbsent() {
    var data = Compress("content"u8.ToArray(), KwajConstants.MethodStore, filename: null);

    using var ms = new MemoryStream(data);
    var name = KwajStream.GetOriginalFilename(ms);

    Assert.That(name, Is.Null);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithFilename_DataUnaffected() {
    var original = "Data with a filename in the header."u8.ToArray();

    var compressed   = Compress(original, KwajConstants.MethodMsZip, filename: "original.txt");
    var decompressed = Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Error-handling tests
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidMagic_Throws() {
    var bad = new byte[64];
    bad[0] = 0xFF; // Not "KWAJ..."

    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => {
      using var output = new MemoryStream();
      KwajStream.Decompress(ms, output);
    });
  }

  [Category("Exception")]
  [Test]
  public void Decompress_TruncatedHeader_Throws() {
    // Only 4 bytes — too short for the 14-byte fixed header.
    var bad = new byte[] { 0x4B, 0x57, 0x41, 0x4A };

    using var ms = new MemoryStream(bad);
    Assert.That(() => {
      using var output = new MemoryStream();
      KwajStream.Decompress(ms, output);
    }, Throws.Exception);
  }

  [Category("Exception")]
  [Test]
  public void Compress_UnsupportedMethod_Throws() {
    using var input  = new MemoryStream("data"u8.ToArray());
    using var output = new MemoryStream();

    Assert.Throws<ArgumentException>(() =>
      KwajStream.Compress(input, output, method: KwajConstants.MethodLzss));
  }

  [Category("Exception")]
  [Test]
  public void Compress_NullInput_Throws() =>
    Assert.Throws<ArgumentNullException>(() =>
      KwajStream.Compress(null!, new MemoryStream()));

  [Category("Exception")]
  [Test]
  public void Compress_NullOutput_Throws() =>
    Assert.Throws<ArgumentNullException>(() =>
      KwajStream.Compress(new MemoryStream(), null!));

  [Category("Exception")]
  [Test]
  public void Decompress_NullInput_Throws() =>
    Assert.Throws<ArgumentNullException>(() =>
      KwajStream.Decompress(null!, new MemoryStream()));

  [Category("Exception")]
  [Test]
  public void Decompress_NullOutput_Throws() =>
    Assert.Throws<ArgumentNullException>(() =>
      KwajStream.Decompress(new MemoryStream(), null!));
}
