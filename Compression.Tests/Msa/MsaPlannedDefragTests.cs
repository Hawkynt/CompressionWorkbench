#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Msa;

namespace Compression.Tests.Msa;

/// <summary>
/// An MSA is a wrapper, and what is inside knows how to lay itself out.
/// </summary>
/// <remarks>
/// The old path read every file out through the FAT reader and wrote a fresh
/// volume from them. That lost the subdirectories, the volume label and the
/// boot sector as it stood — and it refused outright on the GEMDOS volumes this
/// descriptor itself writes, so defragmenting one of our own images threw
/// rather than doing anything. The inner volume is handed the request now, and
/// it moves its clusters.
/// </remarks>
[TestFixture]
public class MsaPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> kept) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_msa_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    var all = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 3000 + k * 900);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        all[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      var descriptor = new MsaFormatDescriptor();
      descriptor.Create(image, inputs, new FormatCreateOptions());

      // Punch holes so there is something to close up.
      image.Position = 0;
      descriptor.Remove(image, ["F1.BIN", "F3.BIN"]);
      all.Remove("F1.BIN");
      all.Remove("F3.BIN");

      kept = all;
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayload(DefragMode mode) {
    using var image = Volume(out var kept);

    image.Position = 0;
    new MsaFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    image.Position = 0;
    var reader = new MsaReader(image);
    Assert.That(reader.Entries, Is.Not.Empty, "the wrapper must still decode");

    var work = Path.Combine(Path.GetTempPath(), "cwb_msa_out_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      image.Position = 0;
      new MsaFormatDescriptor().Extract(image, work, null, null);
      foreach (var (name, data) in kept) {
        var found = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(found, Is.Not.Null, $"{name} must still be on the disc");
        Assert.That(File.ReadAllBytes(found!), Is.EqualTo(data), $"{name} must read back byte for byte");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test]
  public void Defragment_NoLongerRefusesTheVolumesThisDescriptorWrites() {
    using var image = Volume(out _);
    image.Position = 0;

    // The wrapper's own Create emits a GEMDOS volume; the old path only spoke
    // to a FAT reader and threw on exactly the images it had just written.
    Assert.DoesNotThrow(() => new MsaFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd }));
  }
}
