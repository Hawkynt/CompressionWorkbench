#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.BinaryII;

namespace Compression.Tests.BinaryII;

[TestFixture]
public sealed class BinaryIITests {
  [Test]
  public void Descriptor_AdvertisesRealReadWriteAndSqueeze() {
    var d = new BinaryIIFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
    Assert.That(d.Methods.Select(m => m.Name), Is.EquivalentTo(new[] { "stored", "squeeze", "auto" }));
    Assert.That(d.Extensions, Does.Contain(".bny"));
    Assert.That(d.Extensions, Does.Contain(".bqy"));
  }

  [Test]
  public void Stored_CreateRoundTripsFilesAndDirectories() {
    var d = new BinaryIIFormatDescriptor();
    var inputs = new ArchiveInputInfo[] {
      new("DIR", "dir", true),
      ArchiveInputInfo.InMemory("dir/hello.txt", "hello"u8),
      ArchiveInputInfo.InMemory("root.bin", new byte[] { 0, 1, 2, 3, 4 }),
    };
    using var archive = new MemoryStream();

    d.Create(archive, inputs, new FormatCreateOptions { MethodName = "stored" });

    var bytes = archive.ToArray();
    Assert.That(bytes.Length % 128, Is.Zero);
    Assert.That(bytes.AsSpan(0, 3).ToArray(), Is.EqualTo(new byte[] { 0x0A, 0x47, 0x4C }));
    archive.Position = 0;
    var listed = d.List(archive, null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "DIR", "DIR/HELLO.TXT", "ROOT.BIN" }));
    Assert.That(listed.Single(e => e.Name == "DIR").IsDirectory, Is.True);

    archive.Position = 0;
    Assert.That(d.ExtractEntryToMemory(archive, "DIR/HELLO.TXT", null), Is.EqualTo("hello"u8.ToArray()));
    archive.Position = 0;
    Assert.That(d.ExtractEntryToMemory(archive, "ROOT.BIN", null), Is.EqualTo(new byte[] { 0, 1, 2, 3, 4 }));
  }

  [Test]
  public void Squeeze_CreateSetsFlagAndRoundTrips() {
    var d = new BinaryIIFormatDescriptor();
    var payload = Enumerable.Repeat((byte)'A', 4096).Concat(Enumerable.Repeat((byte)'B', 1024)).ToArray();
    using var archive = new MemoryStream();

    d.Create(archive, [ArchiveInputInfo.InMemory("sample.dat", payload)], new FormatCreateOptions { MethodName = "squeeze" });

    var raw = archive.ToArray();
    Assert.That((raw[0x7D] & 0x80) != 0, Is.True);
    Assert.That(raw[128], Is.EqualTo(0x76));
    Assert.That(raw[129], Is.EqualTo(0xFF));

    archive.Position = 0;
    var listed = d.List(archive, null);
    Assert.That(listed.Single().Method, Is.EqualTo("Squeeze"));
    archive.Position = 0;
    Assert.That(d.ExtractEntryToMemory(archive, "SAMPLE.DAT", null), Is.EqualTo(payload));
  }

  [Test]
  public void QqSuffix_TriggersHistoricalSqueezeFallbackWithoutFlag() {
    var d = new BinaryIIFormatDescriptor();
    var payload = Enumerable.Repeat((byte)0x5A, 1024).ToArray();
    using var archive = new MemoryStream();
    d.Create(archive, [ArchiveInputInfo.InMemory("thing.qq", payload)], new FormatCreateOptions { MethodName = "squeeze" });

    var raw = archive.ToArray();
    raw[0x7D] &= 0x7F;
    using var compat = new MemoryStream(raw, writable: false);

    Assert.That(d.ExtractEntryToMemory(compat, "THING.QQ", null), Is.EqualTo(payload));
  }

  [Test]
  public void DirectAddReplaceRemovePatchesCountdownAndPreservesOtherPayloads() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [
      ArchiveInputInfo.InMemory("one.bin", Enumerable.Repeat((byte)1, 129).ToArray()),
      ArchiveInputInfo.InMemory("two.bin", Enumerable.Repeat((byte)2, 260).ToArray()),
    ], new FormatCreateOptions { MethodName = "stored" });

    var originalTwo = d.ExtractEntryToMemory(Reset(archive), "TWO.BIN", null);
    d.Add(archive, [ArchiveInputInfo.InMemory("three.bin", Enumerable.Repeat((byte)3, 17).ToArray())]);

    var afterAdd = archive.ToArray();
    Assert.That(afterAdd[0x7F], Is.EqualTo(2));
    var secondHeader = 128 + 256;
    Assert.That(afterAdd[secondHeader + 0x7F], Is.EqualTo(1));

    d.Add(archive, [ArchiveInputInfo.InMemory("one.bin", Enumerable.Repeat((byte)9, 700).ToArray())]);
    Assert.That(d.ExtractEntryToMemory(Reset(archive), "ONE.BIN", null), Is.EqualTo(Enumerable.Repeat((byte)9, 700).ToArray()));
    Assert.That(d.ExtractEntryToMemory(Reset(archive), "TWO.BIN", null), Is.EqualTo(originalTwo));

    d.Remove(archive, ["two.bin"]);
    var names = d.List(Reset(archive), null).Select(e => e.Name).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "ONE.BIN", "THREE.BIN" }));
    Assert.That(d.ExtractEntryToMemory(Reset(archive), "THREE.BIN", null), Is.EqualTo(Enumerable.Repeat((byte)3, 17).ToArray()));
    var afterRemove = archive.ToArray();
    Assert.That(afterRemove[0x7F], Is.EqualTo(1));
  }

  [Test]
  public void AddSynthesizesMissingParentDirectoryInPlace() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [ArchiveInputInfo.InMemory("root.bin", "root"u8)], new FormatCreateOptions());

    d.Add(archive, [ArchiveInputInfo.InMemory("sub/new.bin", "new"u8)]);

    var entries = d.List(Reset(archive), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "ROOT.BIN", "SUB", "SUB/NEW.BIN" }));
    Assert.That(entries[1].IsDirectory, Is.True);
  }

  [Test]
  public void RemoveDirectoryRemovesItsDescendants() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [
      ArchiveInputInfo.InMemory("keep.bin", "keep"u8),
      ArchiveInputInfo.InMemory("tree/a.bin", "a"u8),
      ArchiveInputInfo.InMemory("tree/b.bin", "b"u8),
    ], new FormatCreateOptions());

    d.Remove(archive, ["tree"]);

    Assert.That(d.List(Reset(archive), null).Select(e => e.Name), Is.EqualTo(new[] { "KEEP.BIN" }));
  }

  [Test]
  public void AutoCompressionUsesBlockRoundedSize() {
    var d = new BinaryIIFormatDescriptor();
    using var compressible = new MemoryStream();
    d.Create(compressible, [ArchiveInputInfo.InMemory("repeat.bin", Enumerable.Repeat((byte)'A', 4096).ToArray())],
      new FormatCreateOptions { MethodName = "auto" });
    Assert.That((compressible.ToArray()[0x7D] & 0x80) != 0, Is.True);

    using var tiny = new MemoryStream();
    d.Create(tiny, [ArchiveInputInfo.InMemory("tiny.bin", new byte[] { 1, 2, 3 })],
      new FormatCreateOptions { MethodName = "auto" });
    Assert.That((tiny.ToArray()[0x7D] & 0x80) != 0, Is.False);
  }

  [Test]
  public void NamesAreNormalizedToProDosPartialPathRules() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [
      ArchiveInputInfo.InMemory("123 very-long-directory-name/hello world.txt", "x"u8),
      ArchiveInputInfo.InMemory("123 very-long-directory-name/hello world.txt", "y"u8),
    ], new FormatCreateOptions());

    var names = d.List(Reset(archive), null).Select(e => e.Name).ToArray();
    Assert.That(names[0], Does.StartWith("X123.VERY.LONG"));
    Assert.That(names[^2], Is.Not.EqualTo(names[^1]).IgnoreCase);
    Assert.That(names.All(n => n.Length <= 64), Is.True);
  }

  [Test]
  public void EncryptionIsRejected() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    Assert.Throws<NotSupportedException>(() =>
      d.Create(archive, [ArchiveInputInfo.InMemory("x.bin", "x"u8)], new FormatCreateOptions { Password = "secret" }));
  }

  [Test]
  public void DefragmentTrimsTrailingGarbageButKeepsPayload() {
    var d = new BinaryIIFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [ArchiveInputInfo.InMemory("x.bin", Enumerable.Repeat((byte)0xA5, 200).ToArray())], new FormatCreateOptions());
    var canonicalLength = archive.Length;
    archive.Position = archive.Length;
    archive.Write(new byte[321]);

    d.Defragment(archive);

    Assert.That(archive.Length, Is.EqualTo(canonicalLength));
    Assert.That(d.ExtractEntryToMemory(Reset(archive), "X.BIN", null), Is.EqualTo(Enumerable.Repeat((byte)0xA5, 200).ToArray()));
  }

  private static MemoryStream Reset(MemoryStream stream) {
    stream.Position = 0;
    return stream;
  }
}
