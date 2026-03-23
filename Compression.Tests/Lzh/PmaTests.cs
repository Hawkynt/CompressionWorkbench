using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

[TestFixture]
public class PmaTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Pm2_RoundTrip_Pattern() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var compressed = PmaEncoder.Encode(data, 3);
    var decompressed = PmaDecoder.Decode(compressed, data.Length, 3);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Pm1_RoundTrip_Pattern() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var compressed = PmaEncoder.Encode(data, 2);
    var decompressed = PmaDecoder.Decode(compressed, data.Length, 2);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Pm2_RoundTrip_Text() {
    var data = "Hello, PMA compression! This is a test of PPMd-based encoding in LHA archives."u8.ToArray();

    var compressed = PmaEncoder.Encode(data, 3);
    var decompressed = PmaDecoder.Decode(compressed, data.Length, 3);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Pm2_Empty() {
    var compressed = PmaEncoder.Encode([], 3);
    Assert.That(compressed, Is.Empty);

    var decompressed = PmaDecoder.Decode(compressed, 0, 3);
    Assert.That(decompressed, Is.Empty);
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Pm2_Archive_RoundTrip() {
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var writer = new LhaWriter(LhaConstants.MethodPm2);
    writer.AddFile("test.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodPm2));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Pm1_Archive_RoundTrip() {
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 5);

    var writer = new LhaWriter(LhaConstants.MethodPm1);
    writer.AddFile("test.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodPm1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Pm0_Archive_RoundTrip() {
    var data = "Stored PMA data"u8.ToArray();

    var writer = new LhaWriter(LhaConstants.MethodPm0);
    writer.AddFile("test.txt", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodPm0));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Pm2_MultipleFiles_RoundTrip() {
    var data1 = new byte[100];
    var data2 = new byte[200];
    for (var i = 0; i < data1.Length; ++i) data1[i] = (byte)(i % 10);
    for (var i = 0; i < data2.Length; ++i) data2[i] = (byte)(i % 7);

    var writer = new LhaWriter(LhaConstants.MethodPm2);
    writer.AddFile("f1.bin", data1);
    writer.AddFile("f2.bin", data2);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new LhaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }
}
