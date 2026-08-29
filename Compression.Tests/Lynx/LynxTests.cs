#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Lynx;

namespace Compression.Tests.Lynx;

[TestFixture]
public sealed class LynxTests {
  [Test]
  public void Descriptor_CreatesCanonicalMultiFileArchive_AndRoundTripsBytes() {
    var first = Enumerable.Range(0, 700).Select(index => (byte)(index * 17)).ToArray();
    var second = "HELLO FROM LYNX"u8.ToArray();
    var descriptor = new LynxFormatDescriptor();
    using var archive = Create(descriptor, [
      ArchiveInputInfo.InMemory("FIRST.PRG", first),
      ArchiveInputInfo.InMemory("SECOND.SEQ", second),
    ]);

    var bytes = archive.ToArray();
    Assert.Multiple(() => {
      Assert.That(bytes.Length % 254, Is.Zero);
      Assert.That(Encoding.ASCII.GetString(bytes, 60, 4), Is.EqualTo("LYNX"));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    });

    archive.Position = 0;
    var listed = descriptor.List(archive, null);
    Assert.That(listed.Select(entry => entry.Name), Is.EqualTo(new[] { "FIRST.PRG", "SECOND.SEQ" }));
    Assert.That(listed.Select(entry => entry.OriginalSize), Is.EqualTo(new long[] { first.Length, second.Length }));

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "FIRST.PRG", null), Is.EqualTo(first));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "SECOND.SEQ", null), Is.EqualTo(second));
  }

  [Test]
  public void Descriptor_InPlaceAddReplaceRemove_PreservesUnaffectedEntries() {
    var descriptor = new LynxFormatDescriptor();
    var keep = Enumerable.Repeat((byte)0x4B, 600).ToArray();
    using var archive = Create(descriptor, [
      ArchiveInputInfo.InMemory("KEEP.PRG", keep),
      ArchiveInputInfo.InMemory("EDIT.PRG", "old"u8.ToArray()),
    ]);

    archive.Position = 0;
    descriptor.Add(archive, [ArchiveInputInfo.InMemory("NEW.PRG", Enumerable.Repeat((byte)0x4E, 900).ToArray())]);
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "KEEP.PRG", null), Is.EqualTo(keep));

    var replacement = Enumerable.Range(0, 1300).Select(index => (byte)(255 - index)).ToArray();
    archive.Position = 0;
    descriptor.Add(archive, [ArchiveInputInfo.InMemory("EDIT.PRG", replacement)]);
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "EDIT.PRG", null), Is.EqualTo(replacement));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "KEEP.PRG", null), Is.EqualTo(keep));

    archive.Position = 0;
    descriptor.Remove(archive, ["NEW.PRG"]);
    archive.Position = 0;
    var names = descriptor.List(archive, null).Select(entry => entry.Name).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "KEEP.PRG", "EDIT.PRG" }));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "KEEP.PRG", null), Is.EqualTo(keep));
  }

  [Test]
  public void InPlaceAdd_GrowsDirectoryByWhole254ByteBlocks_WhenNeeded() {
    var descriptor = new LynxFormatDescriptor();
    using var archive = Create(descriptor, [ArchiveInputInfo.InMemory("BASE.PRG", "base"u8.ToArray())]);
    var initialBlocks = ReadDirectoryBlockCount(archive.ToArray());
    var basePayload = descriptor.ExtractEntryToMemory(archive, "BASE.PRG", null);

    for (var i = 0; i < 12; ++i) {
      archive.Position = 0;
      descriptor.Add(archive, [ArchiveInputInfo.InMemory($"FILE{i:D2}.PRG", Enumerable.Repeat((byte)i, 20 + i).ToArray())]);
    }

    var grownBlocks = ReadDirectoryBlockCount(archive.ToArray());
    Assert.That(grownBlocks, Is.GreaterThan(initialBlocks));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "BASE.PRG", null), Is.EqualTo(basePayload));
    archive.Position = 0;
    Assert.That(descriptor.List(archive, null), Has.Count.EqualTo(13));
  }

  [Test]
  public void Descriptor_SupportsEmptyArchive_AndPurgeToZeroEntries() {
    var descriptor = new LynxFormatDescriptor();
    using var empty = Create(descriptor, []);
    Assert.That(empty.Length, Is.EqualTo(254));
    empty.Position = 0;
    Assert.That(descriptor.List(empty, null), Is.Empty);

    using var archive = Create(descriptor, [ArchiveInputInfo.InMemory("ONLY.PRG", "payload"u8.ToArray())]);
    archive.Position = 0;
    descriptor.Remove(archive, ["ONLY.PRG"]);
    archive.Position = 0;
    Assert.That(descriptor.List(archive, null), Is.Empty);
    Assert.That(archive.Length, Is.EqualTo(ReadDirectoryBlockCount(archive.ToArray()) * 254L));
  }

  [Test]
  public void Writer_UsesRequestedCommodoreType_AndRejectsLossyNamesOrEncryption() {
    var descriptor = new LynxFormatDescriptor();
    using var seq = Create(descriptor,
      [ArchiveInputInfo.InMemory("DATA.SEQ", "data"u8.ToArray())],
      new FormatCreateOptions {
        MethodName = "stored",
        FormatSpecific = new Dictionary<string, string> { ["FileType"] = "S" },
      });
    seq.Position = 0;
    Assert.That(descriptor.List(seq, null).Single().Method, Is.EqualTo("Stored/S"));

    Assert.Throws<ArgumentException>(() => Create(descriptor, [
      ArchiveInputInfo.InMemory("abcdefghijklmnop-A", Array.Empty<byte>()),
      ArchiveInputInfo.InMemory("abcdefghijklmnop-B", new byte[] { 1 }),
    ]));
    Assert.Throws<ArgumentException>(() => Create(descriptor,
      [ArchiveInputInfo.InMemory("MÄDCHEN.PRG", Array.Empty<byte>())]));
    Assert.Throws<NotSupportedException>(() => Create(descriptor,
      [ArchiveInputInfo.InMemory("A.PRG", Array.Empty<byte>())],
      new FormatCreateOptions { Password = "secret" }));
    Assert.Throws<NotSupportedException>(() => Create(descriptor,
      [ArchiveInputInfo.InMemory("A.PRG", Array.Empty<byte>())],
      new FormatCreateOptions { MethodName = "deflate" }));
  }

  [Test]
  public void Defragment_TrimsOnlyTrailingTransportPadding() {
    var descriptor = new LynxFormatDescriptor();
    using var archive = Create(descriptor, [ArchiveInputInfo.InMemory("A.PRG", Enumerable.Repeat((byte)1, 300).ToArray())]);
    var logicalLength = archive.Length;
    archive.Position = archive.Length;
    archive.Write(new byte[777]);
    Assert.That(archive.Length, Is.GreaterThan(logicalLength));

    archive.Position = 0;
    descriptor.Defragment(archive);
    Assert.That(archive.Length, Is.EqualTo(logicalLength));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "A.PRG", null), Is.EqualTo(Enumerable.Repeat((byte)1, 300).ToArray()));
  }

  private static MemoryStream Create(
      LynxFormatDescriptor descriptor,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions? options = null) {
    var archive = new MemoryStream();
    descriptor.Create(archive, inputs, options ?? new FormatCreateOptions());
    archive.Position = 0;
    return archive;
  }

  private static int ReadDirectoryBlockCount(byte[] archive) {
    var cursor = -1;
    for (var i = 4; i <= Math.Min(archive.Length, 1024); ++i) {
      if (archive[i - 4] == 0 && archive[i - 3] == 0 && archive[i - 2] == 0 && archive[i - 1] == 13) {
        cursor = i;
        break;
      }
    }
    Assert.That(cursor, Is.GreaterThanOrEqualTo(0));
    while (archive[cursor] == (byte)' ') ++cursor;
    var value = 0;
    while (archive[cursor] is >= (byte)'0' and <= (byte)'9')
      value = value * 10 + archive[cursor++] - (byte)'0';
    return value;
  }
}
