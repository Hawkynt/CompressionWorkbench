#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the TAR <see cref="IWipeEmpty"/> and <see cref="IArchiveShrinkable"/>
/// operations: trailing junk past the end-of-archive marker is wiped/dropped
/// while every entry still extracts byte-identically.
/// </summary>
[TestFixture]
public class TarWipeShrinkOperationTests {

  private static byte[] BuildTar(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    var w = new TarWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files)
      w.AddEntry(new TarEntry { Name = n, Size = d.Length }, d);
    w.Finish();
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(Stream s) {
    s.Position = 0;
    var r = new TarReader(s);
    var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    while (r.GetNextEntry() is { } e) {
      if (e.IsDirectory) { r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var data = new byte[e.Size];
      es.ReadExactly(data);
      map[e.Name] = data;
    }
    return map;
  }

  [Test]
  public void TarDescriptorImplementsWipeAndShrink() {
    var d = new TarFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IWipeEmpty>());
    Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
  }

  [Test]
  public void Wipe_ZerosTrailingJunk_AndPreservesEntries() {
    var payload = "tape archive payload"u8.ToArray();
    var tar = BuildTar(("a.txt", payload));

    using var ms = new MemoryStream();
    ms.Write(tar);
    var marker = new byte[1024];
    Array.Fill(marker, (byte)0x55);
    ms.Write(marker);
    ms.SetLength(ms.Length);

    var wiped = new TarFormatDescriptor().WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(marker.Length));

    Assert.That(ReadAll(ms)["a.txt"], Is.EqualTo(payload));

    ms.Position = tar.Length;
    var probe = new byte[marker.Length];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero);
  }

  [Test]
  public void Shrink_DropsTrailingJunk_AndPreservesEntries() {
    var payload = new byte[12000];
    Array.Fill(payload, (byte)0xAB);
    var tar = BuildTar(("big.bin", payload));   // ~13 KiB, padded to a 10 KiB record boundary

    using var input = new MemoryStream();
    input.Write(tar);
    // Append junk well past the next blocking-factor boundary so the trim is
    // unambiguous (more than one 10 KiB record of trailing garbage).
    var junk = new byte[30000];
    Array.Fill(junk, (byte)0xCC);
    input.Write(junk);
    input.Position = 0;

    using var output = new MemoryStream();
    new TarFormatDescriptor().Shrink(input, output);

    Assert.That(output.Length, Is.LessThan(input.Length), "trailing junk must be dropped");
    Assert.That(output.Length, Is.GreaterThanOrEqualTo(tar.Length), "valid blocking padding is preserved");
    Assert.That(ReadAll(output)["big.bin"], Is.EqualTo(payload));
  }

  [Test]
  public void Shrink_IsIdempotent_OnTightArchive() {
    var tar = BuildTar(("x", "data"u8.ToArray()));
    using var input = new MemoryStream(tar);
    using var output = new MemoryStream();
    new TarFormatDescriptor().Shrink(input, output);
    // A well-formed TAR is already blocking-factor aligned and terminated, so
    // shrink returns it byte-identical.
    Assert.That(output.ToArray(), Is.EqualTo(tar));
  }
}
