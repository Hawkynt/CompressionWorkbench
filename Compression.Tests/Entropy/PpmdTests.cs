using Compression.Core.Entropy.Ppmd;

namespace Compression.Tests.Entropy;

[TestFixture]
public class PpmdTests {
  [Test]
  public void ModelH_RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_TextData() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes("Hello, World! This is a test of PPMd compression.");
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_RepetitiveData() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 4);
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_RandomData() {
    byte[] data = new byte[1024];
    new Random(42).NextBytes(data);
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_LargeData() {
    byte[] data = new byte[10240];
    var rng = new Random(123);
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)rng.Next(0, 64); // limited alphabet
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_DifferentOrders() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes("ABCABCABCABCABC");
    foreach (int order in new[] { 2, 4, 6, 8 }) {
      byte[] result = CompressDecompressH(data, order);
      Assert.That(result, Is.EqualTo(data), $"Failed at order {order}");
    }
  }

  [Test]
  public void ModelH_CompressesWellOnText() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes(
      string.Concat(Enumerable.Repeat(
        "The quick brown fox jumps over the lazy dog. ", 20)));

    var ms = new MemoryStream();
    var model = new PpmdModelH(6, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(ms);
    foreach (byte b in data)
      model.EncodeSymbol(encoder, b);
    encoder.Finish();

    double ratio = (double)ms.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.5));
  }

  [Test]
  public void ModelH_RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_AllBytesOnce() {
    byte[] data = new byte[256];
    for (int i = 0; i < 256; i++)
      data[i] = (byte)i;
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_AllSameBytes() {
    byte[] data = new byte[200];
    Array.Fill(data, (byte)0xAA);
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelH_RoundTrip_TwoByteAlternating() {
    byte[] data = new byte[300];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 2 == 0 ? 0x10 : 0x20);
    byte[] result = CompressDecompressH(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelI_RoundTrip_SingleByte() {
    byte[] data = [99];
    byte[] result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelI_RoundTrip_TextData() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes("Hello, World! PPMd Model I test.");
    byte[] result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelI_RoundTrip_RepetitiveData() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 4);
    byte[] result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelI_RoundTrip_RandomData() {
    byte[] data = new byte[1024];
    new Random(42).NextBytes(data);
    byte[] result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void ModelI_RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] result = CompressDecompressI(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void SubAllocator_AllocFree_Works() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset1 = alloc.AllocUnits(1);
    int offset2 = alloc.AllocUnits(1);
    Assert.That(offset1, Is.Not.EqualTo(offset2));
    Assert.That(offset1, Is.GreaterThanOrEqualTo(0));
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
    alloc.FreeUnits(offset1, 1);
    int offset3 = alloc.AllocUnits(1);
    Assert.That(offset3, Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void SubAllocator_Reset_Works() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset1 = alloc.AllocUnits(10);
    Assert.That(offset1, Is.GreaterThanOrEqualTo(0));
    alloc.Reset();
    int offset2 = alloc.AllocUnits(10);
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void SubAllocator_AllocContext_ReturnsValidOffset() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocContext();
    Assert.That(offset, Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void SubAllocator_GetSetByte_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocUnits(1);
    alloc.SetByte(offset, 0xAB);
    Assert.That(alloc.GetByte(offset), Is.EqualTo(0xAB));
  }

  [Test]
  public void SubAllocator_GetSetInt_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocUnits(1);
    alloc.SetInt(offset, 0x12345678);
    Assert.That(alloc.GetInt(offset), Is.EqualTo(0x12345678));
  }

  [Test]
  public void SubAllocator_GetSetUShort_RoundTrips() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocUnits(1);
    alloc.SetUShort(offset, 0xABCD);
    Assert.That(alloc.GetUShort(offset), Is.EqualTo(0xABCD));
  }

  [Test]
  public void SubAllocator_ShrinkUnits_FreesExcess() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocUnits(4);
    Assert.That(offset, Is.GreaterThanOrEqualTo(0));
    alloc.ShrinkUnits(offset, 4, 2);
    // The freed 2 units should now be reusable
    int offset2 = alloc.AllocUnits(2);
    Assert.That(offset2, Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void SubAllocator_MultipleAllocationSizes() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int o1 = alloc.AllocUnits(1);
    int o2 = alloc.AllocUnits(3);
    int o3 = alloc.AllocUnits(5);
    Assert.That(o1, Is.GreaterThanOrEqualTo(0));
    Assert.That(o2, Is.GreaterThanOrEqualTo(0));
    Assert.That(o3, Is.GreaterThanOrEqualTo(0));
    // All offsets should be different
    Assert.That(o1, Is.Not.EqualTo(o2));
    Assert.That(o2, Is.Not.EqualTo(o3));
    Assert.That(o1, Is.Not.EqualTo(o3));
  }

  [Test]
  public void SubAllocator_FreeAndReallocate_ReusesMemory() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset1 = alloc.AllocUnits(2);
    alloc.FreeUnits(offset1, 2);
    int offset2 = alloc.AllocUnits(2);
    // Should reuse the freed memory
    Assert.That(offset2, Is.EqualTo(offset1));
  }

  [Test]
  public void SubAllocator_GetSpan_ReturnsCorrectMemory() {
    var alloc = new SubAllocator(PpmdConstants.MinMemorySize);
    int offset = alloc.AllocUnits(1);
    var span = alloc.GetSpan(offset, PpmdConstants.UnitSize);
    Assert.That(span.Length, Is.EqualTo(PpmdConstants.UnitSize));
    span[0] = 42;
    Assert.That(alloc.GetByte(offset), Is.EqualTo(42));
  }

  [Test]
  public void PpmdRangeCoder_RoundTrip_SingleSymbol() {
    var ms = new MemoryStream();
    var encoder = new PpmdRangeEncoder(ms);
    encoder.Encode(5, 1, 10);
    encoder.Finish();

    var input = new MemoryStream(ms.ToArray());
    var decoder = new PpmdRangeDecoder(input);
    uint threshold = decoder.GetThreshold(10);
    Assert.That(threshold, Is.EqualTo(5));
    decoder.Decode(5, 1, 10);
  }

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
    foreach (int sym in symbols)
      encoder.Encode(cumFreqs[sym], freqs[sym], total);
    encoder.Finish();

    var input = new MemoryStream(ms.ToArray());
    var decoder = new PpmdRangeDecoder(input);
    int[] decoded = new int[symbols.Length];
    for (int i = 0; i < symbols.Length; i++) {
      uint threshold = decoder.GetThreshold(total);
      // Find symbol
      var s = 0;
      for (int j = 0; j < cumFreqs.Length; j++) {
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
    foreach (byte b in data)
      encModel.EncodeSymbol(encoder, b);
    encoder.Finish();

    // Decompress
    var inputMs = new MemoryStream(compressedMs.ToArray());
    byte[] lenBytes = new byte[4];
    _ = inputMs.Read(lenBytes, 0, 4);
    int originalLen = BitConverter.ToInt32(lenBytes);

    var decModel = new PpmdModelH(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(inputMs);
    byte[] result = new byte[originalLen];
    for (int i = 0; i < originalLen; i++)
      result[i] = decModel.DecodeSymbol(decoder);

    return result;
  }

  private static byte[] CompressDecompressI(byte[] data, int order = PpmdConstants.DefaultOrder) {
    var compressedMs = new MemoryStream();
    compressedMs.Write(BitConverter.GetBytes(data.Length));

    var encModel = new PpmdModelI(order, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(compressedMs);
    foreach (byte b in data)
      encModel.EncodeSymbol(encoder, b);
    encoder.Finish();

    var inputMs = new MemoryStream(compressedMs.ToArray());
    byte[] lenBytes = new byte[4];
    _ = inputMs.Read(lenBytes, 0, 4);
    int originalLen = BitConverter.ToInt32(lenBytes);

    var decModel = new PpmdModelI(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(inputMs);
    byte[] result = new byte[originalLen];
    for (int i = 0; i < originalLen; i++)
      result[i] = decModel.DecodeSymbol(decoder);

    return result;
  }
}
