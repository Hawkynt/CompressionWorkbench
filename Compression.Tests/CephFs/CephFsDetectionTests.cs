using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.CephFs;

namespace Compression.Tests.CephFs;

[TestFixture]
public class CephFsDetectionTests {
  private static readonly UTF8Encoding Utf8 = new(false, true);

  [Test, Category("HappyPath")]
  public void Descriptor_UsesRealRadosExportMagicAndAdvertisesVerifiedCapabilities() {
    var descriptor = new CephFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("CephFs"));
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xCE, 0xFF, 0xCE, 0xFF }));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Create_RoundTripsDefaultAndNamedNamespaces() {
    var descriptor = new CephFsFormatDescriptor();
    using var image = new MemoryStream();

    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("alpha.bin", "alpha"u8),
      ArchiveInputInfo.InMemory("rados/team%2Fblue/object%2Fone", "bravo"u8),
    ], new FormatCreateOptions());

    image.Position = 0;
    using var reader = new CephFsReader(image);
    Assert.That(reader.Entries.Select(static e => e.Name), Is.EquivalentTo(new[] {
      "rados/@default/alpha.bin",
      "rados/team%2Fblue/object%2Fone",
    }));
    Assert.Multiple(() => {
      var alpha = reader.Entries.Single(static e => e.ObjectId == "alpha.bin");
      Assert.That(alpha.Namespace, Is.Empty);
      Assert.That(alpha.Data, Is.EqualTo("alpha"u8.ToArray()));
      var namespaced = reader.Entries.Single(static e => e.ObjectId == "object/one");
      Assert.That(namespaced.Namespace, Is.EqualTo("team/blue"));
      Assert.That(namespaced.Data, Is.EqualTo("bravo"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesUpstreamPoolDumpSemantics() {
    var bytes = BuildPoolDump(new ObjectSpec(
      ObjectId: "object/1",
      Namespace: "ns",
      Locator: "locator",
      Data: "payload"u8.ToArray(),
      Attributes: new Dictionary<string, byte[]> { ["owner"] = "alice"u8.ToArray() },
      OmapHeader: "header"u8.ToArray(),
      Omap: new Dictionary<string, byte[]> { ["key"] = "value"u8.ToArray() }));

    using var reader = new CephFsReader(new MemoryStream(bytes));
    var entry = reader.Entries.Single();
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("rados/ns/object%2F1"));
      Assert.That(entry.ObjectId, Is.EqualTo("object/1"));
      Assert.That(entry.Namespace, Is.EqualTo("ns"));
      Assert.That(entry.LocatorKey, Is.EqualTo("locator"));
      Assert.That(entry.Data, Is.EqualTo("payload"u8.ToArray()));
      Assert.That(entry.Attributes["owner"], Is.EqualTo("alice"u8.ToArray()));
      Assert.That(entry.OmapHeader, Is.EqualTo("header"u8.ToArray()));
      Assert.That(entry.Omap["key"], Is.EqualTo("value"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void Add_ReplacesPayloadWithoutLosingRadosMetadata() {
    var descriptor = new CephFsFormatDescriptor();
    var bytes = BuildPoolDump(new ObjectSpec(
      ObjectId: "obj",
      Namespace: "team",
      Locator: "placement",
      Data: "old"u8.ToArray(),
      Attributes: new Dictionary<string, byte[]> { ["x"] = [1, 2, 3] },
      OmapHeader: [4, 5],
      Omap: new Dictionary<string, byte[]> { ["k"] = [6, 7] }));
    using var image = new MemoryStream(bytes, writable: true);

    descriptor.Add(image, [ArchiveInputInfo.InMemory("rados/team/obj", "new-data"u8)]);

    image.Position = 0;
    using var reader = new CephFsReader(image);
    var entry = reader.Entries.Single();
    Assert.Multiple(() => {
      Assert.That(entry.Data, Is.EqualTo("new-data"u8.ToArray()));
      Assert.That(entry.LocatorKey, Is.EqualTo("placement"));
      Assert.That(entry.Attributes["x"], Is.EqualTo(new byte[] { 1, 2, 3 }));
      Assert.That(entry.OmapHeader, Is.EqualTo(new byte[] { 4, 5 }));
      Assert.That(entry.Omap["k"], Is.EqualTo(new byte[] { 6, 7 }));
    });
  }

  [Test, Category("HappyPath")]
  public void RemoveAndPurge_LeaveValidPoolExports() {
    var descriptor = new CephFsFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("one", [1]),
      ArchiveInputInfo.InMemory("two", [2]),
    ], new FormatCreateOptions());

    descriptor.Remove(image, ["rados/@default/one"]);
    image.Position = 0;
    Assert.That(descriptor.List(image, null).Select(static e => e.Name), Is.EqualTo(new[] { "rados/@default/two" }));

    ((IArchivePurgeable)descriptor).Purge(image);
    image.Position = 0;
    Assert.Multiple(() => {
      Assert.That(descriptor.List(image, null), Is.Empty);
      Assert.That(image.ToArray()[..4], Is.EqualTo(new byte[] { 0xCE, 0xFF, 0xCE, 0xFF }));
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_CanonicalizesRedundantDataWritesAndTrailer() {
    var descriptor = new CephFsFormatDescriptor();
    var original = BuildPoolDump(new ObjectSpec("obj", "", "", "same-data"u8.ToArray()),
      duplicateDataWrite: true, trailingBytes: 37);
    using var input = new MemoryStream(original);
    using var output = new MemoryStream();

    descriptor.Shrink(input, output);

    Assert.That(output.Length, Is.LessThan(original.LongLength));
    output.Position = 0;
    using var reader = new CephFsReader(output);
    Assert.That(reader.Entries.Single().Data, Is.EqualTo("same-data"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Layout_CoversEveryByteAndWipeOnlyTouchesIgnoredTrailer() {
    var descriptor = new CephFsFormatDescriptor();
    var bytes = BuildPoolDump(new ObjectSpec("obj", "", "", "payload"u8.ToArray()), trailingBytes: 19);
    using var image = new MemoryStream(bytes, writable: true);

    var layout = descriptor.EnumerateLayout(image).ToList();
    Assert.Multiple(() => {
      Assert.That(layout.Sum(static e => e.Length), Is.EqualTo(image.Length));
      Assert.That(layout.Any(static e => e.Kind == DefragBlockKind.Used && e.FileName == "rados/@default/obj"), Is.True);
      Assert.That(layout.Single(static e => e.Kind == DefragBlockKind.Free).Length, Is.EqualTo(19));
    });

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    Assert.That(wiped, Is.EqualTo(19));
    Assert.That(image.ToArray()[^19..], Is.All.EqualTo((byte)0));
    image.Position = 0;
    Assert.That(descriptor.List(image, null), Has.Count.EqualTo(1));
  }

  [Test, Category("Malformed")]
  public void Modification_RefusesUnknownFutureSections() {
    var descriptor = new CephFsFormatDescriptor();
    var bytes = BuildPoolDump(new ObjectSpec("obj", "", "", [1, 2, 3]), injectUnknownSection: true);
    using var image = new MemoryStream(bytes, writable: true);

    image.Position = 0;
    Assert.That(descriptor.List(image, null), Has.Count.EqualTo(1));
    Assert.Throws<NotSupportedException>(() =>
      descriptor.Add(image, [ArchiveInputInfo.InMemory("obj", [9])]));
  }

  [Test, Category("Malformed")]
  public void Reader_RejectsFormerFakeCephMagic() {
    var fake = new byte[64];
    "CEPH"u8.CopyTo(fake);
    Assert.Throws<InvalidDataException>(() => _ = new CephFsReader(new MemoryStream(fake)));
  }

  private sealed record ObjectSpec(
    string ObjectId,
    string Namespace,
    string Locator,
    byte[] Data,
    IReadOnlyDictionary<string, byte[]>? Attributes = null,
    byte[]? OmapHeader = null,
    IReadOnlyDictionary<string, byte[]>? Omap = null);

  /// <summary>
  /// Independent known-answer author for the public Ceph RadosDump wire grammar.
  /// It intentionally does not call the production writer, so reader tests do
  /// not merely prove that two copies of the same bug agree.
  /// </summary>
  private static byte[] BuildPoolDump(ObjectSpec obj, bool duplicateDataWrite = false,
      int trailingBytes = 0, bool injectUnknownSection = false) {
    using var stream = new MemoryStream();
    WriteU32(stream, 0xFFCEFFCE);
    WriteU32(stream, 2);
    WriteU32(stream, 18);
    WriteU32(stream, 10);
    WriteSection(stream, 10, []); // TYPE_POOL_BEGIN
    if (injectUnknownSection)
      WriteSection(stream, 12, "future"u8.ToArray());

    WriteSection(stream, 3, Struct(2, 1, body => {
      body.Write(Struct(4, 3, hobj => {
        WriteString(hobj, obj.Locator == obj.ObjectId ? "" : obj.Locator);
        WriteString(hobj, obj.ObjectId);
        WriteU64(hobj, ulong.MaxValue - 1); // CEPH_NOSNAP
        WriteU32(hobj, 0);
        hobj.WriteByte(0);
        WriteString(hobj, obj.Namespace);
        WriteI64(hobj, long.MinValue);
      }));
      WriteU64(body, ulong.MaxValue);
      body.WriteByte(0xFF);
    }));

    void WriteData() => WriteSection(stream, 5, Struct(1, 1, body => {
      WriteU64(body, 0);
      WriteU64(body, checked((ulong)obj.Data.Length));
      WriteBuffer(body, obj.Data);
    }));
    if (obj.Data.Length > 0) {
      WriteData();
      if (duplicateDataWrite) WriteData();
    }

    WriteSection(stream, 6, Struct(1, 1, body => {
      var attrs = obj.Attributes ?? new Dictionary<string, byte[]>();
      WriteU32(body, checked((uint)attrs.Count));
      foreach (var pair in attrs.OrderBy(static p => p.Key, StringComparer.Ordinal)) {
        WriteString(body, "_" + pair.Key);
        WriteBuffer(body, pair.Value);
      }
    }));
    WriteSection(stream, 7, Struct(1, 1, body => WriteBuffer(body, obj.OmapHeader ?? [])));

    if (obj.Omap is { Count: > 0 } omap) {
      WriteSection(stream, 8, Struct(1, 1, body => {
        WriteU32(body, checked((uint)omap.Count));
        foreach (var pair in omap.OrderBy(static p => p.Key, StringComparer.Ordinal)) {
          WriteString(body, pair.Key);
          WriteBuffer(body, pair.Value);
        }
      }));
    }

    WriteSection(stream, 4, []);  // TYPE_OBJECT_END
    WriteSection(stream, 11, []); // TYPE_POOL_END
    for (var i = 0; i < trailingBytes; ++i)
      stream.WriteByte(0xA5);
    return stream.ToArray();
  }

  private static void WriteSection(Stream stream, byte type, byte[] body) {
    var debugType = ((uint)type << 24) | ((uint)type << 16) | 0xFFCE;
    var header = Struct(1, 1, inner => {
      WriteU32(inner, debugType);
      WriteI64(inner, body.LongLength);
    });
    Assert.That(header, Has.Length.EqualTo(18));
    stream.Write(header);
    if (body.Length == 0) return;
    stream.Write(body);
    var footer = Struct(1, 1, inner => WriteU32(inner, 0xECFFFFCE));
    Assert.That(footer, Has.Length.EqualTo(10));
    stream.Write(footer);
  }

  private static byte[] Struct(byte version, byte compat, Action<MemoryStream> writeBody) {
    using var body = new MemoryStream();
    writeBody(body);
    using var result = new MemoryStream();
    result.WriteByte(version);
    result.WriteByte(compat);
    WriteU32(result, checked((uint)body.Length));
    body.Position = 0;
    body.CopyTo(result);
    return result.ToArray();
  }

  private static void WriteString(Stream stream, string value) {
    var data = Utf8.GetBytes(value);
    WriteU32(stream, checked((uint)data.Length));
    stream.Write(data);
  }

  private static void WriteBuffer(Stream stream, ReadOnlySpan<byte> value) {
    WriteU32(stream, checked((uint)value.Length));
    stream.Write(value);
  }

  private static void WriteU32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteU64(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteI64(Stream stream, long value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }
}