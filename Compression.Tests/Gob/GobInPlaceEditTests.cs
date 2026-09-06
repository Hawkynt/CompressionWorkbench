#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Gob;

namespace Compression.Tests.Gob;

[TestFixture]
public sealed class GobInPlaceEditTests {
  private const long IoBudget = 128 * 1024;
  private const int DirectoryEntrySize = 136;
  private const int NameFieldSize = 128;

  [Test, Category("ByteIdentity")]
  public void Add_WritesNewPayloadAtOldDirectoryOffset_AndKeepsSurvivorOffset() {
    var keep = Pattern(8192, 7);
    var original = Build(("data\\keep.bin", keep));
    var oldDirectoryOffset = DirectoryOffset(original);
    var oldKeepOffset = Entries(original)["data\\keep.bin"].Offset;

    using var stream = Load(original);
    GobInPlaceModifier.AddFile(stream, "data\\new.bin", "tiny"u8.ToArray());
    var result = stream.ToArray();
    var entries = Entries(result);

    Assert.Multiple(() => {
      Assert.That(entries["data\\new.bin"].Offset, Is.EqualTo(oldDirectoryOffset));
      Assert.That(entries["data\\keep.bin"].Offset, Is.EqualTo(oldKeepOffset));
      Assert.That(Read(result, "data\\keep.bin"), Is.EqualTo(keep));
      Assert.That(Read(result, "data\\new.bin"), Is.EqualTo("tiny"u8.ToArray()));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorAdd_DoesNotReadOrRewriteFourMiBSibling() {
    var keep = Pattern(4 * 1024 * 1024, 13);
    var original = Build(("large\\keep.bin", keep), ("small.txt", "seed"u8.ToArray()));
    var oldOffset = Entries(original)["large\\keep.bin"].Offset;

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new GobFormatDescriptor().Add(counted, [ArchiveInputInfo.InMemory("added.txt", "small"u8.ToArray())]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget), $"add read {reads} archive bytes");
      Assert.That(writes, Is.LessThan(IoBudget), $"add wrote {writes} archive bytes");
      Assert.That(Entries(result)["large\\keep.bin"].Offset, Is.EqualTo(oldOffset));
      Assert.That(Read(result, "large\\keep.bin"), Is.EqualTo(keep));
      Assert.That(Read(result, "added.txt"), Is.EqualTo("small"u8.ToArray()));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorReplace_WipesOldPayloadWithoutTouchingLargeSibling() {
    var keep = Pattern(4 * 1024 * 1024, 29);
    var victim = Pattern(4096, 31);
    var original = Build(("keep.bin", keep), ("victim.bin", victim));
    var before = Entries(original);
    var keepOffset = before["keep.bin"].Offset;
    var victimEntry = before["victim.bin"];

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new GobFormatDescriptor().Add(counted, [ArchiveInputInfo.InMemory("victim.bin", "replacement"u8.ToArray())]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget));
      Assert.That(writes, Is.LessThan(IoBudget));
      Assert.That(Entries(result)["keep.bin"].Offset, Is.EqualTo(keepOffset));
      Assert.That(Read(result, "keep.bin"), Is.EqualTo(keep));
      Assert.That(Read(result, "victim.bin"), Is.EqualTo("replacement"u8.ToArray()));
      Assert.That(result.AsSpan((int)victimEntry.Offset, (int)victimEntry.Size).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorRemove_RewritesDirectoryAndWipesOnlyVictim() {
    var keep = Pattern(4 * 1024 * 1024, 37);
    var victim = Pattern(4096, 41);
    var original = Build(("keep.bin", keep), ("victim.bin", victim));
    var before = Entries(original);
    var keepOffset = before["keep.bin"].Offset;
    var victimEntry = before["victim.bin"];

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new GobFormatDescriptor().Remove(counted, ["victim.bin"]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget));
      Assert.That(writes, Is.LessThan(IoBudget));
      Assert.That(Entries(result).ContainsKey("victim.bin"), Is.False);
      Assert.That(Entries(result)["keep.bin"].Offset, Is.EqualTo(keepOffset));
      Assert.That(Read(result, "keep.bin"), Is.EqualTo(keep));
      Assert.That(result.AsSpan((int)victimEntry.Offset, (int)victimEntry.Size).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("EdgeCase")]
  public void RemoveMissingName_PerformsZeroWrites() {
    var original = Build(("keep.bin", Pattern(1024, 3)));
    using var inner = Load(original);
    using var counted = new CountingStream(inner);

    Assert.That(GobInPlaceModifier.RemoveFile(counted, "ghost.bin"), Is.False);
    Assert.That(counted.BytesWritten, Is.Zero);
    Assert.That(inner.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void RemoveAlias_DoesNotWipeSharedPayload() {
    var original = BuildAliased();
    var before = Entries(original);
    Assert.That(before["a.bin"].Offset, Is.EqualTo(before["b.bin"].Offset));

    using var stream = Load(original);
    Assert.That(GobInPlaceModifier.RemoveFile(stream, "a.bin"), Is.True);
    var result = stream.ToArray();

    Assert.Multiple(() => {
      Assert.That(Entries(result).ContainsKey("a.bin"), Is.False);
      Assert.That(Read(result, "b.bin"), Is.EqualTo("shared-data"u8.ToArray()));
    });
  }

  [Test, Category("Layout")]
  public void DescriptorLayout_IncludesHeaderPayloadAndDirectory() {
    var original = Build(("x.bin", Pattern(32, 1)));
    using var stream = new MemoryStream(original, writable: false);
    var blocks = new GobFormatDescriptor().EnumerateLayout(stream).ToArray();

    Assert.Multiple(() => {
      Assert.That(blocks.Any(block => block.Offset == 0 && block.Length == 12 && block.Kind == DefragBlockKind.MetadataReserved), Is.True);
      Assert.That(blocks.Any(block => block.FileName == "x.bin" && block.Kind == DefragBlockKind.Used), Is.True);
      Assert.That(blocks.Any(block => block.Offset == DirectoryOffset(original) && block.Kind == DefragBlockKind.MetadataReserved), Is.True);
    });
  }

  private static byte[] Build(params (string Name, byte[] Data)[] entries) {
    using var stream = new MemoryStream();
    using (var writer = new GobWriter(stream, leaveOpen: true))
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
    return stream.ToArray();
  }

  private static byte[] BuildAliased() {
    var bytes = Build(("a.bin", "shared-data"u8.ToArray()));
    var directoryOffset = DirectoryOffset(bytes);
    var oldLength = bytes.Length;
    Array.Resize(ref bytes, oldLength + DirectoryEntrySize);
    bytes.AsSpan(directoryOffset + 4, DirectoryEntrySize)
      .CopyTo(bytes.AsSpan(directoryOffset + 4 + DirectoryEntrySize, DirectoryEntrySize));
    bytes.AsSpan(directoryOffset + 4 + DirectoryEntrySize + 8, NameFieldSize).Clear();
    Encoding.ASCII.GetBytes("b.bin").CopyTo(bytes, directoryOffset + 4 + DirectoryEntrySize + 8);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset, 4), 2);
    return bytes;
  }

  private static int DirectoryOffset(byte[] archive)
    => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(8, 4)));

  private static Dictionary<string, GobEntry> Entries(byte[] archive) {
    using var stream = new MemoryStream(archive, writable: false);
    using var reader = new GobReader(stream);
    return reader.Entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
  }

  private static byte[] Read(byte[] archive, string name) {
    using var stream = new MemoryStream(archive, writable: false);
    using var reader = new GobReader(stream);
    var entry = reader.Entries.Single(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(entry);
  }

  private static byte[] Pattern(int size, int seed) {
    var result = new byte[size];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)((i * 137 + seed) & 0xFF);
    return result;
  }

  private static MemoryStream Load(byte[] bytes) {
    var stream = new MemoryStream();
    stream.Write(bytes);
    stream.Position = 0;
    return stream;
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
    public override int ReadByte() {
      var value = inner.ReadByte();
      if (value >= 0) ++this.BytesRead;
      return value;
    }
    public override void Write(byte[] buffer, int offset, int count) {
      inner.Write(buffer, offset, count);
      this.BytesWritten += count;
    }
    public override void Write(ReadOnlySpan<byte> buffer) {
      inner.Write(buffer);
      this.BytesWritten += buffer.Length;
    }
    public override void WriteByte(byte value) {
      inner.WriteByte(value);
      ++this.BytesWritten;
    }
    protected override void Dispose(bool disposing) { }
  }
}
