#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Gettext;

namespace Compression.Tests.Gettext;

[TestFixture]
public class GettextTests {

  private static byte[] MakeMinimalMo() {
    // Messages: ("" → header), ("Hello" → "Bonjour"), with context separator test
    var strings = new (string Orig, string Tran)[] {
      ("", "Content-Type: text/plain; charset=UTF-8\n"),
      ("Hello", "Bonjour"),
      ("menu\u0004File", "Fichier"),
    };
    var n = strings.Length;

    using var ms = new MemoryStream();
    // Leave space for header (28 bytes) + 2 tables of n entries (8 bytes each)
    var headerEnd = 28;
    var origTableOff = headerEnd;
    var tranTableOff = origTableOff + 8 * n;
    var stringPoolStart = tranTableOff + 8 * n;

    // First write string pool (each NUL-terminated)
    ms.Position = stringPoolStart;
    var origOffsets = new (uint Off, uint Len)[n];
    var tranOffsets = new (uint Off, uint Len)[n];
    for (var i = 0; i < n; ++i) {
      var origBytes = Encoding.UTF8.GetBytes(strings[i].Orig);
      origOffsets[i] = ((uint)ms.Position, (uint)origBytes.Length);
      ms.Write(origBytes); ms.WriteByte(0);
    }
    for (var i = 0; i < n; ++i) {
      var tranBytes = Encoding.UTF8.GetBytes(strings[i].Tran);
      tranOffsets[i] = ((uint)ms.Position, (uint)tranBytes.Length);
      ms.Write(tranBytes); ms.WriteByte(0);
    }

    // Header
    Span<byte> buf = stackalloc byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x950412DE);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], 0);           // rev
    BinaryPrimitives.WriteUInt32LittleEndian(buf[8..], (uint)n);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[12..], (uint)origTableOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[16..], (uint)tranTableOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[20..], 0);          // hashSize
    BinaryPrimitives.WriteUInt32LittleEndian(buf[24..], 0);          // hashOffset
    ms.Position = 0;
    ms.Write(buf);

    // Tables
    ms.Position = origTableOff;
    for (var i = 0; i < n; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(buf, origOffsets[i].Len);
      BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], origOffsets[i].Off);
      ms.Write(buf[..8]);
    }
    ms.Position = tranTableOff;
    for (var i = 0; i < n; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(buf, tranOffsets[i].Len);
      BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], tranOffsets[i].Off);
      ms.Write(buf[..8]);
    }

    return ms.ToArray();
  }

  [Test]
  public void MoReader_ParsesHeaderAndEntries() {
    var entries = new MoReader().Read(MakeMinimalMo());
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[0].MsgId, Is.Empty);
    Assert.That(entries[0].MsgStr, Does.StartWith("Content-Type"));
    Assert.That(entries[1].MsgId, Is.EqualTo("Hello"));
    Assert.That(entries[1].MsgStr, Is.EqualTo("Bonjour"));
    Assert.That(entries[2].Context, Is.EqualTo("menu"));
    Assert.That(entries[2].MsgId, Is.EqualTo("File"));
    Assert.That(entries[2].MsgStr, Is.EqualTo("Fichier"));
  }

  [Test]
  public void MoDescriptor_ExtractsTxtFiles() {
    var data = MakeMinimalMo();
    var dir = Path.Combine(Path.GetTempPath(), "mo_test_" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(data))
        new MoFormatDescriptor().Extract(ms, dir, null, null);
      var files = Directory.GetFiles(dir);
      Assert.That(files, Has.Length.EqualTo(3));
      Assert.That(files.Any(f => Path.GetFileName(f).Contains("HEADER")));
      Assert.That(files.Any(f => Path.GetFileName(f).Contains("Hello")));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void PoReader_ParsesSingularAndContext() {
    var po = """
      # comment
      msgid ""
      msgstr "Content-Type: text/plain; charset=UTF-8\n"

      msgid "Hello"
      msgstr "Bonjour"

      msgctxt "menu"
      msgid "File"
      msgstr "Fichier"
      """;
    var entries = new PoReader().Read(Encoding.UTF8.GetBytes(po));
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[1].MsgStr, Is.EqualTo("Bonjour"));
    Assert.That(entries[2].Context, Is.EqualTo("menu"));
    Assert.That(entries[2].MsgStr, Is.EqualTo("Fichier"));
  }

  [Test]
  public void PoReader_HandlesMultilineContinuation() {
    var po = """
      msgid "one"
      msgstr ""
      "line one\n"
      "line two\n"
      """;
    var entries = new PoReader().Read(Encoding.UTF8.GetBytes(po));
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].MsgStr, Is.EqualTo("line one\nline two\n"));
  }

  [Test]
  public void PoReader_PluralForms() {
    var po = """
      msgid "one file"
      msgid_plural "%d files"
      msgstr[0] "eine Datei"
      msgstr[1] "%d Dateien"
      """;
    var entries = new PoReader().Read(Encoding.UTF8.GetBytes(po));
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].MsgIdPlural, Is.EqualTo("%d files"));
    Assert.That(entries[0].MsgStrPlural, Is.Not.Null);
    Assert.That(entries[0].MsgStrPlural![0], Is.EqualTo("eine Datei"));
    Assert.That(entries[0].MsgStrPlural![1], Is.EqualTo("%d Dateien"));
  }
}
