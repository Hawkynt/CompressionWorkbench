#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.TrDos;

namespace Compression.Tests.TrDos;

[TestFixture]
public class TrDosModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "GREET", (byte)'C', "hello-trdos"u8.ToArray());
    ms.Position = 0;
    var reader = new TrDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name.StartsWith("GREET"));
    Assert.That(Encoding.ASCII.GetString(reader.Extract(entry)).TrimEnd('\0').StartsWith("hello-trdos"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 8 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 11) & 0xFF);
    TrDosModifier.AddFile(ms, "BIG", (byte)'C', data);

    ms.Position = 0;
    var reader = new TrDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name.StartsWith("BIG"));
    var got = reader.Extract(entry);
    Assert.That(got.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddMultiple_ReadsBackAll() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "ALPHA", (byte)'C', new byte[100]);
    TrDosModifier.AddFile(ms, "BETA",  (byte)'B', new byte[200]);
    TrDosModifier.AddFile(ms, "GAMMA", (byte)'D', new byte[300]);

    ms.Position = 0;
    var reader = new TrDosReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("ALPHA")), Is.True);
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("BETA")),  Is.True);
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("GAMMA")), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "OLD", (byte)'C', new byte[1000]);
    Assert.That(TrDosModifier.RemoveFile(ms, "OLD"), Is.True);
    TrDosModifier.AddFile(ms, "NEW", (byte)'C', new byte[1000]);

    ms.Position = 0;
    var reader = new TrDosReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("OLD")), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("NEW")), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(TrDosModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "SECRET", (byte)'C', "TOPSECRET-MARKER-TRDOS"u8.ToArray());
    Assert.That(TrDosModifier.RemoveFile(ms, "SECRET"), Is.True);
    var asAscii = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-TRDOS"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_UsesDeletedMarker() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "DELME", (byte)'C', new byte[10]);
    TrDosModifier.RemoveFile(ms, "DELME");
    // First byte of the directory entry must be 0x01 (TR-DOS deleted marker).
    Assert.That(ms.ToArray()[0], Is.EqualTo(0x01));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_BumpsDeletedCount() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "A", (byte)'C', new byte[10]);
    TrDosModifier.AddFile(ms, "B", (byte)'C', new byte[10]);
    TrDosModifier.RemoveFile(ms, "A");
    var raw = ms.ToArray();
    // disk-info sector at 0x800; deleted-files count at offset 0xF4.
    Assert.That(raw[0x800 + 0xF4], Is.EqualTo(1));
    // file count at 0xE4 should now be 1.
    Assert.That(raw[0x800 + 0xE4], Is.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void DeletedSlot_IsReusedByNextAdd() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "A", (byte)'C', new byte[10]);
    TrDosModifier.RemoveFile(ms, "A");
    TrDosModifier.AddFile(ms, "B", (byte)'C', new byte[10]);

    // Slot 0 (the previously-deleted one) must now hold the new entry,
    // proving deleted slots are reused rather than appended after.
    var raw = ms.ToArray();
    var slot0Name = Encoding.ASCII.GetString(raw, 0, 8).TrimEnd();
    Assert.That(slot0Name, Is.EqualTo("B"));
  }

  [Test, Category("Performance")]
  public void AddSmallFile_TouchesOnlyMetadataAndDataSectors() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    TrDosModifier.AddFile(counter, "TINY", (byte)'C', "x"u8.ToArray());

    var totalIo = counter.BytesRead + counter.BytesWritten;
    // Dir read (8 sectors = 2048) + info read (256) + 1 data sector (256+pad)
    // + 1 dir-sector write (256) + info write (256). Should be far below the
    // full 655 360-byte image.
    Assert.That(totalIo, Is.LessThan(16 * 256),
      $"Add of a 1-byte file should touch < 16 sectors; touched {totalIo} bytes.");
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    TrDosModifier.AddFile(counter, "TINY", (byte)'C', "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / ms.Length;
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P1} of the image; should be O(touched bytes).");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new TrDosFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF", false)]);

      ms.Position = 0;
      var reader = new TrDosReader(ms);
      Assert.That(reader.Entries.Any(e => e.Name.StartsWith("VIAIF")), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "GONE", (byte)'C', new byte[10]);
    ((IArchiveModifiable)new TrDosFormatDescriptor()).Remove(ms, ["GONE"]);

    ms.Position = 0;
    var reader = new TrDosReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name.StartsWith("GONE")), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddReplaces_SameName() {
    var ms = BuildEmptyImage();
    TrDosModifier.AddFile(ms, "REPL", (byte)'C', "first-content"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "second-content"u8.ToArray());
      ((IArchiveModifiable)new TrDosFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "REPL", false)]);

      ms.Position = 0;
      var reader = new TrDosReader(ms);
      var matches = reader.Entries.Where(e => e.Name.StartsWith("REPL")).ToList();
      Assert.That(matches, Has.Count.EqualTo(1));
      var got = Encoding.ASCII.GetString(reader.Extract(matches[0]));
      Assert.That(got, Does.StartWith("second-content"));
      Assert.That(got, Does.Not.Contain("first-content"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void CanModify_FlagSet() {
    var d = new TrDosFormatDescriptor();
    Assert.That((d.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify));
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new TrDosWriter().Build());
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
