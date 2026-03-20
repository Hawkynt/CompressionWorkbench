using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

[TestFixture]
public class LhaTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Store() {
    byte[] data = "Hello, LHA archive!"u8.ToArray();
    var writer = new LhaWriter(LhaConstants.MethodLh0);
    writer.AddFile("test.txt", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("test.txt"));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(data.Length));

    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh5() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 37);

    var writer = new LhaWriter(LhaConstants.MethodLh5);
    writer.AddFile("pattern.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    byte[] data1 = "First file content"u8.ToArray();
    byte[] data2 = "Second file with different content"u8.ToArray();
    byte[] data3 = new byte[100];
    Array.Fill(data3, (byte)0xFF);

    var writer = new LhaWriter(LhaConstants.MethodLh0);
    writer.AddFile("file1.txt", data1);
    writer.AddFile("file2.txt", data2);
    writer.AddFile("file3.bin", data3);

    byte[] archive = writer.ToArray();

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
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 37);

    var writer = new LhaWriter(LhaConstants.MethodLh4);
    writer.AddFile("lh4.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh4));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh1() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var writer = new LhaWriter(LhaConstants.MethodLh1);
    writer.AddFile("lh1.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh1));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh2() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var writer = new LhaWriter(LhaConstants.MethodLh2);
    writer.AddFile("lh2.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh3() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var writer = new LhaWriter(LhaConstants.MethodLh3);
    writer.AddFile("lh3.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh6() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 41);

    var writer = new LhaWriter(LhaConstants.MethodLh6);
    writer.AddFile("lh6.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh6));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile_Lh7() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 53);

    var writer = new LhaWriter(LhaConstants.MethodLh7);
    writer.AddFile("lh7.bin", data);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLh7));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyFile() {
    var writer = new LhaWriter();
    writer.AddFile("empty.txt", []);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }
}

[TestFixture]
public class LzsDecoderTests {
  [Category("HappyPath")]
  [Test]
  public void Decode_LiteralOnly() {
    // All literal flags, 3 bytes
    byte[] compressed = [0x07, 0x41, 0x42, 0x43]; // flags=0b111, A B C
    byte[] result = LzsDecoder.Decode(compressed, 3);
    Assert.That(result, Is.EqualTo("ABC"u8.ToArray()));
  }
}

[TestFixture]
public class Lz5DecoderTests {
  [Category("HappyPath")]
  [Test]
  public void Decode_LiteralOnly() {
    // All literal flags, 3 bytes
    byte[] compressed = [0x07, 0x41, 0x42, 0x43]; // flags=0b111, A B C
    byte[] result = Lz5Decoder.Decode(compressed, 3);
    Assert.That(result, Is.EqualTo("ABC"u8.ToArray()));
  }
}
