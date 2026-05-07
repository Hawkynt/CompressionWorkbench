#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Ar;

namespace Compression.Tests.Ar;

[TestFixture]
public class ArModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedAr();
    ArModifier.AddFile(ms, "added.txt", "hello-ar"u8.ToArray());
    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries["added.txt"], Is.EqualTo("hello-ar"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedAr();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    ArModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new ArReader(ms);
    var found = reader.Entries.FirstOrDefault(e => e.Name == "big.bin");
    Assert.That(found, Is.Not.Null);
    Assert.That(found!.Data, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_OddSize_AlignmentPadIsClean() {
    var ms = BuildSeedAr();
    // Odd-length data triggers the 0x0A padding byte after the entry.
    ArModifier.AddFile(ms, "odd.txt", "odd"u8.ToArray()); // 3 bytes
    ArModifier.AddFile(ms, "after.txt", "after"u8.ToArray()); // 5 bytes (also odd)

    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries["odd.txt"], Is.EqualTo("odd"));
    Assert.That(entries["after.txt"], Is.EqualTo("after"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedAr();
    ArModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    ArModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(ArModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries.ContainsKey("keeper.txt"), Is.True);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedAr();
    Assert.That(ArModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FirstEntry_ShiftsRemainder() {
    var ms = BuildSeedAr();
    ArModifier.AddFile(ms, "second.txt", "second"u8.ToArray());
    ArModifier.AddFile(ms, "third.txt", "third"u8.ToArray());
    Assert.That(ArModifier.RemoveFile(ms, "seed.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries.ContainsKey("seed.txt"), Is.False);
    Assert.That(entries["second.txt"], Is.EqualTo("second"));
    Assert.That(entries["third.txt"], Is.EqualTo("third"));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedAr();
    ArModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    ArModifier.RemoveFile(ms, "doc.txt");
    ArModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries["doc.txt"], Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("Performance")]
  public void AddTinyFile_TouchesOnlyHeadersAndNewBytes() {
    // Build a 4MB seed with a single huge entry; Add should walk only the
    // (single) header chain to EOF and write our new bytes.
    var ms = BuildLargeSeedAr(megabytes: 4);
    var seedLen = ms.Length;
    var counter = new ByteCountingStream(ms);
    ArModifier.AddFile(counter, "tiny.txt", "x"u8.ToArray());

    var ratio = (double)(counter.BytesRead + counter.BytesWritten) / seedLen;
    Assert.That(ratio, Is.LessThan(0.01),
      $"Add of a 1-byte file touched {ratio:P1} of a 4MB archive; should walk headers only.");
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedAr();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new ArFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(new ArReader(ms));
      Assert.That(entries["via-if.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeedAr();
    ArModifier.AddFile(ms, "victim.txt", "x"u8.ToArray());
    ((IArchiveModifiable)new ArFormatDescriptor()).Remove(ms, ["victim.txt"]);

    ms.Position = 0;
    var entries = ReadAll(new ArReader(ms));
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new ArFormatDescriptor();
    Assert.That((d.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify));
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedAr() {
    var ms = new MemoryStream();
    using (var w = new ArWriter(ms, leaveOpen: true)) {
      w.Write([new ArEntry { Name = "seed.txt", Data = "seed-content"u8.ToArray() }]);
    }
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static MemoryStream BuildLargeSeedAr(int megabytes) {
    var ms = new MemoryStream();
    var bigBuf = new byte[megabytes * 1024 * 1024];
    Random.Shared.NextBytes(bigBuf);
    using (var w = new ArWriter(ms, leaveOpen: true)) {
      w.Write([new ArEntry { Name = "seed.bin", Data = bigBuf }]);
    }
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static Dictionary<string, string> ReadAll(ArReader r) {
    var result = new Dictionary<string, string>();
    foreach (var e in r.Entries)
      result[e.Name] = System.Text.Encoding.ASCII.GetString(e.Data);
    return result;
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
      var n = _inner.Read(buffer, offset, count); BytesRead += n; return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count); BytesWritten += count;
    }
  }
}
