using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Mobi;
using NUnit.Framework;

namespace Compression.Tests.Mobi;

[TestFixture]
public sealed class MobiFormatTests {
  [Test]
  public void PalmDocTextRecordsAreDecodedAndRawContainerIsPreserved() {
    var book = MakeBook(compression: 2, "Hello, world"u8);
    var descriptor = new MobiFormatDescriptor();
    using var stream = new MemoryStream(book, writable: false);

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.mobi"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("book.html"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("records/0001.bin"));
    Assert.That(descriptor.ExtractEntryToMemory(stream, "book.html", null), Is.EqualTo("Hello, world"u8.ToArray()));
    Assert.That(descriptor.ExtractEntryToMemory(stream, "FULL.mobi", null), Is.EqualTo(book));
  }

  [Test]
  public void StoredTextRecordsAreDecoded() {
    var book = MakeBook(compression: 1, "plain text"u8);
    var descriptor = new MobiFormatDescriptor();
    using var stream = new MemoryStream(book, writable: false);

    Assert.That(descriptor.ExtractEntryToMemory(stream, "book.html", null), Is.EqualTo("plain text"u8.ToArray()));
  }

  [Test]
  public void PalmDocBackReferencesAreExpanded() {
    var book = MakeBook(compression: 2, "abcabcabc"u8, [3, (byte)'a', (byte)'b', (byte)'c', 0x80, 27]);
    var descriptor = new MobiFormatDescriptor();
    using var stream = new MemoryStream(book, writable: false);

    Assert.That(descriptor.ExtractEntryToMemory(stream, "book.html", null), Is.EqualTo("abcabcabc"u8.ToArray()));
  }

  [Test]
  public void HufFRecordsRemainAvailableWithoutInventingDecodedText() {
    var book = MakeBook(compression: 17480, "payload"u8);
    var descriptor = new MobiFormatDescriptor();
    using var stream = new MemoryStream(book, writable: false);

    Assert.That(descriptor.List(stream, null).Select(e => e.Name), Does.Not.Contain("book.html"));
    Assert.That(descriptor.ExtractEntryToMemory(stream, "records/0001.bin", null), Is.EqualTo("payload"u8.ToArray()));
  }

  [TestCase("stored")]
  [TestCase("palmdoc")]
  public void CreatedBooksRoundTripContentAndMetadata(string method) {
    var descriptor = new MobiFormatDescriptor();
    using var archive = new MemoryStream();
    var options = new FormatCreateOptions(method);
    options.FormatSpecific["title"] = "Round trip title";
    options.FormatSpecific["author"] = "Ada Example";
    options.FormatSpecific["language"] = "1036";

    descriptor.Create(archive, [ArchiveInputInfo.InMemory("book.html", "<html>repeat repeat repeat</html>"u8)], options);
    archive.Position = 0;

    Assert.That(archive.ToArray().AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8), Is.True);
    Assert.That(descriptor.ExtractEntryToMemory(archive, "book.html", null), Is.EqualTo("<html>repeat repeat repeat</html>"u8.ToArray()));
    var metadata = System.Text.Encoding.UTF8.GetString(descriptor.ExtractEntryToMemory(archive, "metadata.ini", null));
    Assert.That(metadata, Does.Contain("title=Round trip title"));
    Assert.That(metadata, Does.Contain("author=Ada Example"));
    Assert.That(metadata, Does.Contain("language_locale=1036"));
  }

  private static byte[] MakeBook(ushort compression, ReadOnlySpan<byte> text, byte[]? encodedOverride = null) {
    var encoded = compression switch {
      1 => text.ToArray(),
      2 => PalmDocLiteralEncode(text),
      _ => text.ToArray(),
    };
    if (encodedOverride != null) encoded = encodedOverride;
    const int headerLength = 78 + 3 * 8;
    var record0 = new byte[16];
    BinaryPrimitives.WriteUInt16BigEndian(record0, compression);
    BinaryPrimitives.WriteUInt32BigEndian(record0.AsSpan(4), (uint)text.Length);
    BinaryPrimitives.WriteUInt16BigEndian(record0.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt16BigEndian(record0.AsSpan(10), 4096);
    var result = new byte[headerLength + record0.Length + encoded.Length];
    "Test book"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(60), 0x424F4F4Bu);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(64), 0x4D4F4249u);
    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(76), 2);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(78), headerLength);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(86), headerLength + (uint)record0.Length);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(94), 0);
    record0.CopyTo(result, headerLength);
    encoded.CopyTo(result, headerLength + record0.Length);
    return result;
  }

  private static byte[] PalmDocLiteralEncode(ReadOnlySpan<byte> input) {
    var output = new List<byte>(input.Length + input.Length / 8);
    for (var offset = 0; offset < input.Length;) {
      var length = Math.Min(8, input.Length - offset);
      output.Add((byte)length);
      for (var i = 0; i < length; ++i)
        output.Add(input[offset + i]);
      offset += length;
    }
    return output.ToArray();
  }
}
