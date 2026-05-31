#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

[TestFixture]
public class IsoModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    IsoModifier.AddFile(ms, "HELLO.TXT", "world-iso"u8.ToArray());
    ms.Position = 0;
    var reader = new IsoReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "HELLO.TXT");
    Assert.That(Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("world-iso"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExisting() {
    var ms = BuildSeededImage(("FIRST.TXT", "alpha"u8.ToArray()));
    IsoModifier.AddFile(ms, "SECOND.TXT", "beta"u8.ToArray());
    ms.Position = 0;
    var reader = new IsoReader(ms);
    var byName = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(
      e => e.Name, e => Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName, Has.Count.EqualTo(2));
    Assert.That(byName["FIRST.TXT"], Is.EqualTo("alpha"));
    Assert.That(byName["SECOND.TXT"], Is.EqualTo("beta"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_Replace_ReplacesByName() {
    var ms = BuildSeededImage(("DOC.TXT", "v1"u8.ToArray()));
    IsoModifier.AddFile(ms, "DOC.TXT", "v2-replacement"u8.ToArray());
    ms.Position = 0;
    var reader = new IsoReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(Encoding.ASCII.GetString(reader.Extract(reader.Entries[0])),
      Is.EqualTo("v2-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[12_000]; // 6 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13) & 0xFF);
    IsoModifier.AddFile(ms, "BIG.DAT", data);
    ms.Position = 0;
    var reader = new IsoReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "BIG.DAT");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsTargetEntry() {
    var ms = BuildSeededImage(
      ("KEEP.TXT", "stay"u8.ToArray()),
      ("DROP.TXT", "go"u8.ToArray()),
      ("ALSO.TXT", "stay too"u8.ToArray())
    );
    Assert.That(IsoModifier.RemoveFile(ms, "DROP.TXT"), Is.True);
    ms.Position = 0;
    var reader = new IsoReader(ms);
    Assert.That(reader.Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "KEEP.TXT", "ALSO.TXT" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(IsoModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildSeededImage(
      ("KEEP.TXT", "harmless"u8.ToArray()),
      ("SECRET.TXT", "TOPSECRET-MARKER-ISO-XYZ"u8.ToArray())
    );
    Assert.That(IsoModifier.RemoveFile(ms, "SECRET.TXT"), Is.True);
    var asAscii = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-ISO-XYZ"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesSlotForReuse() {
    var ms = BuildSeededImage(("OLD.TXT", new byte[5_000]));
    Assert.That(IsoModifier.RemoveFile(ms, "OLD.TXT"), Is.True);
    IsoModifier.AddFile(ms, "NEW.TXT", "fresh"u8.ToArray());
    ms.Position = 0;
    var reader = new IsoReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "OLD.TXT"), Is.False);
    var newEntry = reader.Entries.Single(e => e.Name == "NEW.TXT");
    Assert.That(Encoding.ASCII.GetString(reader.Extract(newEntry)), Is.EqualTo("fresh"));
  }

  [Test, Category("RoundTrip")]
  public void AddRemove_Sequence_RoundTrips() {
    var ms = BuildSeededImage(("A.TXT", "alpha"u8.ToArray()));
    IsoModifier.AddFile(ms, "B.TXT", "beta"u8.ToArray());
    IsoModifier.AddFile(ms, "C.TXT", "gamma"u8.ToArray());
    Assert.That(IsoModifier.RemoveFile(ms, "A.TXT"), Is.True);
    ms.Position = 0;
    var reader = new IsoReader(ms);
    Assert.That(reader.Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "B.TXT", "C.TXT" }));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_SanitizesLowercaseToUppercase() {
    // ECMA-119 (the primary tree) sanitizes identifiers to uppercase 8.3; the
    // parallel Joliet tree preserves the original mixed case. Inspect the
    // primary tree (Joliet disabled) to pin the sanitization behaviour.
    var ms = BuildEmptyImage();
    IsoModifier.AddFile(ms, "lower.txt", "x"u8.ToArray());
    ms.Position = 0;
    var primary = new IsoReader(ms, useJoliet: false);
    Assert.That(primary.Entries.Any(e => e.Name == "LOWER.TXT"), Is.True);

    // The default (Joliet-preferred) reader returns the original mixed-case name.
    ms.Position = 0;
    var joliet = new IsoReader(ms);
    Assert.That(joliet.Entries.Any(e => e.Name == "lower.txt"), Is.True);
  }

  [Test, Category("Performance")]
  public void AddTinyFile_TouchesBoundedAmount() {
    // Seed with 5 files so the image isn't trivially small.
    var ms = BuildSeededImage(
      ("F1.DAT", new byte[2048]),
      ("F2.DAT", new byte[2048]),
      ("F3.DAT", new byte[2048]),
      ("F4.DAT", new byte[2048]),
      ("F5.DAT", new byte[2048])
    );
    var counter = new ByteCountingStream(ms);
    IsoModifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Touch budget: PVD read+write (1 sector each) + RemoveFile probe (1 PVD read,
    // 1 root read, no write) + root sector read+write + 1 data sector write +
    // SetLength tail write. Bound at 16 sectors (32 KB) to flag any regression.
    Assert.That(totalIo, Is.LessThan(16 * 2048),
      $"Add of a 1-byte file should touch < 16 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    // Build a "big" image (~1 MB of file data) so the per-image ratio is dominated
    // by image size, not by the constant overhead. The modifier should still touch
    // only a handful of sectors regardless.
    var seed = new (string Name, byte[] Data)[10];
    for (var i = 0; i < seed.Length; i++)
      seed[i] = ($"F{i}.DAT", new byte[100_000]);
    var ms = BuildSeededImage(seed);
    var imageBefore = ms.Length;
    var counter = new ByteCountingStream(ms);
    IsoModifier.AddFile(counter, "TINY.TXT", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / imageBefore;
    Assert.That(ratio, Is.LessThan(0.05),
      $"Add of a 1-byte file touched {ratio:P1} of the image ({imageBefore} bytes); should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-iso"u8.ToArray());
      ((IArchiveModifiable)new IsoFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF.TXT", false)]);

      ms.Position = 0;
      var reader = new IsoReader(ms);
      var entry = reader.Entries.Single(e => e.Name == "VIAIF.TXT");
      Assert.That(Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("hello-iso"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_UsesInPlacePath() {
    var ms = BuildSeededImage(
      ("KEEP.TXT", "stay"u8.ToArray()),
      ("DROP.TXT", "go"u8.ToArray())
    );
    ((IArchiveModifiable)new IsoFormatDescriptor()).Remove(ms, ["DROP.TXT"]);
    ms.Position = 0;
    var reader = new IsoReader(ms);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "KEEP.TXT" }));
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new IsoWriter().Build());
    return ms;
  }

  private static MemoryStream BuildSeededImage(params (string Name, byte[] Data)[] files) {
    var w = new IsoWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
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
}
