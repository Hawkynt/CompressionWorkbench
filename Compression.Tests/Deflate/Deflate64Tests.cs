using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class Deflate64Tests {
  [Test]
  public void Deflate64Constants_DistanceBase_Has32Entries() {
    Assert.That(Deflate64Constants.DistanceBase.Length, Is.EqualTo(32));
  }

  [Test]
  public void Deflate64Constants_DistanceExtraBits_Has32Entries() {
    Assert.That(Deflate64Constants.DistanceExtraBits.Length, Is.EqualTo(32));
  }

  [Test]
  public void Deflate64Constants_LengthBase_Has29Entries() {
    Assert.That(Deflate64Constants.LengthBase.Length, Is.EqualTo(29));
  }

  [Test]
  public void Deflate64Constants_LengthExtraBits_Has29Entries() {
    Assert.That(Deflate64Constants.LengthExtraBits.Length, Is.EqualTo(29));
  }

  [Test]
  public void Deflate64Constants_Code285_Has16ExtraBits() {
    // Code 285 is index 28 (285-257)
    Assert.That(Deflate64Constants.LengthExtraBits[28], Is.EqualTo(16));
    Assert.That(Deflate64Constants.LengthBase[28], Is.EqualTo(3));
  }

  [Test]
  public void Deflate64Constants_WindowSize_Is64KB() {
    Assert.That(Deflate64Constants.WindowSize, Is.EqualTo(65536));
  }

  [Test]
  public void Deflate64Constants_DistanceCodes30And31() {
    // Code 30: base=32769, 14 extra bits
    Assert.That(Deflate64Constants.DistanceBase[30], Is.EqualTo(32769));
    Assert.That(Deflate64Constants.DistanceExtraBits[30], Is.EqualTo(14));

    // Code 31: base=49153, 14 extra bits
    Assert.That(Deflate64Constants.DistanceBase[31], Is.EqualTo(49153));
    Assert.That(Deflate64Constants.DistanceExtraBits[31], Is.EqualTo(14));
  }

  [Test]
  public void Deflate64Constants_MaxMatchLength() {
    Assert.That(Deflate64Constants.MaxMatchLength, Is.EqualTo(65538));
  }

  [Test]
  public void Deflate64Constants_DistanceAlphabetSize() {
    Assert.That(Deflate64Constants.DistanceAlphabetSize, Is.EqualTo(32));
  }

  [TestCase(1, 0)]
  [TestCase(4, 3)]
  [TestCase(5, 4)]
  [TestCase(24577, 29)]
  [TestCase(32768, 29)]
  [TestCase(32769, 30)]
  [TestCase(49152, 30)]
  [TestCase(49153, 31)]
  [TestCase(65536, 31)]
  public void GetDistanceCode_ReturnsCorrectCode(int distance, int expectedCode) {
    Assert.That(Deflate64Constants.GetDistanceCode(distance), Is.EqualTo(expectedCode));
  }

  [Test]
  public void GetDistanceCode_ThrowsForInvalidDistance() {
    Assert.Throws<ArgumentOutOfRangeException>(() => Deflate64Constants.GetDistanceCode(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => Deflate64Constants.GetDistanceCode(65537));
  }

  [Test]
  public void GetDistanceCode_RoundTripsWithBaseAndExtraBits() {
    ReadOnlySpan<int> bases = Deflate64Constants.DistanceBase;
    ReadOnlySpan<int> extras = Deflate64Constants.DistanceExtraBits;

    for (int distance = 1; distance <= 65536; distance++) {
      int code = Deflate64Constants.GetDistanceCode(distance);
      int extra = distance - bases[code];
      Assert.That(extra, Is.GreaterThanOrEqualTo(0), $"Distance {distance}: negative extra");
      Assert.That(extra, Is.LessThan(1 << extras[code]), $"Distance {distance}: extra out of range");
    }
  }

  [Test]
  public void DistanceTable_CoversAllDistances1To65536() {
    var reachable = new HashSet<int>();
    ReadOnlySpan<int> bases = Deflate64Constants.DistanceBase;
    ReadOnlySpan<int> extras = Deflate64Constants.DistanceExtraBits;

    for (int i = 0; i < bases.Length; i++) {
      var range = 1 << extras[i];
      for (int j = 0; j < range; j++)
        reachable.Add(bases[i] + j);
    }

    for (int dist = 1; dist <= 65536; dist++)
      Assert.That(reachable.Contains(dist), Is.True, $"Distance {dist} not reachable");
  }

  [Test]
  public void DistanceBase_IsMonotonicallyIncreasing() {
    ReadOnlySpan<int> bases = Deflate64Constants.DistanceBase;
    for (int i = 1; i < bases.Length; i++)
      Assert.That(bases[i], Is.GreaterThan(bases[i - 1]), $"DistanceBase[{i}] <= DistanceBase[{i - 1}]");
  }

  [Test]
  public void DistanceBase_First30MatchStandardDeflate() {
    // The first 30 distance codes must match standard Deflate exactly
    ReadOnlySpan<int> deflate64Bases = Deflate64Constants.DistanceBase;
    ReadOnlySpan<int> deflateBases = DeflateConstants.DistanceBase;

    for (int i = 0; i < 30; i++)
      Assert.That(deflate64Bases[i], Is.EqualTo(deflateBases[i]), $"DistanceBase[{i}] mismatch");
  }

  [Test]
  public void LengthBase_First28MatchStandardDeflate() {
    // Length codes 257-284 (indices 0-27) must match standard Deflate exactly
    ReadOnlySpan<int> deflate64Bases = Deflate64Constants.LengthBase;
    ReadOnlySpan<int> deflateBases = DeflateConstants.LengthBase;

    for (int i = 0; i < 28; i++)
      Assert.That(deflate64Bases[i], Is.EqualTo(deflateBases[i]), $"LengthBase[{i}] mismatch");
  }

  [Test]
  public void Decompressor_StandardDeflateData_Decompresses() {
    // Standard Deflate data should also work through Deflate64 decompressor
    // (Deflate64 is a superset). Use a simple static Huffman block.
    byte[] original = "Hello, Deflate64 World!"u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(original);
    byte[] result = Deflate64Decompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(original));
  }

  [Test]
  public void Decompressor_EmptyInput_ProducesEmptyOutput() {
    // Compress empty data with standard Deflate and decompress with Deflate64
    byte[] compressed = DeflateCompressor.Compress([]);
    byte[] result = Deflate64Decompressor.Decompress(compressed);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Decompressor_UncompressedBlock_Decompresses() {
    // Build a minimal uncompressed Deflate block manually:
    // BFINAL=1, BTYPE=00 (uncompressed), LEN=5, NLEN=~5, data="Hello"
    byte[] data = "Hello"u8.ToArray();
    int len = data.Length;
    int nlen = len ^ 0xFFFF;
    var compressed = new byte[1 + 4 + len]; // 1 header byte + 2 LEN + 2 NLEN + data
    compressed[0] = 0x01; // BFINAL=1, BTYPE=00
    compressed[1] = (byte)(len & 0xFF);
    compressed[2] = (byte)((len >> 8) & 0xFF);
    compressed[3] = (byte)(nlen & 0xFF);
    compressed[4] = (byte)((nlen >> 8) & 0xFF);
    Array.Copy(data, 0, compressed, 5, len);

    byte[] result = Deflate64Decompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompressor_StreamingDecompress_Works() {
    byte[] original = "Streaming decompression test for Deflate64."u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(original);

    using var ms = new MemoryStream(compressed);
    var decompressor = new Deflate64Decompressor(ms);

    var output = new byte[original.Length];
    int totalRead = 0;
    while (totalRead < original.Length) {
      int read = decompressor.Decompress(output, totalRead, original.Length - totalRead);
      if (read == 0)
        break;
      totalRead += read;
    }

    Assert.That(totalRead, Is.EqualTo(original.Length));
    Assert.That(output, Is.EqualTo(original));
  }
}
