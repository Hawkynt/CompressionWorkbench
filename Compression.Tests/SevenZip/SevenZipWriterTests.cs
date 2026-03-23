using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipWriterTests {
  [Category("Exception")]
  [Test]
  public void Write_NonSeekableStream_Throws() {
    using var ms = new MemoryStream();
    using var nonSeekable = new NonSeekableStream(ms);
    Assert.Throws<ArgumentException>(() => _ = new SevenZipWriter(nonSeekable));
  }

  [Category("Exception")]
  [Test]
  public void AddEntry_AfterFinish_Throws() {
    var ms = new MemoryStream();
    using var writer = new SevenZipWriter(ms, leaveOpen: true);
    writer.Finish();
    Assert.Throws<InvalidOperationException>(
      () => writer.AddEntry(new SevenZipEntry { Name = "test.bin" }, ReadOnlySpan<byte>.Empty));
  }

  [Category("Exception")]
  [Test]
  public void AddDirectory_AfterFinish_Throws() {
    var ms = new MemoryStream();
    using var writer = new SevenZipWriter(ms, leaveOpen: true);
    writer.Finish();
    Assert.Throws<InvalidOperationException>(() => writer.AddDirectory("dir"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Finish_CalledTwice_IsIdempotent() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using var writer = new SevenZipWriter(ms, leaveOpen: true);
    writer.AddEntry(new SevenZipEntry { Name = "test.bin", Size = data.Length }, data);
    writer.Finish();
    var lengthAfterFirst = ms.Length;
    writer.Finish(); // Should be a no-op
    Assert.That(ms.Length, Is.EqualTo(lengthAfterFirst));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void AddEntry_FromStream() {
    var data = System.Text.Encoding.UTF8.GetBytes("from stream");
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      using var dataStream = new MemoryStream(data);
      writer.AddEntry(new SevenZipEntry { Name = "stream.txt" }, dataStream);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Dispose_FinishesAndClosesStream() {
    byte[] data = [99];
    var ms = new MemoryStream();
    var writer = new SevenZipWriter(ms, leaveOpen: true);
    writer.AddEntry(new SevenZipEntry { Name = "test.bin", Size = 1 }, data);
    writer.Dispose();

    // Archive should be valid after dispose
    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  // ── Filter tests ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_Copy_RoundTrip() {
    var data = "Copy filter test"u8.ToArray();
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Copy))
      writer.AddEntry(new SevenZipEntry { Name = "copy.txt" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_BcjX86_RoundTrip() {
    // Create data with some E8/E9 instructions to exercise BCJ
    var data = new byte[1024];
    var rng = new Random(42);
    rng.NextBytes(data);
    // Scatter some E8 (CALL) instructions
    for (var i = 0; i < data.Length - 5; i += 20)
      data[i] = 0xE8;

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.BcjX86))
      writer.AddEntry(new SevenZipEntry { Name = "code.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_Delta_RoundTrip() {
    // Data with correlated values (good for delta)
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 3);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Delta, deltaDistance: 1))
      writer.AddEntry(new SevenZipEntry { Name = "delta.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_BcjArm_RoundTrip() {
    var data = new byte[512];
    new Random(123).NextBytes(data);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.BcjArm))
      writer.AddEntry(new SevenZipEntry { Name = "arm.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_WithDeflateCodec_RoundTrip() {
    var data = "Filter + Deflate combo"u8.ToArray();
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Deflate, leaveOpen: true,
        filter: SevenZipFilter.BcjX86))
      writer.AddEntry(new SevenZipEntry { Name = "combo.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_MultipleFiles_RoundTrip() {
    var f1 = "file1 data"u8.ToArray();
    var f2 = "file2 data"u8.ToArray();

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Delta, deltaDistance: 2)) {
      writer.AddEntry(new SevenZipEntry { Name = "f1.bin" }, f1);
      writer.AddEntry(new SevenZipEntry { Name = "f2.bin" }, f2);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(f1));
    Assert.That(reader.Extract(1), Is.EqualTo(f2));
  }

  // ── BCJ2 tests ─────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_Bcj2_RoundTrip() {
    // Create data with E8/E9 instructions to exercise BCJ2
    var data = new byte[2048];
    var rng = new Random(99);
    rng.NextBytes(data);
    for (var i = 0; i < data.Length - 5; i += 30)
      data[i] = 0xE8;

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Bcj2))
      writer.AddEntry(new SevenZipEntry { Name = "code.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_Bcj2_MultipleFiles_RoundTrip() {
    var f1 = new byte[1024];
    new Random(42).NextBytes(f1);
    for (var i = 0; i < f1.Length - 5; i += 20)
      f1[i] = 0xE8;

    var f2 = "Some text data for BCJ2 test"u8.ToArray();

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Bcj2)) {
      writer.AddEntry(new SevenZipEntry { Name = "code.bin" }, f1);
      writer.AddEntry(new SevenZipEntry { Name = "text.txt" }, f2);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(f1));
    Assert.That(reader.Extract(1), Is.EqualTo(f2));
  }

  // ── Encryption tests ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypt_Aes256_RoundTrip() {
    var data = "Secret 7z content with AES-256!"u8.ToArray();
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        password: "test123"))
      writer.AddEntry(new SevenZipEntry { Name = "secret.txt" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms, password: "test123");
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypt_Aes256_WithFilter_RoundTrip() {
    var data = new byte[512];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 3);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        filter: SevenZipFilter.Delta, deltaDistance: 1, password: "mypass"))
      writer.AddEntry(new SevenZipEntry { Name = "encrypted_delta.bin" }, data);

    ms.Position = 0;
    using var reader = new SevenZipReader(ms, password: "mypass");
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypt_Aes256_MultipleFiles_RoundTrip() {
    var f1 = "first encrypted file"u8.ToArray();
    var f2 = new byte[200];
    for (var i = 0; i < f2.Length; ++i) f2[i] = (byte)(i % 13);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        password: "pass"))  {
      writer.AddEntry(new SevenZipEntry { Name = "f1.txt" }, f1);
      writer.AddEntry(new SevenZipEntry { Name = "f2.bin" }, f2);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms, password: "pass");
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(f1));
    Assert.That(reader.Extract(1), Is.EqualTo(f2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncryptHeaders_RoundTrip() {
    var f1 = "header encryption test"u8.ToArray();
    var f2 = new byte[100];
    for (var i = 0; i < f2.Length; ++i) f2[i] = (byte)(i % 7);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true,
        password: "secret", encryptHeaders: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "visible.txt" }, f1);
      writer.AddEntry(new SevenZipEntry { Name = "hidden.bin" }, f2);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms, password: "secret");
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("visible.txt"));
    Assert.That(reader.Extract(0), Is.EqualTo(f1));
    Assert.That(reader.Extract(1), Is.EqualTo(f2));
  }

  // ── Copy codec test ────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CopyCodec_SingleBlock_RoundTrip() {
    var data = new byte[256];
    new Random(42).NextBytes(data);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "random.bin" }, data);
      writer.FinishWithBlocks([
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [0],
          Codec = SevenZipCodec.Copy,
        },
      ]);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(256));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  // ── Multi-codec FinishWithBlocks tests ──────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void FinishWithBlocks_TwoCodecs_RoundTrip() {
    // Block 0: text files → LZMA2
    // Block 1: binary data → Copy (store)
    var text1 = "Hello world, this is text data for LZMA2 compression."u8.ToArray();
    var text2 = "Another text file with similar content patterns."u8.ToArray();
    var binary = new byte[256];
    new Random(42).NextBytes(binary);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "text1.txt" }, text1);
      writer.AddEntry(new SevenZipEntry { Name = "text2.txt" }, text2);
      writer.AddEntry(new SevenZipEntry { Name = "random.bin" }, binary);

      writer.FinishWithBlocks([
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [0, 1],
          Codec = SevenZipCodec.Lzma2,
        },
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [2],
          Codec = SevenZipCodec.Copy,
        },
      ]);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    // Check sizes first to diagnose issues
    Assert.That(reader.Entries[0].Size, Is.EqualTo(text1.Length), "text1 size");
    Assert.That(reader.Entries[1].Size, Is.EqualTo(text2.Length), "text2 size");
    Assert.That(reader.Entries[2].Size, Is.EqualTo(binary.Length), "binary size");
    Assert.That(reader.Extract(0), Is.EqualTo(text1));
    Assert.That(reader.Extract(1), Is.EqualTo(text2));
    Assert.That(reader.Extract(2), Is.EqualTo(binary));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void FinishWithBlocks_ThreeCodecs_RoundTrip() {
    var src = "int main() { return 0; }"u8.ToArray();
    var xml = "<root><item>value</item></root>"u8.ToArray();
    var jpg = new byte[128];
    new Random(99).NextBytes(jpg);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "main.c" }, src);
      writer.AddEntry(new SevenZipEntry { Name = "config.xml" }, xml);
      writer.AddEntry(new SevenZipEntry { Name = "photo.jpg" }, jpg);

      writer.FinishWithBlocks([
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [0],
          Codec = SevenZipCodec.Lzma2,
          Filter = SevenZipFilter.BcjX86,
        },
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [1],
          Codec = SevenZipCodec.Deflate,
        },
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [2],
          Codec = SevenZipCodec.Copy,
        },
      ]);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    Assert.That(reader.Extract(0), Is.EqualTo(src));
    Assert.That(reader.Extract(1), Is.EqualTo(xml));
    Assert.That(reader.Extract(2), Is.EqualTo(jpg));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void FinishWithBlocks_WithDirectories_RoundTrip() {
    var data = "File inside directory"u8.ToArray();

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      writer.AddDirectory("folder/");
      writer.AddEntry(new SevenZipEntry { Name = "folder/file.txt" }, data);

      writer.FinishWithBlocks([
        new SevenZipWriter.BlockDescriptor {
          EntryIndices = [0],
          Codec = SevenZipCodec.Lzma2,
        },
      ]);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    var fileIdx = Enumerable.Range(0, reader.Entries.Count).First(i => !reader.Entries[i].IsDirectory);
    Assert.That(reader.Extract(fileIdx), Is.EqualTo(data));
  }

  /// <summary>
  /// A non-seekable wrapper around a stream for testing.
  /// </summary>
  private sealed class NonSeekableStream : Stream {
    private readonly Stream _inner;

    public NonSeekableStream(Stream inner) => this._inner = inner;
    public override bool CanRead => this._inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => this._inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
