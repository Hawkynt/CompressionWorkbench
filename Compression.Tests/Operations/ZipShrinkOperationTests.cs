#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the ZIP <see cref="IArchiveShrinkable"/> operation: trailing junk
/// after the end-of-central-directory record is dropped, every entry still
/// extracts byte-identically, and a tight archive is left untouched (idempotent).
/// </summary>
[TestFixture]
public class ZipShrinkOperationTests {

  private static byte[] BuildZip(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddEntry(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(Stream s) {
    s.Position = 0;
    var r = new ZipReader(s, leaveOpen: true);
    var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var e in r.Entries)
      if (!e.IsDirectory)
        map[e.FileName] = r.ExtractEntry(e);
    return map;
  }

  [Test]
  public void ZipDescriptorImplementsShrinkable() {
    var d = new ZipFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(d.CanonicalSizes, Has.Count.EqualTo(1));
  }

  [Test]
  public void Shrink_DropsTrailingJunk_AndPreservesEntries() {
    var a = new byte[1000];
    Array.Fill(a, (byte)0x11);
    var b = "hello world"u8.ToArray();
    var zip = BuildZip(("a.bin", a), ("b.txt", b));

    // Append forensic trailing junk after the EOCD.
    using var input = new MemoryStream();
    input.Write(zip);
    var junk = new byte[512];
    Array.Fill(junk, (byte)0xCC);
    input.Write(junk);
    input.Position = 0;

    using var output = new MemoryStream();
    new ZipFormatDescriptor().Shrink(input, output);

    Assert.That(output.Length, Is.EqualTo(zip.Length), "trailing junk must be trimmed");

    var got = ReadAll(output);
    Assert.That(got["a.bin"], Is.EqualTo(a));
    Assert.That(got["b.txt"], Is.EqualTo(b));
  }

  [Test]
  public void Shrink_IsIdempotent_OnTightArchive() {
    var zip = BuildZip(("x", "data"u8.ToArray()));
    using var input = new MemoryStream(zip);
    using var output = new MemoryStream();
    new ZipFormatDescriptor().Shrink(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(zip), "an already-tight ZIP is byte-identical after shrink");
  }
}
