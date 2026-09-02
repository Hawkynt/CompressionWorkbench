#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.UnrealPak;

namespace Compression.Tests.UnrealPak;

[TestFixture]
public sealed class UnrealPakInPlaceEditTests {
  [Test]
  public void Add_AppendsAtOldIndex_WithoutReadingUntouchedPayload() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x50414B).NextBytes(large);
    var descriptor = new UnrealPakFormatDescriptor();
    using var archive = Expandable(CreatePak([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("old.txt", "old"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new UnrealPakReader(archive);
    var oldIndexOffset = before.IndexOffset;
    var largeOffset = before.Entries.Single(entry => entry.Path == "large.bin").Offset;

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.txt", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "A trailer-only add must not scan the 4 MiB untouched payload.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "A trailer-only add should write only the new record plus index/footer.");

    archive.Position = 0;
    var after = new UnrealPakReader(archive);
    Assert.That(after.Entries.Single(entry => entry.Path == "large.bin").Offset, Is.EqualTo(largeOffset));
    Assert.That(after.Entries.Single(entry => entry.Path == "new.txt").Offset, Is.EqualTo(oldIndexOffset));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.Path == "large.bin")), Is.EqualTo(large));
    Assert.That(after.Extract(after.Entries.Single(entry => entry.Path == "new.txt")), Is.EqualTo("new payload"u8.ToArray()));
  }

  [Test]
  public void Replace_ReadsAndWipesOnlyReplacedRecord() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x5245504C).NextBytes(large);
    var descriptor = new UnrealPakFormatDescriptor();
    using var archive = Expandable(CreatePak([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("replace.txt", "old value"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new UnrealPakReader(archive);
    var oldIndexOffset = before.IndexOffset;
    var largeOffset = before.Entries.Single(entry => entry.Path == "large.bin").Offset;
    var replaced = before.Entries.Single(entry => entry.Path == "replace.txt");
    var replacedRecordLength = checked(53 + (int)replaced.Size);

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("replace.txt", "replacement"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "Replace may verify the replaced record, but must not read unrelated payloads.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "Replace should append the new record/index and wipe only the old record.");

    var bytes = archive.ToArray();
    Assert.That(bytes.AsSpan(checked((int)replaced.Offset), replacedRecordLength).ToArray(),
      Is.All.EqualTo((byte)0), "The superseded local record and payload should be wiped in place.");

    archive.Position = 0;
    var after = new UnrealPakReader(archive);
    Assert.That(after.Entries.Single(entry => entry.Path == "large.bin").Offset, Is.EqualTo(largeOffset));
    var replacement = after.Entries.Single(entry => entry.Path == "replace.txt");
    Assert.That(replacement.Offset, Is.EqualTo(oldIndexOffset));
    Assert.That(after.Extract(replacement), Is.EqualTo("replacement"u8.ToArray()));
  }

  [Test]
  public void Remove_RewritesTrailerAndWipesTarget_WithoutMovingSurvivors() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x524D5645).NextBytes(large);
    var descriptor = new UnrealPakFormatDescriptor();
    using var archive = Expandable(CreatePak([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("remove.txt", "delete me"u8.ToArray()),
    ]));

    archive.Position = 0;
    var before = new UnrealPakReader(archive);
    var oldIndexOffset = before.IndexOffset;
    var largeOffset = before.Entries.Single(entry => entry.Path == "large.bin").Offset;
    var removed = before.Entries.Single(entry => entry.Path == "remove.txt");
    var removedRecordLength = checked(53 + (int)removed.Size);

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "Remove should verify only the target record plus the trailing index.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "Remove should rewrite the trailer and wipe only the target record.");

    var bytes = archive.ToArray();
    Assert.That(bytes.AsSpan(checked((int)removed.Offset), removedRecordLength).ToArray(),
      Is.All.EqualTo((byte)0));

    archive.Position = 0;
    var after = new UnrealPakReader(archive);
    Assert.That(after.IndexOffset, Is.EqualTo(oldIndexOffset));
    Assert.That(after.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "large.bin" }));
    Assert.That(after.Entries.Single().Offset, Is.EqualTo(largeOffset));
    Assert.That(after.Extract(after.Entries.Single()), Is.EqualTo(large));
  }

  [Test]
  public void Add_PreservesMountPoint_WhenCallerUsesListedPath() {
    var descriptor = new UnrealPakFormatDescriptor();
    using var archive = Expandable(CreatePak(
      [ArchiveInputInfo.InMemory("old.txt", "old"u8.ToArray())],
      new FormatCreateOptions {
        MethodName = "stored",
        FormatSpecific = new Dictionary<string, string> { ["MountPoint"] = "../../../Game/" },
      }));

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("Game/new.txt", "new"u8.ToArray())]);

    archive.Position = 0;
    var reader = new UnrealPakReader(archive);
    Assert.That(reader.MountPoint, Is.EqualTo("../../../Game/"));
    Assert.That(reader.Entries.Select(entry => entry.Path).Order(), Is.EqualTo(new[] { "new.txt", "old.txt" }));
    archive.Position = 0;
    Assert.That(descriptor.List(archive, null).Select(entry => entry.Name).Order(),
      Is.EqualTo(new[] { "Game/new.txt", "Game/old.txt" }));
  }

  private static byte[] CreatePak(IReadOnlyList<ArchiveInputInfo> inputs)
    => CreatePak(inputs, new FormatCreateOptions { MethodName = "stored" });

  private static byte[] CreatePak(IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var descriptor = new UnrealPakFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(output, inputs, options);
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 64 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
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
