#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

/// <summary>
/// Btrfs lays an image out again by moving extents. This writer keeps logical
/// and physical addresses the same, so a move is the extent item's
/// <c>disk_bytenr</c> and the checksum over the leaf holding it — a tree block's
/// checksum covers itself and nothing else, so no chain of checks follows.
/// </summary>
/// <remarks>
/// What does reach further is the extent tree. Its items are keyed by the
/// address of what they describe, so an extent that moved leaves an item naming
/// an address nothing occupies — and the next allocation reads that as free and
/// writes over live data. Defragmenting looked perfect and the very next add
/// destroyed two files; that is what the second test here is for.
/// </remarks>
[TestFixture]
public class BtrfsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files, out string work) {
    work = Path.Combine(Path.GetTempPath(), "cwb_btrfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    var inputs = new List<ArchiveInputInfo>();
    for (var k = 0; k < 5; ++k) {
      var data = Payload(k, 9000 + k * 4000);
      var path = Path.Combine(work, $"F{k}.BIN");
      File.WriteAllBytes(path, data);
      inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
      files[$"F{k}.BIN"] = data;
    }

    var image = new MemoryStream();
    new BtrfsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
    return image;
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    using var reader = new BtrfsReader(image, leaveOpen: true);
    return reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => Path.GetFileName(e.Name), reader.Extract, StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheImagesSize(DefragMode mode) {
    using var image = Volume(out var files, out var work);
    try {
      var size = image.Length;
      image.Position = 0;
      new BtrfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
      Assert.That(image.Length, Is.EqualTo(size), "an image keeps its size");

      var read = ReadBack(image);
      foreach (var (name, data) in files) {
        Assert.That(read.Keys, Does.Contain(name), $"{name} must still be in the image");
        Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  public void Defragment_ThenAdd_LeavesEveryFileIntact() {
    using var image = Volume(out var files, out var work);
    try {
      image.Position = 0;
      new BtrfsFormatDescriptor().Defragment(image,
        new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

      // Adding allocates from the extent tree. If a moved extent's old home is
      // still listed there, this writes straight over live data.
      var added = Payload(9, 7000);
      var path = Path.Combine(work, "ADDED.BIN");
      File.WriteAllBytes(path, added);
      image.Position = 0;
      new BtrfsFormatDescriptor().Add(image, [new ArchiveInputInfo(path, "ADDED.BIN", false)]);

      var read = ReadBack(image);
      Assert.That(read.Keys, Does.Contain("ADDED.BIN"));
      Assert.That(read["ADDED.BIN"], Is.EqualTo(added));
      foreach (var (name, data) in files) {
        Assert.That(read.Keys, Does.Contain(name), $"{name} must have survived the add");
        Assert.That(read[name], Is.EqualTo(data), $"{name} must still read back byte for byte");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test]
  public void Defragment_KeepsFilesOutOfTheTrees() {
    using var image = Volume(out _, out var work);
    try {
      var descriptor = new BtrfsFormatDescriptor();
      image.Position = 0;
      var structure = descriptor.EnumerateExtents(image)
        .Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
      Assert.That(structure, Is.Not.Empty, "the image must reserve its own trees");

      image.Position = 0;
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

      image.Position = 0;
      var used = descriptor.EnumerateExtents(image)
        .Where(e => e.Kind == DefragBlockKind.Used).ToList();

      // A file packed towards the front must stop where the data chunk starts;
      // the trees live in front of it, and one written over reads as a block
      // whose address does not match itself.
      foreach (var file in used)
        foreach (var block in structure)
          Assert.That(file.Offset < block.Offset + block.Length && block.Offset < file.Offset + file.Length,
            Is.False, "a file and a tree block may not claim the same bytes");
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }
}
