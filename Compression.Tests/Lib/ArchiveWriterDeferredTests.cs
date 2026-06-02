using Compression.Lib;

namespace Compression.Tests.Lib;

/// <summary>
/// Validates the deferred-length write path on <see cref="ArchiveWriter"/>:
/// the no-length <see cref="ArchiveWriter.CreateFileEntry(string, DateTime?)"/>
/// overload, the auto-pick <see cref="ArchiveWriter.AddEntry(string, Stream, DateTime?)"/>
/// (and FileInfo / byte[] / ROSpan overloads), and mixed up-front / deferred
/// archives.
/// </summary>
[TestFixture]
public class ArchiveWriterDeferredTests {

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-wf-defer-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryClean(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
  }

  /// <summary>A Stream wrapper that reports CanSeek=false regardless of
  /// what the wrapped MemoryStream supports, so the auto-pick logic must
  /// fall through to the deferred path.</summary>
  private sealed class NonSeekableStream : Stream {
    private readonly Stream _inner;
    public NonSeekableStream(Stream inner) { this._inner = inner; }
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>A producer-only Stream that emits a fixed pattern of bytes
  /// without supporting seek or length queries — simulates a real
  /// generator (e.g. piping decompressor output through the writer).</summary>
  private sealed class GeneratorStream : Stream {
    private readonly byte[] _payload;
    private int _position;
    public GeneratorStream(byte[] payload) { this._payload = payload; }
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) {
      var remaining = this._payload.Length - this._position;
      if (remaining <= 0) return 0;
      var n = Math.Min(count, remaining);
      Array.Copy(this._payload, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  [Test, Category("HappyPath")]
  public void CreateFileEntry_WithoutLength_DeferredRoundTrip() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "deferred.zip");
      var payload = "Deferred-length entry payload."u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        // No-length overload — exercises the DeferredLengthWriteStream path.
        using var es = w.CreateFileEntry("notes/deferred.txt");
        es.Write(payload, 0, payload.Length);
      }

      Assert.That(File.Exists(archive), Is.True);
      using var reader = ArchiveReader.Open(archive);
      var entry = reader.Files.Single();
      Assert.That(entry.Name, Is.EqualTo("notes/deferred.txt"));
      using var s = entry.OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Writer_MixedUpfrontAndDeferred_BothEntriesPresent() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "mixed.zip");
      var upfrontPayload = "UPFRONT"u8.ToArray();
      var deferredPayload = "DEFERRED-and-then-some"u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        using (var es1 = w.CreateFileEntry("a.txt", upfrontPayload.LongLength)) {
          es1.Write(upfrontPayload, 0, upfrontPayload.Length);
        }
        using (var es2 = w.CreateFileEntry("b.txt")) {
          es2.Write(deferredPayload, 0, deferredPayload.Length);
        }
      }

      using var reader = ArchiveReader.Open(archive);
      var entries = reader.Files.OrderBy(e => e.Name).ToList();
      Assert.That(entries.Count, Is.EqualTo(2));

      using var s1 = entries[0].OpenRead();
      using var sink1 = new MemoryStream();
      s1.CopyTo(sink1);
      Assert.That(sink1.ToArray(), Is.EqualTo(upfrontPayload));

      using var s2 = entries[1].OpenRead();
      using var sink2 = new MemoryStream();
      s2.CopyTo(sink2);
      Assert.That(sink2.ToArray(), Is.EqualTo(deferredPayload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromFileStream_TakesUpfrontPath() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "from-file.zip");
      var src = Path.Combine(dir, "src.bin");
      var payload = new byte[1024];
      Random.Shared.NextBytes(payload);
      File.WriteAllBytes(src, payload);

      int deferredBefore;
      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        deferredBefore = w.DeferredEntriesIssued;
        w.AddEntry("data.bin", new FileInfo(src));
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(deferredBefore),
          "FileInfo source has a known length — must take the zero-buffer path");
      }

      using var reader = ArchiveReader.Open(archive);
      using var s = reader.Files.Single().OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromMemoryStream_TakesUpfrontPath() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "from-mem.zip");
      var payload = "memstream-payload"u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        var before = w.DeferredEntriesIssued;
        using var src = new MemoryStream(payload, writable: false);
        w.AddEntry("p.txt", src);
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(before),
          "MemoryStream is seekable + known length — must take the zero-buffer path");
      }
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromBoundedEntryStream_TakesUpfrontPath() {
    // The convert-archive happy path: source's OpenEntry returns a
    // BoundedEntryStream with CanSeek=true and Length=LogicalSize, so
    // AddEntry must pick the zero-buffer path without instantiating a
    // DeferredLengthWriteStream.
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.zip");
      var dst = Path.Combine(dir, "dst.zip");
      var payload = "bounded-entry-payload"u8.ToArray();

      using (var w = ArchiveWriter.Create(src, "Zip")) {
        using var es = w.CreateFileEntry("inner.txt", payload.LongLength);
        es.Write(payload, 0, payload.Length);
      }

      using (var reader = ArchiveReader.Open(src))
      using (var writer = ArchiveWriter.Create(dst, "Zip")) {
        var before = writer.DeferredEntriesIssued;
        foreach (var e in reader.Files) {
          using var s = e.OpenRead();
          // s is a BoundedEntryStream (CanSeek + Length both work).
          writer.AddEntry(e.Name, s);
        }
        Assert.That(writer.DeferredEntriesIssued, Is.EqualTo(before),
          "BoundedEntryStream advertises CanSeek + Length — must take zero-buffer path");
      }

      // Sanity: round-trip survived.
      using var r2 = ArchiveReader.Open(dst);
      using var s2 = r2.Files.Single().OpenRead();
      using var sink = new MemoryStream();
      s2.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromNonSeekableStream_TakesDeferredPath() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "from-nonseek.zip");
      var payload = "non-seekable-payload"u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        var before = w.DeferredEntriesIssued;
        using var memInner = new MemoryStream(payload, writable: false);
        using var src = new NonSeekableStream(memInner);
        w.AddEntry("p.txt", src);
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(before + 1),
          "non-seekable source must trigger the deferred path");
      }

      using var reader = ArchiveReader.Open(archive);
      using var s = reader.Files.Single().OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromGeneratorStream_TakesDeferredPath() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "from-gen.zip");
      var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        var before = w.DeferredEntriesIssued;
        using var gen = new GeneratorStream(payload);
        w.AddEntry("gen.bin", gen);
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(before + 1),
          "custom generator stream must trigger the deferred path");
      }

      using var reader = ArchiveReader.Open(archive);
      using var s = reader.Files.Single().OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void ConvertArchive_BoundedEntryStreamSource_HasCanSeekAndLength() {
    // ConvertArchive's streaming pipeline uses srcOps.OpenEntry(...) and
    // passes the result to writer.AddEntry(name, stream). The contract is
    // that the source's OpenEntry returns a BoundedEntryStream with
    // CanSeek=true and Length=entry.Size — which makes AddEntry pick the
    // zero-buffer path automatically. This test verifies the contract end
    // to end via a real ZIP→ZIP convert; if any source descriptor ever
    // returns a non-seekable bounded view, the convert path would silently
    // start spilling, which we want to catch.
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.zip");
      var dst = Path.Combine(dir, "dst.zip");
      var payload = "convert-archive-roundtrip"u8.ToArray();

      using (var w = ArchiveWriter.Create(src, "Zip")) {
        using var es = w.CreateFileEntry("entry.txt", payload.LongLength);
        es.Write(payload, 0, payload.Length);
      }

      // ConvertArchive routes the source's OpenEntry result through
      // writer.AddEntry, which picks the zero-buffer path iff CanSeek + Length
      // both work.
      var warnings = Compression.Lib.ArchiveOperations.ConvertArchive(src, dst);
      Assert.That(File.Exists(dst), Is.True);
      Assert.That(warnings, Is.Not.Null);

      using var r = ArchiveReader.Open(dst);
      using var s = r.Files.Single().OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void AddEntry_FromByteArray_TakesUpfrontPath() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "from-bytes.zip");
      var payload = "byte[]-payload"u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        var before = w.DeferredEntriesIssued;
        w.AddEntry("p.txt", payload);
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(before),
          "byte[] source has a known length — must take the zero-buffer path");
        // ROSpan overload as well.
        ReadOnlySpan<byte> span = payload;
        w.AddEntry("q.txt", span);
        Assert.That(w.DeferredEntriesIssued, Is.EqualTo(before),
          "ROSpan source has a known length — must take the zero-buffer path");
      }

      using var reader = ArchiveReader.Open(archive);
      Assert.That(reader.Files.Count(), Is.EqualTo(2));
    } finally { TryClean(dir); }
  }
}
