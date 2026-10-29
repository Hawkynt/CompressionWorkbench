using Compression.Registry;
using Compression.Tests.Csc;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Archive-level behaviour of <see cref="ZpaqWriter"/> and <see cref="ZpaqReader"/>. Whether zpaq
/// itself agrees is pinned by <see cref="ZpaqReferenceVectorTests"/> (its archives) and
/// <see cref="ZpaqExternalOracleTests"/> (live, both directions).
/// </summary>
[TestFixture]
public class ZpaqTests {
  private static readonly DateTime When = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

  private static byte[] Write(string method, params (string Name, byte[]? Data)[] entries) {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true, method) { TransactionDate = When }) {
      foreach (var (name, data) in entries)
        if (data == null)
          w.AddDirectory(name);
        else
          w.AddFile(name, data);
    }
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] archive) {
    using var r = new ZpaqReader(new MemoryStream(archive));
    return r.Entries.ToDictionary(e => e.FileName, r.ExtractBytes);
  }

  // ── round trips ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EveryPredefinedMethod_RoundTripsMixedFiles([Values("0", "1", "2", "3", "4", "5")] string method) {
    var text = CscSamples.Text(30_000, 11);
    var archive = Write(method,
      ("docs/", null),
      ("docs/text.txt", text),
      ("bin/x86.bin", CscSamples.X86(20_000, 12)),
      ("bin/random.bin", CscSamples.RandomBytes(6_000, 13)),
      ("table.bin", CscSamples.Table(12_000)),
      ("empty.dat", []));

    var files = ReadAll(archive);

    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "docs/", "docs/text.txt", "bin/x86.bin", "bin/random.bin", "table.bin", "empty.dat" }));
      Assert.That(files["docs/text.txt"], Is.EqualTo(text));
      Assert.That(files["bin/x86.bin"], Is.EqualTo(CscSamples.X86(20_000, 12)));
      Assert.That(files["bin/random.bin"], Is.EqualTo(CscSamples.RandomBytes(6_000, 13)));
      Assert.That(files["table.bin"], Is.EqualTo(CscSamples.Table(12_000)));
      Assert.That(files["empty.dat"], Is.Empty);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ExplicitMethodStrings_RoundTrip([Values("x4.0ci1.1.1.1.2aw2mm16tst", "x4.3ci1", "x4.7ci1", "x4.2.12.0.7.25.1c0.0.511i2", "x4.6.4.0.3.24c0.0.511", "x4.1.4.0.3.24")] string method) {
    var data = CscSamples.Mixed();
    var files = ReadAll(Write(method, ("mixed.bin", data)));
    Assert.That(files["mixed.bin"], Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void HigherMethods_CompressTextBetter() {
    var text = CscSamples.Text(60_000, 14);
    var sizes = new[] { "0", "1", "3", "5" }.Select(m => Write(m, ("t.txt", text)).Length).ToArray();
    Assert.That(sizes, Is.Ordered.Descending);
  }

  // ── deduplication ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void IdenticalFiles_AreStoredOnce() {
    var data = CscSamples.RandomBytes(50_000, 15);
    var one = Write("1", ("a.bin", data));
    var two = Write("1", ("a.bin", data), ("copy/a.bin", data));

    using var r = new ZpaqReader(new MemoryStream(two));
    var a = r.Entries.Single(e => e.FileName == "a.bin");
    var b = r.Entries.Single(e => e.FileName == "copy/a.bin");
    Assert.Multiple(() => {
      Assert.That(two.Length - one.Length, Is.LessThan(400), "the copy costs only its index record");
      Assert.That(b.Fragments, Is.EqualTo(a.Fragments));
      Assert.That(r.ExtractBytes(b), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void SharedContentInsideDifferentFiles_IsDeduplicatedByFragment() {
    var common = CscSamples.RandomBytes(300_000, 16);
    var x = (byte[])[.. CscSamples.RandomBytes(20_000, 17), .. common];
    var y = (byte[])[.. CscSamples.RandomBytes(30_000, 18), .. common];
    var together = Write("1", ("x", x), ("y", y)).Length;
    var apart = Write("1", ("x", x)).Length + Write("1", ("y", y)).Length;
    var files = ReadAll(Write("1", ("x", x), ("y", y)));
    Assert.Multiple(() => {
      Assert.That(together, Is.LessThan(apart - 200_000), "content-defined fragments realign after the differing prefix");
      Assert.That(files["x"], Is.EqualTo(x));
      Assert.That(files["y"], Is.EqualTo(y));
    });
  }

  // ── boundaries ───────────────────────────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void FileSizesAroundFragmentLimits_RoundTrip([Values(1, 4095, 4096, 4097, 520_191, 520_192, 520_193, 1_100_000)] int size) {
    var data = CscSamples.RandomBytes(size, size);
    var files = ReadAll(Write("1", ("f.bin", data)));
    Assert.That(files["f.bin"], Is.EqualTo(data));
  }

  [Test, Category("BoundaryCase")]
  public void DataLargerThanOneBlock_SpansSeveralBlocks() {
    // Method 10 = 1 MiB blocks, so 2.5 MiB of incompressible data needs three d blocks.
    var data = CscSamples.RandomBytes(2_600_000, 19);
    var archive = Write("10", ("big.bin", data));
    var files = ReadAll(archive);
    Assert.Multiple(() => {
      Assert.That(files["big.bin"], Is.EqualTo(data));
      Assert.That(CountOccurrences(archive, "d0000000"u8.ToArray()), Is.GreaterThanOrEqualTo(3));
    });
  }

  [Test, Category("BoundaryCase")]
  public void OnlyDirectories_ProduceAnIndexWithoutDataBlocks() {
    var files = ReadAll(Write("1", ("a/", null), ("a/b/", null)));
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "a/", "a/b/" }));
  }

  [Test, Category("BoundaryCase")]
  public void NoEntries_WritesNothing_AndEmptyInputReadsAsEmptyArchive() {
    var archive = Write("1");
    using var r = new ZpaqReader(new MemoryStream(archive));
    Assert.Multiple(() => {
      Assert.That(archive, Is.Empty, "zpaq truncates an update that adds nothing");
      Assert.That(r.Entries, Is.Empty);
      Assert.That(r.VersionCount, Is.Zero);
    });
  }

  [Test, Category("HappyPath")]
  public void Utf8Names_AndDates_Survive() {
    var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true) { TransactionDate = When })
      w.AddFile("ünï/cödé ✓.txt", "x"u8.ToArray(), stamp);
    using var r = new ZpaqReader(new MemoryStream(ms.ToArray()));
    var e = r.Entries.Single();
    Assert.Multiple(() => {
      Assert.That(e.FileName, Is.EqualTo("ünï/cödé ✓.txt"));
      Assert.That(e.LastModified, Is.EqualTo(stamp));
      Assert.That(r.GetVersionDate(1), Is.EqualTo(When));
      Assert.That(e.Version, Is.EqualTo(1));
    });
  }

  // ── stream behaviour and errors ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void NonSeekableInput_IsBuffered() {
    var archive = Write("1", ("a.txt", "hello"u8.ToArray()));
    using var r = new ZpaqReader(new ForwardOnlyStream(archive));
    Assert.That(r.ExtractBytes(r.Entries.Single()), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void Constructor_NullStream_Throws() => Assert.That(() => new ZpaqReader(null!), Throws.ArgumentNullException);

  [Test, Category("Exception")]
  public void NotAnArchive_ThrowsInvalidData() =>
    Assert.That(() => new ZpaqReader(new MemoryStream("This is not a ZPAQ archive."u8.ToArray())), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exception")]
  public void EncryptedArchive_IsRefused() {
    // An encrypted archive starts with its 32-byte random salt (never '7' or 'z' first).
    var salt = Enumerable.Range(1, 64).Select(i => (byte)(i * 37)).ToArray();
    Assert.That(() => new ZpaqReader(new MemoryStream(salt)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("encrypted"));
  }

  [Test, Category("Exception")]
  public void CorruptedData_FailsTheFragmentChecksum() {
    var data = CscSamples.Text(40_000, 20);
    var archive = Write("0", ("t.txt", data));
    // Method 0 stores fragments verbatim, so flipping a byte of the text corrupts content, not structure.
    var at = IndexOf(archive, data.AsSpan(1000, 32).ToArray());
    archive[at + 5] ^= 0x20;
    using var r = new ZpaqReader(new MemoryStream(archive));
    Assert.That(() => r.ExtractBytes(r.Entries.Single()), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("checksum"));
  }

  [Test, Category("Exception")]
  public void Writer_RejectsBadArguments() {
    using var ms = new MemoryStream();
    Assert.Multiple(() => {
      Assert.That(() => new ZpaqWriter(ms, true, "q"), Throws.ArgumentException);
      Assert.That(() => new ZpaqWriter(ms, true, ""), Throws.ArgumentException);
      using var w = new ZpaqWriter(ms, true);
      Assert.That(() => w.AddFile("dir/", []), Throws.ArgumentException);
      Assert.That(() => w.AddFile(null!, []), Throws.ArgumentNullException);
    });
  }

  // ── descriptor ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_CreateHonoursLevel_AndListsEntries() {
    var d = new ZpaqFormatDescriptor();
    var text = CscSamples.Text(20_000, 21);
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a/t.txt", text)], new FormatCreateOptions { Level = 4 });
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.Multiple(() => {
      Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "a/t.txt" }));
      Assert.That(list[0].OriginalSize, Is.EqualTo(text.Length));
    });
  }

  [Test, Category("Exception")]
  public void Descriptor_List_FallsBackInsteadOfThrowing() {
    var d = new ZpaqFormatDescriptor();
    var list = d.List(new MemoryStream("7kSt garbage that is not a block"u8.ToArray()), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "FULL.zpaq", "metadata.ini" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_MethodMapping() {
    Assert.Multiple(() => {
      Assert.That(ZpaqFormatDescriptor.MethodFor(new FormatCreateOptions()), Is.EqualTo("1"));
      Assert.That(ZpaqFormatDescriptor.MethodFor(new FormatCreateOptions { Level = 9 }), Is.EqualTo("5"));
      Assert.That(ZpaqFormatDescriptor.MethodFor(new FormatCreateOptions("x4.3ci1")), Is.EqualTo("x4.3ci1"));
      Assert.That(ZpaqFormatDescriptor.MethodFor(new FormatCreateOptions("zpaq")), Is.EqualTo("1"));
    });
  }

  private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

  private static int CountOccurrences(byte[] haystack, byte[] needle) {
    var count = 0;
    for (var span = haystack.AsSpan(); ;) {
      var i = span.IndexOf(needle);
      if (i < 0)
        return count;
      ++count;
      span = span[(i + needle.Length)..];
    }
  }

  private sealed class ForwardOnlyStream(byte[] data) : Stream {
    private readonly MemoryStream _inner = new(data);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
