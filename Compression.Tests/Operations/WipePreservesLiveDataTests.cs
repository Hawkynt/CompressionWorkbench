using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Wiping unused space must never touch a file that is still there.
/// </summary>
/// <remarks>
/// <para>The verb zeroes everything the block map does not claim. That is only
/// safe while the map claims everything the volume holds, and on a small volume
/// it did not: Btrfs mapped eleven readable files as metadata and no file data
/// at all, and the wipe destroyed all eleven. NTFS mapped ten of fourteen, and
/// the wipe zeroed two of the four it had missed — each came back at its right
/// length over zeroed clusters, which is the shape of the fault that hides
/// longest.</para>
///
/// <para>Both now check the map against the volume's own list before wiping and
/// decline when a file large enough to own extents is not in it. A volume that
/// lists no files still wipes: scrubbing what a deletion left behind is the
/// point of the verb, and a blind map has nothing there to lose.</para>
///
/// <para>The sizes matter. Every fixture elsewhere is a few kilobytes with a
/// handful of files, where the maps are complete and none of this appears.</para>
/// </remarks>
[TestFixture]
public class WipePreservesLiveDataTests {

  private static byte[] Payload(int length, int seed) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed * 7 + (i >> 11));
    return data;
  }

  [Test, Category("Regression")]
  [TestCase("Btrfs", 50 * 1024)]
  [TestCase("Btrfs", 8 * 1024 * 1024)]
  [TestCase("Ntfs", 50 * 1024)]
  [TestCase("Ntfs", 8 * 1024 * 1024)]
  public void WipingUnusedSpaceKeepsEveryFile(string formatId, int totalBytes) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)!;

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();

    void Add(List<ArchiveInputInfo> into, string name, int length, int seed) {
      var data = Payload(length, seed);
      expected[name] = data;
      into.Add(ArchiveInputInfo.InMemory(name, data));
    }

    // One file taking half the volume, a spread of middling ones, and a few far
    // smaller than an allocation unit — the mix the maps were incomplete for.
    Add(inputs, "BIG00001.BIN", totalBytes / 2, 1);
    var perFile = (totalBytes - totalBytes / 2) / 10;
    for (var i = 0; i < 10; ++i) {
      var length = Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) length = 17 + i;
      Add(inputs, $"F{i:D4}.BIN", length, i + 2);
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    // Churn it. A freshly created volume is the case the maps handle.
    var doomed = expected.Keys.Where(k => k.StartsWith('F')).Where((_, n) => n % 3 == 1).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    var added = new List<ArchiveInputInfo>();
    var addSize = totalBytes > 1024 * 1024 ? 40 * 1024 : 3 * 1024;
    for (var i = 0; i < 6; ++i) Add(added, $"ADD{i:D2}.BIN", addSize + i * 97, 900 + i);
    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, added);

    image.Position = 0;
    ((IWipeEmpty)ops).WipeUnusedSpace(image);

    var outDir = Path.Combine(Path.GetTempPath(), "wipekeep_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ((IArchiveFormatOperations)ops).Extract(image, outDir, null, null);

      // Match on content: what matters is that the bytes survived, whatever the
      // format chose to call the file.
      var present = new HashSet<string>(StringComparer.Ordinal);
      foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        present.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

      foreach (var (name, want) in expected) {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(want));
        Assert.That(present, Does.Contain(hash),
          $"{formatId} at {totalBytes:N0} bytes: {name} ({want.Length:N0} bytes) did not survive the wipe");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }
}
