using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

[TestFixture]
public class LhaTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Store() {
    var data = "Hello, LHA archive!"u8.ToArray();
    var writer = new LhaWriter(LhaConstants.MethodLh0);
    writer.AddFile("test.txt", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("test.txt"));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(data.Length));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh5() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 37);

    var writer = new LhaWriter(LhaConstants.MethodLh5);
    writer.AddFile("pattern.bin", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file with different content"u8.ToArray();
    var data3 = new byte[100];
    Array.Fill(data3, (byte)0xFF);

    var writer = new LhaWriter(LhaConstants.MethodLh0);
    writer.AddFile("file1.txt", data1);
    writer.AddFile("file2.txt", data2);
    writer.AddFile("file3.bin", data3);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(data3));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh4() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 37);

    var writer = new LhaWriter(LhaConstants.MethodLh4);
    writer.AddFile("lh4.bin", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh4));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh1() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var writer = new LhaWriter(LhaConstants.MethodLh1);
    writer.AddFile("lh1.bin", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  // -lh2- and -lh3- are read-only: the writer once emitted lh5 data under those IDs, which no real
  // LHA reads. Reading them is pinned against real archives in LhaLegacyMethodVectorTests.
  [Category("Exception")]
  [TestCase(LhaConstants.MethodLh2)]
  [TestCase(LhaConstants.MethodLh3)]
  public void Writer_RefusesReadOnlyMethod(string method) =>
    Assert.Throws<NotSupportedException>(() => _ = new LhaWriter(method));

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh6() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 41);

    var writer = new LhaWriter(LhaConstants.MethodLh6);
    writer.AddFile("lh6.bin", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh6));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh7() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 53);

    var writer = new LhaWriter(LhaConstants.MethodLh7);
    writer.AddFile("lh7.bin", data);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh7));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyFile() {
    var writer = new LhaWriter();
    writer.AddFile("empty.txt", []);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }
}

[TestFixture]
public class LzsDecoderTests {
  [Category("HappyPath")]
  [Test]
  public void Decode_LiteralOnly() {
    // Bit stream: 1 + 'A', 1 + 'B', 1 + 'C' (each literal is a 1 flag bit and 8 bits), zero-padded.
    byte[] compressed = [0xA0, 0xD0, 0xA8, 0x60];
    var result = LzsDecoder.Decode(compressed, 3);
    Assert.That(result, Is.EqualTo("ABC"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Test]
  public void Decode_CopyFromInitialSpaces() {
    // 0 + position 0 (11 bits) + length 3-2 (4 bits): copies three of the spaces the ring starts with.
    byte[] compressed = [0b0000_0000, 0b0000_0001];
    Assert.That(LzsDecoder.Decode(compressed, 3), Is.EqualTo("   "u8.ToArray()));
  }

  [Category("RoundTrip")]
  [Test]
  public void Encoder_RoundTrips() {
    var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("lzs round trip ", 300)));
    Assert.That(LzsDecoder.Decode(LzsEncoder.Encode(data), data.Length), Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void TruncatedStream_Throws() =>
    Assert.Throws<InvalidDataException>(() => LzsDecoder.Decode([0xA0], 5));
}

[TestFixture]
public class Lz5DecoderTests {
  [Category("HappyPath")]
  [Test]
  public void Decode_LiteralOnly() {
    // All literal flags, 3 bytes
    byte[] compressed = [0x07, 0x41, 0x42, 0x43]; // flags=0b111, A B C
    var result = Lz5Decoder.Decode(compressed, 3);
    Assert.That(result, Is.EqualTo("ABC"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Test]
  public void Decode_CopyFromInitialRing() {
    // Copy (flag 0) from position 13 (the run of byte 0x01), length 3.
    byte[] compressed = [0x00, 13, 0x00];
    Assert.That(Lz5Decoder.Decode(compressed, 3), Is.EqualTo(new byte[] { 1, 1, 1 }));
  }

  [Category("RoundTrip")]
  [Test]
  public void Encoder_RoundTrips() {
    var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("lz5 round trip ", 400)));
    Assert.That(Lz5Decoder.Decode(Lz5Encoder.Encode(data), data.Length), Is.EqualTo(data));
  }
}
