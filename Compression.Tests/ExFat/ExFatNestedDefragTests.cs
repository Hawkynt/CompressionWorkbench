#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

/// <summary>
/// Defragmenting must repoint files inside folders, not only in the root. The mover
/// used to look for a moved file's entry in the root directory only and return
/// silently when it was not there, leaving the entry pointing at clusters that had
/// just been handed to something else. It also refused every file whose packed runs
/// were not consecutive, and the descriptor then rebuilt the volume without its
/// label, serial, times and attributes.
/// </summary>
[TestFixture]
public class ExFatNestedDefragTests {

  private static byte[] Data(int length, int seed) {
    var d = new byte[length];
    for (var i = 0; i < d.Length; i++) d[i] = (byte)(i * 13 + seed * 31 + (i >> 9));
    return d;
  }

  private static (MemoryStream Image, Dictionary<string, byte[]> Expected) FragmentedVolumeWithFolders() {
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var w = new ExFatWriter();
    void Add(string name, byte[] data) { w.AddFile(name, data); expected[name] = data; }
    Add("pad1.bin", Data(40000, 1));
    Add("docs/inner.bin", Data(70000, 2));
    Add("pad2.bin", Data(40000, 3));
    Add("docs/deep/nested.bin", Data(90000, 4));
    Add("tail.bin", Data(30000, 5));
    var image = new MemoryStream();
    image.Write(w.Build(8));

    // Punch holes in front of the nested files, then add a file that fills them
    // in several pieces.
    var d = new ExFatFormatDescriptor();
    d.Remove(image, ["pad1.bin", "pad2.bin"]);
    expected.Remove("pad1.bin");
    expected.Remove("pad2.bin");
    var filler = Data(120000, 6);
    d.Add(image, [ArchiveInputInfo.InMemory("filler.bin", filler)]);
    expected["filler.bin"] = filler;
    return (image, expected);
  }

  private static Dictionary<string, byte[]> ReadAll(Stream image) {
    image.Position = 0;
    var r = new ExFatReader(image);
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e),
      StringComparer.OrdinalIgnoreCase);
  }

  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void GivenFragmentedFilesAndFolders_WhenDefragmented_ThenEveryFileReadsBackAndTheSizeStays(DefragMode mode) {
    var (image, expected) = FragmentedVolumeWithFolders();
    var size = image.Length;

    new ExFatFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    Assert.That(image.Length, Is.EqualTo(size));
    var after = ReadAll(image);
    Assert.That(after.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, data) in expected)
      Assert.That(after[name], Is.EqualTo(data), $"{mode}: '{name}' changed");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenFragmentedFilesAndFolders_WhenDefragmented_ThenFsckExfatAccepts() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("fsck.exfat"))
      Assert.Ignore("fsck.exfat not available.");
    var (image, _) = FragmentedVolumeWithFolders();
    new ExFatFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    var path = Path.Combine(Path.GetTempPath(), "cwb_exfat_defrag_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    try {
      File.WriteAllBytes(path, image.ToArray());
      var r = FsInteropToolbox.RunWsl($"fsck.exfat -n {FsInteropToolbox.WinToWsl(path)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    } finally {
      File.Delete(path);
    }
  }
}
