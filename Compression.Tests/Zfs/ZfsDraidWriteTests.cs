using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// dRAID pools written by <see cref="ZfsFormatDescriptor.CreatePool"/> (version 5000; draid1 with
/// two data columns on five devices, draid2 with three on six), then edited: the reader reads
/// them; OpenZFS checks them — <c>zdb -bcc</c>, <c>zdb -d</c>, the kernel importing and scrubbing
/// them whole and with a device missing, serving exactly the files.
/// </summary>
[TestFixture]
public sealed class ZfsDraidWriteTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static byte[] Text(string word, int lines) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"{word} {i} {i * 7919 % 10007}\n")));

  private static byte[] Random(int length, int seed) {
    var bytes = new byte[length];
    new System.Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static readonly (string Path, byte[] Data)[] Files = [
    ("text.txt", Text("draid", 20000)), ("big.bin", Random(2_500_000, 3)), ("tiny.txt", Encoding.ASCII.GetBytes("t")),
    ("dir/a.bin", Random(5000, 4)), ("dir/b.bin", Random(131072, 5)), ("dir/c.bin", Random(131073, 6)), ("empty.txt", []),
  ];

  private static List<MemoryStream> Written(string layout, int devices) {
    var streams = Enumerable.Range(0, devices).Select(_ => new MemoryStream()).ToList();
    Descriptor.CreatePool(streams.Cast<Stream>().ToList(), Files.Select(f => ArchiveInputInfo.InMemory(f.Path, f.Data)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["Layout"] = layout, ["Version"] = "5000", ["PoolName"] = "cwbdraidw" } });
    return streams;
  }

  private static Dictionary<string, string> Expected(IEnumerable<(string Path, byte[] Data)> files) =>
    files.ToDictionary(f => f.Path, f => ZfsOracle.Sha(f.Data), StringComparer.Ordinal);

  private static Dictionary<string, string> Read(IEnumerable<byte[]> images) {
    using var reader = new ZfsReader(images.Select(i => (Stream)new MemoryStream(i, writable: false)).ToList());
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(reader.Topology.Single(), Does.StartWith("draid"));
    return reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null)
      .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
  }

  public static IEnumerable<TestCaseData> Layouts() {
    yield return new TestCaseData("draid1:2d", 5);
    yield return new TestCaseData("draid2:3d", 6);
  }

  [TestCaseSource(nameof(Layouts))]
  public void Given_ADraidPool_When_Written_Then_ItReadsBackWholeAndWithADeviceMissing(string layout, int devices) {
    var images = Written(layout, devices).Select(s => s.ToArray()).ToList();
    Assert.That(Read(images), Is.EquivalentTo(Expected(Files)));
    for (var gone = 0; gone < devices; ++gone)
      Assert.That(Read(images.Where((_, i) => i != gone)), Is.EquivalentTo(Expected(Files)), $"without device {gone}");
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool"), NonParallelizable]
  public void Given_AWrittenAndEditedDraidPool_When_OpenZfsChecksIt_Then_ItIsClean(string layout, int devices) {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var streams = Written(layout, devices);
    var written = streams.Select(s => s.ToArray()).ToList();
    Check(written, Expected(Files), degraded: true);

    var added = new (string Path, byte[] Data)[] { ("edit/more.bin", Random(700_000, 9)), ("edit/note.txt", Text("note", 30)) };
    Descriptor.EditPool(streams.Cast<Stream>().ToList(), added.Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList(), ["big.bin", "dir/a.bin"]);
    var expected = Expected(Files.Where(f => f.Path is not ("big.bin" or "dir/a.bin")).Concat(added));
    var edited = streams.Select(s => s.ToArray()).ToList();
    Assert.That(Read(edited), Is.EquivalentTo(expected));
    Check(edited, expected, degraded: false);
  }

  private static void Check(List<byte[]> images, Dictionary<string, string> expected, bool degraded) {
    var (dir, wsl) = ZfsOracle.Stage(images.Select((d, i) => ($"d{i}.img", d)).ToList());
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbdraidw");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbdraidw");
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbdraidw"), Is.EquivalentTo(expected));
      if (!degraded) return;
      File.Delete(Path.Combine(dir, "d1.img"));
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbdraidw"), Is.EquivalentTo(expected), "with device 1 missing");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
