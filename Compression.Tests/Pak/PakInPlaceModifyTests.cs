#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Pak;

namespace Compression.Tests.Pak;

/// <summary>
/// Changed-byte tests for real Quake PACK archives. The physical contract is a
/// trailing directory, not ARC's entry-chain/EOA layout.
/// </summary>
[TestFixture]
public class PakInPlaceModifyTests {
  private const long IoBudget = 128 * 1024;

  [Test, Category("ByteIdentity")]
  public void AddFile_WritesPayloadAtOldDirectoryOffset_AndKeepsExistingOffset() {
    var keep = Pattern(8192, 17);
    var original = BuildSeedPak(("keep.bin", keep));
    var oldDirectoryOffset = DirectoryOffset(original);
    var oldKeepOffset = EntryMap(original)["keep.bin"].FileOffset;

    using var stream = Load(original);
    PakInPlaceModifier.AddFile(stream, "new.bin", "small"u8.ToArray());
    var result = stream.ToArray();
    var entries = EntryMap(result);

    Assert.Multiple(() => {
      Assert.That(entries["new.bin"].FileOffset, Is.EqualTo(oldDirectoryOffset));
      Assert.That(entries["keep.bin"].FileOffset, Is.EqualTo(oldKeepOffset));
      Assert.That(ReadEntry(result, "keep.bin"), Is.EqualTo(keep));
      Assert.That(ReadEntry(result, "new.bin"), Is.EqualTo("small"u8.ToArray()));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorAdd_LeavesFourMiBUntouched_AndStaysUnderIoBudget() {
    var keep = Pattern(4 * 1024 * 1024, 23);
    var original = BuildSeedPak(("large/keep.bin", keep), ("small.txt", "seed"u8.ToArray()));
    var oldOffset = EntryMap(original)["large/keep.bin"].FileOffset;

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new PakFormatDescriptor().Add(counted, [ArchiveInputInfo.InMemory("added.txt", "tiny"u8.ToArray())]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget), $"add read {reads} archive bytes");
      Assert.That(writes, Is.LessThan(IoBudget), $"add wrote {writes} archive bytes");
      Assert.That(EntryMap(result)["large/keep.bin"].FileOffset, Is.EqualTo(oldOffset));
      Assert.That(ReadEntry(result, "large/keep.bin"), Is.EqualTo(keep));
      Assert.That(ReadEntry(result, "added.txt"), Is.EqualTo("tiny"u8.ToArray()));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorReplace_DoesNotReadLargeSibling_AndWipesOldPayload() {
    var keep = Pattern(4 * 1024 * 1024, 31);
    var victim = Pattern(4096, 7);
    var original = BuildSeedPak(("keep.bin", keep), ("victim.bin", victim));
    var before = EntryMap(original);
    var oldKeepOffset = before["keep.bin"].FileOffset;
    var oldVictim = before["victim.bin"];

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new PakFormatDescriptor().Add(counted, [ArchiveInputInfo.InMemory("victim.bin", "replacement"u8.ToArray())]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget));
      Assert.That(writes, Is.LessThan(IoBudget));
      Assert.That(EntryMap(result)["keep.bin"].FileOffset, Is.EqualTo(oldKeepOffset));
      Assert.That(ReadEntry(result, "keep.bin"), Is.EqualTo(keep));
      Assert.That(ReadEntry(result, "victim.bin"), Is.EqualTo("replacement"u8.ToArray()));
      Assert.That(result.AsSpan(oldVictim.FileOffset, oldVictim.Size).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("Performance")]
  public void DescriptorRemove_RewritesOnlyDirectoryAndRemovedPayload() {
    var keep = Pattern(4 * 1024 * 1024, 43);
    var victim = Pattern(4096, 11);
    var original = BuildSeedPak(("keep.bin", keep), ("victim.bin", victim));
    var before = EntryMap(original);
    var oldKeepOffset = before["keep.bin"].FileOffset;
    var oldVictim = before["victim.bin"];

    using var inner = Load(original);
    using var counted = new CountingStream(inner);
    new PakFormatDescriptor().Remove(counted, ["victim.bin"]);
    var reads = counted.BytesRead;
    var writes = counted.BytesWritten;
    var result = inner.ToArray();

    Assert.Multiple(() => {
      Assert.That(reads, Is.LessThan(IoBudget));
      Assert.That(writes, Is.LessThan(IoBudget));
      Assert.That(EntryMap(result).ContainsKey("victim.bin"), Is.False);
      Assert.That(EntryMap(result)["keep.bin"].FileOffset, Is.EqualTo(oldKeepOffset));
      Assert.That(ReadEntry(result, "keep.bin"), Is.EqualTo(keep));
      Assert.That(result.AsSpan(oldVictim.FileOffset, oldVictim.Size).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("EdgeCase")]
  public void RemoveMissingName_PerformsZeroWrites() {
    var original = BuildSeedPak(("keep.bin", Pattern(1024, 5)));
    using var inner = Load(original);
    using var counted = new CountingStream(inner);

    Assert.That(PakInPlaceModifier.RemoveFile(counted, "ghost.bin"), Is.False);
    Assert.That(counted.BytesWritten, Is.Zero);
    Assert.That(inner.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void RemovingAlias_DoesNotWipeSharedPayload() {
    var original = BuildAliasedPak();
    var before = EntryMap(original);
    Assert.That(before["a.bin"].FileOffset, Is.EqualTo(before["b.bin"].FileOffset));

    using var stream = Load(original);
    Assert.That(PakInPlaceModifier.RemoveFile(stream, "a.bin", wipeData: true), Is.True);
    var result = stream.ToArray();

    Assert.Multiple(() => {
      Assert.That(EntryMap(result).ContainsKey("a.bin"), Is.False);
      Assert.That(ReadEntry(result, "b.bin"), Is.EqualTo("shared-payload"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var descriptor = new PakFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    });
  }

  private static byte[] BuildSeedPak(params (string Name, byte[] Data)[] entries) {
    using var stream = new MemoryStream();
    using (var writer = new PakWriter(stream)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
      writer.Finish();
    }
    return stream.ToArray();
  }

  private static byte[] BuildAliasedPak() {
    var bytes = BuildSeedPak(("a.bin", "shared-payload"u8.ToArray()));
    var directoryOffset = DirectoryOffset(bytes);
    var originalLength = bytes.Length;
    Array.Resize(ref bytes, originalLength + PakReader.DirectoryEntrySize);
    bytes.AsSpan(directoryOffset, PakReader.DirectoryEntrySize)
      .CopyTo(bytes.AsSpan(directoryOffset + PakReader.DirectoryEntrySize, PakReader.DirectoryEntrySize));
    bytes.AsSpan(directoryOffset + PakReader.DirectoryEntrySize, PakReader.NameFieldSize).Clear();
    "b.bin"u8.CopyTo(bytes.AsSpan(directoryOffset + PakReader.DirectoryEntrySize, PakReader.NameFieldSize));
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 2 * PakReader.DirectoryEntrySize);
    return bytes;
  }

  private static int DirectoryOffset(byte[] archive)
    => BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(4, 4));

  private static Dictionary<string, PakEntry> EntryMap(byte[] archive) {
    using var stream = new MemoryStream(archive, writable: false);
    using var reader = new PakReader(stream);
    return reader.Entries.ToDictionary(entry => entry.FileName, StringComparer.OrdinalIgnoreCase);
  }

  private static byte[] ReadEntry(byte[] archive, string name) {
    using var stream = new MemoryStream(archive, writable: false);
    using var reader = new PakReader(stream);
    while (reader.GetNextEntry() is { } entry)
      if (string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))
        return reader.ReadEntryData();
    throw new AssertionException($"Entry '{name}' not found.");
  }

  private static byte[] Pattern(int size, int seed) {
    var result = new byte[size];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)((i * 131 + seed) & 0xFF);
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
