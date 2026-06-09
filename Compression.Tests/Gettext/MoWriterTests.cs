#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Gettext;

namespace Compression.Tests.Gettext;

[TestFixture]
public class MoWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new MoFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_EmitsLittleEndianMagic() {
    using var ms = new MemoryStream();
    MoWriter.Write(ms, [
      new CatalogEntry(0, null, "hello", null, "salut", null),
    ]);
    var blob = ms.ToArray();
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4));
    Assert.That(magic, Is.EqualTo(MoWriter.MagicLe));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_PlainEntries_ReadsBackThroughMoReader() {
    var entries = new List<CatalogEntry> {
      new(0, null, "hello", null, "bonjour", null),
      new(1, null, "world", null, "monde", null),
    };
    using var ms = new MemoryStream();
    MoWriter.Write(ms, entries);

    var parsed = new MoReader().Read(ms.ToArray());
    Assert.That(parsed, Has.Count.EqualTo(2));
    Assert.That(parsed.Any(e => e.MsgId == "hello" && e.MsgStr == "bonjour"), Is.True);
    Assert.That(parsed.Any(e => e.MsgId == "world" && e.MsgStr == "monde"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var d = new MoFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("0000_HEADER.txt", "Content-Type: text/plain; charset=UTF-8\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("0001_hello.txt", "bonjour"u8.ToArray()),
      ArchiveInputInfo.InMemory("0002_world.txt", "monde"u8.ToArray()),
    };
    using var outStream = new MemoryStream();
    d.Create(outStream, inputs, new FormatCreateOptions());

    var parsed = new MoReader().Read(outStream.ToArray());
    Assert.That(parsed, Has.Count.EqualTo(3));
    Assert.That(parsed[0].MsgId, Is.EqualTo("")); // HEADER first
    Assert.That(parsed.Any(e => e.MsgId == "hello" && e.MsgStr == "bonjour"), Is.True);
    Assert.That(parsed.Any(e => e.MsgId == "world" && e.MsgStr == "monde"), Is.True);
  }

  // Boundary: empty msgid (HEADER) entry sorts first per gettext convention.
  [Test, Category("Boundary")]
  public void Write_HeaderEntry_ComesFirstRegardlessOfInputOrder() {
    var entries = new List<CatalogEntry> {
      new(0, null, "first", null, "first-tr", null),
      new(1, null, "", null, "Project-Id-Version: x\n", null),
      new(2, null, "third", null, "third-tr", null),
    };
    using var ms = new MemoryStream();
    MoWriter.Write(ms, entries);

    var parsed = new MoReader().Read(ms.ToArray());
    Assert.That(parsed[0].MsgId, Is.EqualTo(""));
    Assert.That(parsed[0].MsgStr, Does.StartWith("Project-Id-Version:"));
  }

  // Equivalence: context-prefixed entries via the EOT separator.
  [Test, Category("HappyPath")]
  public void RoundTrip_WithContext_PreservesContextField() {
    var entries = new List<CatalogEntry> {
      new(0, "menu", "Open", null, "Ouvrir", null),
      new(1, "button", "Open", null, "Ouvrir!", null),
    };
    using var ms = new MemoryStream();
    MoWriter.Write(ms, entries);

    var parsed = new MoReader().Read(ms.ToArray());
    Assert.That(parsed.Where(e => e.Context == "menu").Select(e => e.MsgStr), Contains.Item("Ouvrir"));
    Assert.That(parsed.Where(e => e.Context == "button").Select(e => e.MsgStr), Contains.Item("Ouvrir!"));
  }

  // Boundary: writer's parse-input-name helper restores msgid/context.
  [Test, Category("Boundary")]
  public void ParseInputName_RestoresContextAndMsgId() {
    Assert.That(MoFormatDescriptor.ParseInputName("0042_hello.txt"), Is.EqualTo(((string?)null, "hello")));
    Assert.That(MoFormatDescriptor.ParseInputName("0000_HEADER.txt"), Is.EqualTo(((string?)null, "")));
    Assert.That(MoFormatDescriptor.ParseInputName("0007_menu__Open.txt"), Is.EqualTo(((string?)"menu", "Open")));
  }
}
