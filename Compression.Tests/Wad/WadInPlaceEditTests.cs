using Compression.Registry;
using FileFormat.Wad;

namespace Compression.Tests.Wad;

[TestFixture]
public sealed class WadInPlaceEditTests {
  [Test]
  public void Add_ReplacesTrailingDirectory_WithoutReadingUntouchedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x574144).NextBytes(large);
    var descriptor = new WadFormatDescriptor();
    using var archive = Expandable(CreateWad(writer => {
      writer.AddLump("LARGE", large);
      writer.AddMarker("S_START");
      writer.AddLump("OLD", "old"u8.ToArray());
      writer.AddMarker("S_END");
    }));

    archive.Position = 0;
    using var before = new WadReader(archive, leaveOpen: true);
    var oldDirectoryOffset = ReadDirectoryOffset(archive);
    var largeOffset = before.Entries.Single(entry => entry.Name == "LARGE").DataOffset;

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.bin", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "A WAD add should read the directory, not the 4 MiB untouched lump.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "A WAD add should write only changed payload, directory, and header fields.");

    archive.Position = 0;
    using var after = new WadReader(archive, leaveOpen: true);
    Assert.That(after.Entries.Single(entry => entry.Name == "LARGE").DataOffset, Is.EqualTo(largeOffset));
    Assert.That(after.Entries.Single(entry => entry.Name == "NEW.BIN").DataOffset, Is.EqualTo(oldDirectoryOffset));
    Assert.That(after.Entries.Select(entry => entry.Name),
      Is.EqualTo(new[] { "LARGE", "S_START", "OLD", "S_END", "NEW.BIN" }));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.Name == "LARGE")), Is.EqualTo(large));
  }

  [Test]
  public void Replace_WipesOnlySupersededPayload_AndKeepsOffsetsStable() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x5752504C).NextBytes(large);
    var descriptor = new WadFormatDescriptor();
    using var archive = Expandable(CreateWad(writer => {
      writer.AddLump("LARGE", large);
      writer.AddLump("REPLACE", "old value"u8.ToArray());
    }));

    archive.Position = 0;
    using var before = new WadReader(archive, leaveOpen: true);
    var oldDirectoryOffset = ReadDirectoryOffset(archive);
    var largeOffset = before.Entries.Single(entry => entry.Name == "LARGE").DataOffset;
    var replaced = before.Entries.Single(entry => entry.Name == "REPLACE");

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("replace", "replacement"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024));
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));
    Assert.That(archive.ToArray().AsSpan(replaced.DataOffset, replaced.Size).ToArray(),
      Is.All.EqualTo((byte)0));

    archive.Position = 0;
    using var after = new WadReader(archive, leaveOpen: true);
    Assert.That(after.Entries.Single(entry => entry.Name == "LARGE").DataOffset, Is.EqualTo(largeOffset));
    var replacement = after.Entries.Single(entry => entry.Name == "REPLACE");
    Assert.That(replacement.DataOffset, Is.EqualTo(oldDirectoryOffset));
    Assert.That(after.Extract(replacement), Is.EqualTo("replacement"u8.ToArray()));
  }

  [Test]
  public void Remove_RewritesOnlyDirectory_AndWipesRemovedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x57524D56).NextBytes(large);
    var descriptor = new WadFormatDescriptor();
    using var archive = Expandable(CreateWad(writer => {
      writer.AddLump("LARGE", large);
      writer.AddMarker("M_START");
      writer.AddLump("REMOVE", "delete me"u8.ToArray());
      writer.AddMarker("M_END");
    }));

    archive.Position = 0;
    using var before = new WadReader(archive, leaveOpen: true);
    var oldDirectoryOffset = ReadDirectoryOffset(archive);
    var largeOffset = before.Entries.Single(entry => entry.Name == "LARGE").DataOffset;
    var removed = before.Entries.Single(entry => entry.Name == "REMOVE");

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024));
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));
    Assert.That(archive.ToArray().AsSpan(removed.DataOffset, removed.Size).ToArray(),
      Is.All.EqualTo((byte)0));

    archive.Position = 0;
    using var after = new WadReader(archive, leaveOpen: true);
    Assert.That(ReadDirectoryOffset(archive), Is.EqualTo(oldDirectoryOffset));
    Assert.That(after.Entries.Select(entry => entry.Name),
      Is.EqualTo(new[] { "LARGE", "M_START", "M_END" }));
    Assert.That(after.Entries.Single(entry => entry.Name == "LARGE").DataOffset, Is.EqualTo(largeOffset));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.Name == "LARGE")), Is.EqualTo(large));
  }

  [Test]
  public void Add_PreservesIwadMagic_AndWriterNameNormalization() {
    var descriptor = new WadFormatDescriptor();
    using var archive = Expandable(CreateWad(writer => writer.AddLump("OLD", [1]), isIwad: true));

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("directory/verylongname.bin", [2, 3])]);

    archive.Position = 0;
    using var reader = new WadReader(archive, leaveOpen: true);
    Assert.That(reader.IsIwad, Is.True);
    Assert.That(reader.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "OLD", "VERYLONG" }));
    Assert.That(reader.Extract(reader.Entries.Single(entry => entry.Name == "VERYLONG")), Is.EqualTo(new byte[] { 2, 3 }));
  }

  private static byte[] CreateWad(Action<WadWriter> populate, bool isIwad = false) {
    using var output = new MemoryStream();
    using (var writer = new WadWriter(output, leaveOpen: true, isIwad: isIwad))
      populate(writer);
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 64 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
  }

  private static int ReadDirectoryOffset(Stream archive) {
    var original = archive.Position;
    Span<byte> header = stackalloc byte[12];
    archive.Position = 0;
    archive.ReadExactly(header);
    archive.Position = original;
    return BitConverter.ToInt32(header[8..12]);
  }

  private sealed class CountingStream(Stream inner) : Stream {
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) {
      var read = inner.Read(buffer, offset, count);
      this.BytesRead += read;
      return read;
    }

    public override int Read(Span<byte> buffer) {
      var read = inner.Read(buffer);
      this.BytesRead += read;
      return read;
    }

    public override void Write(byte[] buffer, int offset, int count) {
      inner.Write(buffer, offset, count);
      this.BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      inner.Write(buffer);
      this.BytesWritten += buffer.Length;
    }
  }
}
