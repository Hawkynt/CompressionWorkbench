#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Os9Rbf;

namespace Compression.Tests.Os9Rbf;

[TestFixture]
public class Os9RbfModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    Os9RbfModifier.AddFile(ms, "GREETING", "hello-os9"u8.ToArray());

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var entry = v.Files.Single(e => e.Name == "GREETING");
    var extracted = Os9RbfReader.Extract(v, entry);
    Assert.That(Encoding.ASCII.GetString(extracted), Is.EqualTo("hello-os9"));
    Assert.That(extracted.Length, Is.EqualTo(9), "FD.SIZ trim should yield exact byte length");
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 8 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    Os9RbfModifier.AddFile(ms, "BIG.DAT", data);

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var entry = v.Files.Single(e => e.Name == "BIG.DAT");
    var extracted = Os9RbfReader.Extract(v, entry);
    Assert.That(extracted, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadable() {
    var ms = BuildEmptyImage();
    Os9RbfModifier.AddFile(ms, "alpha.txt", "one"u8.ToArray());
    Os9RbfModifier.AddFile(ms, "beta.txt", "two"u8.ToArray());
    Os9RbfModifier.AddFile(ms, "gamma.txt", "three"u8.ToArray());

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Select(f => f.Name),
      Is.EquivalentTo(new[] { "alpha.txt", "beta.txt", "gamma.txt" }));
    Assert.That(Encoding.ASCII.GetString(Os9RbfReader.Extract(v, v.Files.Single(f => f.Name == "beta.txt"))),
      Is.EqualTo("two"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    Os9RbfModifier.AddFile(ms, "OLD", new byte[1000]);
    Assert.That(Os9RbfModifier.RemoveFile(ms, "OLD"), Is.True);
    Os9RbfModifier.AddFile(ms, "NEW", new byte[1000]);

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Any(f => f.Name == "OLD"), Is.False);
    Assert.That(v.Files.Any(f => f.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(Os9RbfModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    Os9RbfModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-OS9"u8.ToArray());
    Os9RbfModifier.RemoveFile(ms, "SECRET");
    var asAscii = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-OS9"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_AfterRemove_IsByteExactRoundTrip() {
    var ms = BuildEmptyImage();
    var payload = "after-remove-payload"u8.ToArray();
    Os9RbfModifier.AddFile(ms, "TMP", new byte[300]);
    Os9RbfModifier.RemoveFile(ms, "TMP");
    Os9RbfModifier.AddFile(ms, "REUSE", payload);

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var entry = v.Files.Single(e => e.Name == "REUSE");
    Assert.That(Os9RbfReader.Extract(v, entry), Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_BeyondInitialDirCapacity_ExtendsRootDir() {
    // Initial directory is 1 sector = 8 entries (incl. "." and "..")
    // → 6 user file slots. Adding 10 should force the root dir to grow.
    var ms = BuildEmptyImage();
    for (var i = 0; i < 10; i++) {
      Os9RbfModifier.AddFile(ms, $"file{i:D2}", new byte[16]);
    }
    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var names = v.Files.Select(f => f.Name).ToList();
    for (var i = 0; i < 10; i++) {
      Assert.That(names, Does.Contain($"file{i:D2}"));
    }
  }

  [Test, Category("Performance")]
  public void AddTinyFile_DoesNotScaleWithImageSize() {
    var ms = BuildEmptyImage();
    var counter = new ByteCountingStream(ms);
    Os9RbfModifier.AddFile(counter, "TINY", "x"u8.ToArray());

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
      ((IArchiveModifiable)new Os9RbfFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA-IF", false)]);

      var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
      Assert.That(v.Files.Any(f => f.Name == "VIA-IF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_FreesData() {
    var ms = BuildEmptyImage();
    Os9RbfModifier.AddFile(ms, "TOREMOVE", "marker-content-zzz"u8.ToArray());
    ((IArchiveModifiable)new Os9RbfFormatDescriptor()).Remove(ms, ["TOREMOVE"]);

    var v = Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Any(f => f.Name == "TOREMOVE"), Is.False);
  }

  [Test, Category("EdgeCase")]
  public void AddFile_TooLongName_Throws() {
    var ms = BuildEmptyImage();
    var longName = new string('a', 32);
    Assert.That(() => Os9RbfModifier.AddFile(ms, longName, [1, 2, 3]),
      Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_HasCanModifyCapability() {
    var desc = new Os9RbfFormatDescriptor();
    Assert.That((desc.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify));
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(Os9RbfWriter.Build([]));
    ms.Position = 0;
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
