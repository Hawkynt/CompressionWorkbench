using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Editing dRAID pools the OpenZFS kernel wrote (single and double parity, one with a distributed
/// spare): files removed and added, one large enough to need many stripes. The reader reads the
/// result back; OpenZFS checks it — <c>zdb -bcc</c>, <c>zdb -d</c>, the kernel importing,
/// scrubbing (every stripe's parity and zero padding verified) and serving exactly the edited
/// files, writing on, and <c>zdb -bcc</c> again.
/// </summary>
[TestFixture, Category("Slow"), Category("ExternalTool"), NonParallelizable]
public sealed class ZfsDraidEditTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  /// <summary>A dRAID pool the kernel creates on 96 MiB children, with the files it served.</summary>
  private static (List<byte[]> Images, Dictionary<string, string> Files) KernelPool(string layout, int children) {
    var (dir, wsl) = ZfsOracle.Stage([]);
    try {
      var devices = string.Join(' ', Enumerable.Range(0, children).Select(i => $"$O/d{i}.img"));
      var log = ZfsOracle.Appliance(dir, wsl,
        $"for i in $(seq 0 {children - 1}); do truncate -s 96M $O/d$i.img; done\n" +
        $"zpool create -f -o ashift=9 -O compression=off -O mountpoint=$M/cwbdraid cwbdraid {layout} {devices} || exit 1\n" +
        "D=$M/cwbdraid; head -c 34000000 /dev/urandom > $D/big.bin; seq 1 20000 > $D/text.txt; printf tiny > $D/tiny.txt\n" +
        "mkdir $D/dir; for i in $(seq 1 30); do head -c $((i * 2000)) /dev/urandom > $D/dir/f$i; done\n" +
        "( cd $D && find . -type f | sort | while read -r p; do echo \"FILE $(sha256sum < \"$p\" | cut -d' ' -f1) ${p#./}\"; done )\n" +
        "zpool export cwbdraid && echo EXPORTED\n" +
        $"for i in $(seq 0 {children - 1}); do gzip -1 -c $O/d$i.img > $O/d$i.img.gz; done",
        [.. Enumerable.Range(0, children).Select(i => $"d{i}.img.gz")]);
      Assert.That(log, Does.Contain("EXPORTED"), log);
      var files = log.Split('\n').Where(l => l.StartsWith("FILE ", StringComparison.Ordinal))
        .ToDictionary(l => l[70..].TrimEnd('\r'), l => l[5..69], StringComparer.Ordinal);
      var images = Enumerable.Range(0, children).Select(i => {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(dir, $"d{i}.img.gz")), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
      }).ToList();
      return (images, files);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  private static byte[] Random(int length, int seed) {
    var bytes = new byte[length];
    new System.Random(seed).NextBytes(bytes);
    return bytes;
  }

  public static IEnumerable<TestCaseData> Layouts() {
    yield return new TestCaseData("draid1:2d:1s:5c", 5).SetName("Given_ADraid1PoolTheKernelWrote_When_Edited_Then_OpenZfsAcceptsIt");
    yield return new TestCaseData("draid2:3d:0s:6c", 6).SetName("Given_ADraid2PoolTheKernelWrote_When_Edited_Then_OpenZfsAcceptsIt");
  }

  [TestCaseSource(nameof(Layouts))]
  public void Edit(string layout, int children) {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (images, files) = KernelPool(layout, children);
    var streams = images.Select(i => { var ms = new MemoryStream(); ms.Write(i); return ms; }).ToList();
    var added = new (string Path, byte[] Data)[] {
      ("new/large.bin", Random(3_000_000, 1)), ("new/small.txt", Encoding.ASCII.GetBytes("small\n")),
      ("new/odd.bin", Random(12345, 2)),
    };
    string[] removed = ["text.txt", "dir/f7", "dir/f19"];
    Descriptor.EditPool(streams.Cast<Stream>().ToList(), added.Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList(), removed);
    var expected = files.Where(f => !removed.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
    foreach (var (path, data) in added) expected[path] = ZfsOracle.Sha(data);

    using (var reader = new ZfsReader(streams.Select(s => (Stream)new MemoryStream(s.ToArray(), writable: false)).ToList())) {
      Assert.That(reader.Warnings, Is.Empty);
      var read = reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null)
        .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
      Assert.That(read, Is.EquivalentTo(expected));
    }

    var (dir, wsl) = ZfsOracle.Stage(streams.Select((s, i) => ($"d{i}.img", s.ToArray())).ToList());
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbdraid");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbdraid");
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbdraid", out var log,
        after: "seq 1 50000 > $M/kernel.txt && rm $M/new/small.txt && zpool sync cwbdraid && echo WROTE", download: true);
      Assert.That(served, Is.EquivalentTo(expected));
      Assert.That(log, Does.Contain("WROTE"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbdraid");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbdraid");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
