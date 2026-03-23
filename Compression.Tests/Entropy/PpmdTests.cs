using Compression.Core.Entropy.Ppmd;

namespace Compression.Tests.Entropy;

[TestFixture]
public class PpmdTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_SingleByte() {
    byte[] data = [42];
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_TextData() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello, World! This is a test of PPMd compression.");
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_RepetitiveData() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 4);
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_RandomData() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_LargeData() {
    var data = new byte[10240];
    var rng = new Random(123);
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)rng.Next(0, 64); // limited alphabet
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_DifferentOrders() {
    var data = System.Text.Encoding.UTF8.GetBytes("ABCABCABCABCABC");
    foreach (var order in new[] { 2, 4, 6, 8 }) {
      var result = CompressDecompressH(data, order);
      Assert.That(result, Is.EqualTo(data), $"Failed at order {order}");
    }
  }

  [Category("HappyPath")]
  [Test]
  public void ModelH_CompressesWellOnText() {
    var data = System.Text.Encoding.UTF8.GetBytes(
      string.Concat(Enumerable.Repeat(
        "The quick brown fox jumps over the lazy dog. ", 20)));

    var ms = new MemoryStream();
    var model = new PpmdModelH(6, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(ms);
    foreach (var b in data)
      model.EncodeSymbol(encoder, b);
    encoder.Finish();

    var ratio = (double)ms.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.5));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_EmptyData() {
    byte[] data = [];
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_AllBytesOnce() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_AllSameBytes() {
    var data = new byte[200];
    Array.Fill(data, (byte)0xAA);
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelH_RoundTrip_TwoByteAlternating() {
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0x10 : 0x20);
    var result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void ModelI_RoundTrip_SingleByte() {
    byte[] data = [99];
    var result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelI_RoundTrip_TextData() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello, World! PPMd Model I test.");
    var result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelI_RoundTrip_RepetitiveData() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 4);
    var result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ModelI_RoundTrip_RandomData() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void ModelI_RoundTrip_EmptyData() {
    byte[] data = [];
    var result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_AllocFree_Works() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset1 = alloc.AllocUnits(1);
    var offset2 = alloc.AllocUnits(1);
    Assert.That(offset1, Is.Not.EqualTo(offset2));
    Assert.That(offset1, Is.GreaterThanOrEqualTo(0));
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
    alloc.FreeUnits(offset1, 1);
    var offset3 = alloc.AllocUnits(1);
    Assert.That(offset3, Is.GreaterThanOrEqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_Reset_Works() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset1 = alloc.AllocUnits(10);
    Assert.That(offset1, Is.GreaterThanOrEqualTo(0));
    alloc.Reset();
    var offset2 = alloc.AllocUnits(10);
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_AllocContext_ReturnsValidOffset() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocContext();
    Assert.That(offset, Is.GreaterThanOrEqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_GetSetByte_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocUnits(1);
    alloc.SetByte(offset, 0xAB);
    Assert.That(alloc.GetByte(offset), Is.EqualTo(0xAB));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_GetSetInt_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocUnits(1);
    alloc.SetInt(offset, 0x12345678);
    Assert.That(alloc.GetInt(offset), Is.EqualTo(0x12345678));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_GetSetUShort_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocUnits(1);
    alloc.SetUShort(offset, 0xABCD);
    Assert.That(alloc.GetUShort(offset), Is.EqualTo(0xABCD));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_ShrinkUnits_FreesExcess() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocUnits(4);
    Assert.That(offset, Is.GreaterThanOrEqualTo(0));
    alloc.ShrinkUnits(offset, 4, 2);
    // The freed 2 units should now be reusable
    var offset2 = alloc.AllocUnits(2);
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_MultipleAllocationSizes() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var o1 = alloc.AllocUnits(1);
    var o2 = alloc.AllocUnits(3);
    var o3 = alloc.AllocUnits(5);
    Assert.That(o1, Is.GreaterThanOrEqualTo(0));
    Assert.That(o2, Is.GreaterThanOrEqualTo(0));
    Assert.That(o3, Is.GreaterThanOrEqualTo(0));
    // All offsets should be different
    Assert.That(o1, Is.Not.EqualTo(o2));
    Assert.That(o2, Is.Not.EqualTo(o3));
    Assert.That(o1, Is.Not.EqualTo(o3));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_FreeAndReallocate_ReusesMemory() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset1 = alloc.AllocUnits(2);
    alloc.FreeUnits(offset1, 2);
    var offset2 = alloc.AllocUnits(2);
    // Should reuse the freed memory
    Assert.That(offset2, Is.EqualTo(offset1));
  }

  [Category("HappyPath")]
  [Test]
  public void SubAllocator_GetSpan_ReturnsCorrectMemory() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    var offset = alloc.AllocUnits(1);
    var span = alloc.GetSpan(offset, PpmdConstants.UnitSize);
    Assert.That(span.Length, Is.EqualTo(PpmdConstants.UnitSize));
    span[0] = 42;
    Assert.That(alloc.GetByte(offset), Is.EqualTo(42));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void PpmdRangeCoder_RoundTrip_SingleSymbol() {
    var ms = new MemoryStream();
    var encoder = new PpmdRangeEncoder(ms);
    encoder.Encode(5, 1, 10);
    encoder.Finish();

    var input = new MemoryStream(ms.ToArray());
    var decoder = new PpmdRangeDecoder(input);
    var threshold = decoder.GetThreshold(10);
    Assert.That(threshold, Is.EqualTo(5));
    decoder.Decode(5, 1, 10);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void PpmdRangeCoder_RoundTrip_MultipleSymbols() {
    // Encode a sequence of symbols with known frequencies
    // Alphabet: {A=0, B=1, C=2} with frequencies {5, 3, 2}, total=10
    int[] symbols = [0, 1, 2, 0, 0, 1, 2, 0, 1, 0];
    uint[] cumFreqs = [0, 5, 8]; // A:0-4, B:5-7, C:8-9
    uint[] freqs = [5, 3, 2];
    const uint total = 10;

    var ms = new MemoryStream();
    var encoder = new PpmdRangeEncoder(ms);
    foreach (var sym in symbols)
      encoder.Encode(cumFreqs[sym], freqs[sym], total);
    encoder.Finish();

    var input = new MemoryStream(ms.ToArray());
    var decoder = new PpmdRangeDecoder(input);
    var decoded = new int[symbols.Length];
    for (var i = 0; i < symbols.Length; ++i) {
      var threshold = decoder.GetThreshold(total);
      // Find symbol
      var s = 0;
      for (var j = 0; j < cumFreqs.Length; ++j) {
        if (threshold >= cumFreqs[j] && (j + 1 >= cumFreqs.Length || threshold < cumFreqs[j + 1])) {
          s = j;
          break;
        }
      }

      decoder.Decode(cumFreqs[s], freqs[s], total);
      decoded[i] = s;
    }

    Assert.That(decoded, Is.EqualTo(symbols));
  }

  private static byte[] CompressDecompressH(byte[] data, int order = PpmdConstants.DefaultOrder) {
    // Compress
    var compressedMs = new MemoryStream();
    // Write original length first (for decompression)
    compressedMs.Write(BitConverter.GetBytes(data.Length));

    var encModel = new PpmdModelH(order, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(compressedMs);
    foreach (var b in data)
      encModel.EncodeSymbol(encoder, b);
    encoder.Finish();

    // Decompress
    var inputMs = new MemoryStream(compressedMs.ToArray());
    var lenBytes = new byte[4];
    _ = inputMs.Read(lenBytes, 0, 4);
    var originalLen = BitConverter.ToInt32(lenBytes);

    var decModel = new PpmdModelH(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(inputMs);
    var result = new byte[originalLen];
    for (var i = 0; i < originalLen; ++i)
      result[i] = decModel.DecodeSymbol(decoder);

    return result;
  }

  private static byte[] CompressDecompressI(byte[] data, int order = PpmdConstants.DefaultOrder) {
    var compressedMs = new MemoryStream();
    compressedMs.Write(BitConverter.GetBytes(data.Length));

    var encModel = new PpmdModelI(order, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(compressedMs);
    foreach (var b in data)
      encModel.EncodeSymbol(encoder, b);
    encoder.Finish();

    var inputMs = new MemoryStream(compressedMs.ToArray());
    var lenBytes = new byte[4];
    _ = inputMs.Read(lenBytes, 0, 4);
    var originalLen = BitConverter.ToInt32(lenBytes);

    var decModel = new PpmdModelI(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(inputMs);
    var result = new byte[originalLen];
    for (var i = 0; i < originalLen; ++i)
      result[i] = decModel.DecodeSymbol(decoder);

    return result;
  }
}
