using Compression.Core.Dictionary.Rar;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class Rar4WriterTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rar3Codec_Direct_RoundTrip() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var encoder = new Rar3Encoder(20);
    var compressed = encoder.Compress(data);

    var decoder = new Rar3Decoder();
    var decompressed = decoder.Decompress(compressed, data.Length, 20);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  /// <summary>Text-like bytes with long and short repeats, plus a stretch of noise.</summary>
  private static byte[] Mixed(int length, int seed) {
    var rng = new Random(seed);
    var data = new byte[length];
    var words = new[] { "alpha ", "beta ", "gamma ", "delta\r\n", "epsilon ", "zeta " };
    var p = 0;
    while (p < length) {
      if (rng.Next(40) == 0) {
        var n = Math.Min(length - p, rng.Next(1, 3000));
        rng.NextBytes(data.AsSpan(p, n));
        p += n;
        continue;
      }
      foreach (var c in words[rng.Next(words.Length)]) {
        if (p == length) break;
        data[p++] = (byte)c;
      }
    }
    return data;
  }

  [Category("RoundTrip")]
  [Category("Boundary")]
  [TestCase(16, 300_000)]      // several blocks (one token per byte of noise), window slides
  [TestCase(16, 3_000_000)]    // the 64 KB window slides many times
  [TestCase(22, 6_000_000)]    // 4 MB window: the buffer slides once full
  public void Given_DataSpanningSeveralBlocksAndWindowSlides_When_StreamedThroughTheRar3Codec_Then_ItRoundTrips(int windowBits, int length) {
    var data = Mixed(length, length);
    // Noise first forces block switches early: one literal token per byte.
    new Random(1).NextBytes(data.AsSpan(0, Math.Min(length, Rar3Encoder.BlockTokens + 1000)));
    using var packed = new MemoryStream();
    new Rar3Encoder(windowBits).Compress(new MemoryStream(data, writable: false), data.Length, packed);
    packed.Position = 0;
    using var output = new MemoryStream();
    new Rar3Decoder().Decompress(packed, packed.Length, data.Length, output, windowBits);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("RoundTrip")]
  [Test]
  public void Given_ASolidSequence_When_EachFileSpansBlocks_Then_TheSolidStreamRoundTrips() {
    var files = new[] { Mixed(400_000, 1), [], Mixed(5, 2), Mixed(700_000, 3) };
    var encoder = new Rar3Encoder(18);
    var decoder = new Rar3Decoder();
    foreach (var f in files) {
      var packed = encoder.Compress(f);
      Assert.That(decoder.Decompress(packed, f.Length, 18), Is.EqualTo(f));
    }
  }

  [Category("RoundTrip")]
  [Test]
  public void Given_AReadOnlyOnceInput_When_ItDoesNotShrink_Then_TheEntryStaysCompressedAndCorrect() {
    // A non-seekable input cannot be re-read for the store fallback.
    var data = new byte[200_000];
    new Random(5).NextBytes(data);
    using var ms = new MemoryStream();
    using (var w = new Rar4Writer(ms, leaveOpen: true))
      w.AddFile("noise.bin", new ForwardOnly(data), data.Length);
    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);
    Assert.That(reader.Entries[0].CompressionMethod, Is.Not.Zero);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("RoundTrip")]
  [Test]
  public void Given_ASeekableInput_When_ItDoesNotShrink_Then_ItIsStored() {
    var data = new byte[200_000];
    new Random(6).NextBytes(data);
    using var ms = new MemoryStream();
    using (var w = new Rar4Writer(ms, leaveOpen: true))
      w.AddFile("noise.bin", new MemoryStream(data), data.Length);
    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);
    Assert.That(reader.Entries[0].CompressionMethod, Is.Zero);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  private sealed class ForwardOnly(byte[] data) : Stream {
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = Math.Min(count, data.Length - this._pos);
      Array.Copy(data, this._pos, buffer, offset, n);
      this._pos += n;
      return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip() {
    var data = "Hello, RAR4 Store!"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodStore))
      writer.AddFile("test.txt", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_RoundTrip() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodNormal))
      writer.AddFile("pattern.bin", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    var d1 = "First file data"u8.ToArray();
    var d2 = new byte[500];
    for (var i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 13);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true)) {
      writer.AddFile("f1.txt", d1);
      writer.AddFile("f2.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_RoundTrip() {
    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true))
      writer.AddFile("empty.txt", []);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(0), Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_RoundTrip() {
    var d1 = new byte[300];
    var d2 = new byte[300];
    for (var i = 0; i < 300; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, solid: true)) {
      writer.AddFile("f1.bin", d1);
      writer.AddFile("f2.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Store_RoundTrip() {
    var data = "RAR4 encrypted store test"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true,
        method: RarConstants.Rar4MethodStore, password: "secret"))
      writer.AddFile("enc.txt", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "secret", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Compressed_RoundTrip() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i % 10);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true,
        method: RarConstants.Rar4MethodNormal, password: "pass123"))
      writer.AddFile("pattern.bin", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "pass123", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_MultipleFiles_RoundTrip() {
    var d1 = "File one"u8.ToArray();
    var d2 = new byte[300];
    for (var i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 7);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, password: "test")) {
      writer.AddFile("a.txt", d1);
      writer.AddFile("b.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "test", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_RoundTrip() {
    var d1 = new byte[1000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) d1[i] = (byte)(i % 10);
    for (var i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 7);

    var volumes = Rar4Writer.CreateSplit(
      maxVolumeSize: 1024,
      entries: [("a.bin", d1), ("b.bin", d2)],
      method: RarConstants.Rar4MethodStore);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    using var reader = new RarReader(volumes.Select(v => (Stream)new MemoryStream(v)).ToList(), null);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }
}
