#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.AppleDos;

namespace Compression.Tests.AppleDos;

[TestFixture]
public class AppleDosModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    AppleDosModifier.AddFile(ms, "GREETING", "hello-apple"u8.ToArray());
    ms.Position = 0;
    var reader = new AppleDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "GREETING");
    var extracted = reader.Extract(entry);
    // Default file type is Binary (0x04) which has no length prefix; logical size = sector count * 256.
    // Trim trailing zeros to recover the payload.
    var trimmed = TrimTrailingZeros(extracted);
    Assert.That(System.Text.Encoding.ASCII.GetString(trimmed), Is.EqualTo("hello-apple"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 8 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    AppleDosModifier.AddFile(ms, "BIG", data);

    ms.Position = 0;
    var reader = new AppleDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG");
    var extracted = reader.Extract(entry);
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    AppleDosModifier.AddFile(ms, "OLD", new byte[1000]);
    Assert.That(AppleDosModifier.RemoveFile(ms, "OLD"), Is.True);
    AppleDosModifier.AddFile(ms, "NEW", new byte[1000]);

    ms.Position = 0;
    var reader = new AppleDosReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(AppleDosModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    AppleDosModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-APPLE"u8.ToArray());
    AppleDosModifier.RemoveFile(ms, "SECRET");
    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-APPLE"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    AppleDosModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Expected: 1 VTOC read + 1 data write + 1 T/S list write + 1 dir read + 1 dir write + 1 VTOC write ≈ 6 sectors.
    Assert.That(totalIo, Is.LessThan(12 * 256),
      $"Add of a 1-byte file should touch < 12 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    AppleDosModifier.AddFile(counter, "TINY", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new AppleDosFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      ms.Position = 0;
      var reader = new AppleDosReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name == "VIA-IF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new AppleDosWriter().Build());
    return ms;
  }

  private static byte[] TrimTrailingZeros(byte[] data) {
    var n = data.Length;
    while (n > 0 && data[n - 1] == 0) n--;
    var r = new byte[n];
    Buffer.BlockCopy(data, 0, r, 0, n);
    return r;
  }

  private sealed class ByteCountingStream : Stream {
    private readonly Stream _inner;
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public ByteCountingStream(Stream inner) { _inner = inner; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) {
      var n = _inner.Read(buffer, offset, count);
      BytesRead += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count);
      BytesWritten += count;
    }
  }

  /// <summary>
  /// A file added to an existing volume must read back whole.
  /// </summary>
  /// <remarks>
  /// A DOS 3.3 binary file begins with a two-byte load address and a two-byte
  /// length, and that length is how the catalog's reader knows where the file
  /// ends. Creating a volume wrote those four bytes; adding to one did not, and
  /// stored the payload under the same binary file type. The reader then took
  /// the payload's own third and fourth bytes for a length: a file of 3,169
  /// bytes came back as 225, which is exactly what those bytes spell.
  /// </remarks>
  [Test, Category("Regression")]
  public void AddedFilesKeepTheirBinaryHeader() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps("AppleDos")!;

    byte[] Payload(int length, int seed) {
      var data = new byte[length];
      for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed * 7 + (i >> 11));
      return data;
    }

    var first = Payload(2048, 1);
    using var image = new MemoryStream();
    ((Compression.Registry.IArchiveCreatable)ops).Create(image,
      [Compression.Registry.ArchiveInputInfo.InMemory("FIRST.BIN", first)],
      new Compression.Registry.FormatCreateOptions());

    // Lengths whose own third and fourth bytes say something far shorter, which
    // is what made the truncation visible rather than subtle.
    var added = new Dictionary<string, byte[]> {
      ["ADD01.BIN"] = Payload(3169, 901),
      ["ADD02.BIN"] = Payload(3266, 902),
    };
    image.Position = 0;
    ((Compression.Registry.IArchiveModifiable)ops).Add(image,
      [.. added.Select(kv => Compression.Registry.ArchiveInputInfo.InMemory(kv.Key, kv.Value))]);

    var outDir = Path.Combine(Path.GetTempPath(), "adosadd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((Compression.Registry.IArchiveFormatOperations)ops).Extract(image, outDir, null, null);
      foreach (var (name, want) in added) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(path, Is.Not.Null, $"{name} is missing after being added");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(want), $"{name} did not read back whole");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }
}
