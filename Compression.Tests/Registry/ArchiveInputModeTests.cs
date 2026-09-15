using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Registry;

/// <summary>
/// Verifies the universal archive input bridges. The fake descriptor intentionally
/// implements only the historical seek-dependent Stream methods; every new mode
/// must therefore work through the default registry contract rather than through
/// format-specific test code.
/// </summary>
[TestFixture]
public sealed class ArchiveInputModeTests {
  private static readonly byte[] ArchiveBytes = "TESTpayload"u8.ToArray();
  private static readonly byte[] EntryBytes = "payload"u8.ToArray();

  [Test, Category("Spec")]
  public void ListStreaming_ForwardOnlySource_SpoolsAndLists() {
    IArchiveFormatOperations ops = new SeekOnlyArchiveOperations();
    using var inner = new MemoryStream(ArchiveBytes, writable: false);
    using var source = new NonSeekableReadStream(inner);

    var entries = ops.ListStreaming(source, password: null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("payload.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(EntryBytes.Length));
  }

  [Test, Category("Spec")]
  public void ListSeekable_ForwardOnlySource_RejectsCapabilityMismatch() {
    IArchiveFormatOperations ops = new SeekOnlyArchiveOperations();
    using var inner = new MemoryStream(ArchiveBytes, writable: false);
    using var source = new NonSeekableReadStream(inner);

    Assert.That(
      () => ops.ListSeekable(source, password: null),
      Throws.ArgumentException.With.Message.Contains("must support seeking"));
  }

  [Test, Category("Spec")]
  public void ListSpan_ConcreteDescriptor_UsesUniversalSpanSurface() {
    var ops = new SeekOnlyArchiveOperations();

    var entries = ops.List(ArchiveBytes.AsSpan(), password: null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("payload.bin"));
  }

  [Test, Category("Spec")]
  public void ExtractStreaming_ForwardOnlySource_ExtractsSelectedEntry() {
    IArchiveFormatOperations ops = new SeekOnlyArchiveOperations();
    using var inner = new MemoryStream(ArchiveBytes, writable: false);
    using var source = new NonSeekableReadStream(inner);
    var outputDir = Path.Combine(Path.GetTempPath(), $"cwb-input-mode-{Guid.NewGuid():N}");

    try {
      ops.ExtractStreaming(source, outputDir, password: null, files: ["payload.bin"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outputDir, "payload.bin")), Is.EqualTo(EntryBytes));
    } finally {
      try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("Spec")]
  public void OpenEntryStreaming_ForwardOnlySource_KeepsSpoolAliveUntilEntryIsDisposed() {
    IArchiveFormatOperations ops = new SeekOnlyArchiveOperations();
    using var inner = new MemoryStream(ArchiveBytes, writable: false);
    using var source = new NonSeekableReadStream(inner);

    using var entry = ops.OpenEntryStreaming(source, "payload.bin", password: null);
    using var sink = new MemoryStream();
    entry.CopyTo(sink);

    Assert.That(sink.ToArray(), Is.EqualTo(EntryBytes));
  }

  [Test, Category("Spec")]
  public void OpenEntrySpan_OwnsCompatibilityBufferForReturnedStreamLifetime() {
    var ops = new SeekOnlyArchiveOperations();

    using var entry = ops.OpenEntry(ArchiveBytes.AsSpan(), "payload.bin", password: null);
    using var sink = new MemoryStream();
    entry.CopyTo(sink);

    Assert.That(sink.ToArray(), Is.EqualTo(EntryBytes));
  }

  private sealed class SeekOnlyArchiveOperations : IArchiveFormatOperations {
    public List<ArchiveEntryInfo> List(Stream stream, string? password) {
      VerifyHeader(stream);
      return [new ArchiveEntryInfo(
        0,
        "payload.bin",
        EntryBytes.Length,
        EntryBytes.Length,
        "stored",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null)];
    }

    public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
      VerifyHeader(stream);
      if (files is { Length: > 0 } && !files.Contains("payload.bin", StringComparer.Ordinal))
        return;

      Directory.CreateDirectory(outputDir);
      stream.Position = 4;
      var data = new byte[EntryBytes.Length];
      stream.ReadExactly(data);
      File.WriteAllBytes(Path.Combine(outputDir, "payload.bin"), data);
    }

    public Stream OpenEntry(Stream archive, string entryName, string? password) {
      if (!string.Equals(entryName, "payload.bin", StringComparison.Ordinal))
        throw new FileNotFoundException("Entry not found.", entryName);
      VerifyHeader(archive);
      archive.Position = 4;
      return new BoundedEntryStream(archive, EntryBytes.Length, leaveOpen: true);
    }

    private static void VerifyHeader(Stream stream) {
      if (!stream.CanSeek)
        throw new NotSupportedException("This fake represents a legacy seek-only descriptor.");
      stream.Position = 0;
      Span<byte> header = stackalloc byte[4];
      stream.ReadExactly(header);
      if (!header.SequenceEqual("TEST"u8))
        throw new InvalidDataException("Bad test archive header.");
    }
  }

  private sealed class NonSeekableReadStream(Stream inner) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
