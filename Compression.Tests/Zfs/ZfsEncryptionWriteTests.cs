using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Editing natively encrypted datasets with their keys: files added to and removed from every
/// dataset of cwbcrypt (AES-256-GCM passphrase root with a snapshot and a child, AES-128-CCM raw,
/// AES-192-GCM hex). The reader reads the result back with the keys; OpenZFS checks it —
/// <c>zdb -bcc</c> (no key), the kernel loading our keys, scrubbing, serving every file and the
/// snapshot byte for byte, writing on, and <c>zdb -bcc</c> again.
/// </summary>
[TestFixture]
public sealed class ZfsEncryptionWriteTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static readonly Dictionary<string, ZfsKeyMaterial> Keys = new(StringComparer.Ordinal) {
    ["cwbcrypt/gcm256"] = ZfsKeyMaterial.FromText("gcm-256-passphrase"),
    ["cwbcrypt/ccm128"] = ZfsKeyMaterial.FromBytes(Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef")),
    ["cwbcrypt/gcm192"] = ZfsKeyMaterial.FromText("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"),
  };

  private static byte[] Text(string word, int lines) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"{word} {i} {i * 7919 % 10007}\n")));

  private static byte[] Random(int length, int seed) {
    var bytes = new byte[length];
    new System.Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static readonly (string Path, byte[] Data)[] Adds = [
    ("gcm256/new.txt", Text("gcm", 3000)),
    ("gcm256/child/new.bin", Random(90000, 1)),
    ("ccm128/dir/new.txt", Text("ccm", 200)),
    ("ccm128/big.bin", Random(400000, 2)),
    ("gcm192/new.txt", Text("hex", 5000)),
    ("added-plain.txt", Text("plain", 50)),
  ];
  private static readonly string[] Removes = ["gcm256/text.txt", "gcm256/many", "ccm128/large.bin", "gcm192/hardlink.txt", "gcm256/child/tiny.txt"];

  private static (byte[] Image, Dictionary<string, string> Head, Dictionary<string, string> Snapshot) Edited() {
    var stream = ZfsReferencePools.Copy("cwbcrypt");
    Descriptor.EditPool([stream], Adds.Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList(), Removes, null, Keys);
    var manifest = ZfsReferencePools.Manifest("cwbcrypt").Where(m => m.Kind == 'F').ToList();
    var head = manifest.Where(m => !m.Path.Contains("/.zfs/", StringComparison.Ordinal)
      && !Removes.Any(r => m.Path == r || m.Path.StartsWith(r + "/", StringComparison.Ordinal)))
      .ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    foreach (var (path, data) in Adds) head[path] = ZfsOracle.Sha(data);
    var snapshot = manifest.Where(m => m.Path.Contains("/.zfs/", StringComparison.Ordinal)).ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    return (stream.ToArray(), head, snapshot);
  }

  private static Dictionary<string, string> Read(byte[] image, Func<string, bool> which) {
    using var reader = new ZfsReader(new MemoryStream(image, writable: false), null, Keys);
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(reader.Datasets.Where(d => d.Refusal != null), Is.Empty);
    return reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null && which(e.Name))
      .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
  }

  [Test]
  public void Given_EncryptedDatasets_When_EditedWithTheirKeys_Then_TheReaderSeesEveryEdit() {
    var (image, head, snapshot) = Edited();
    Assert.That(Read(image, n => !n.Contains("/.zfs/", StringComparison.Ordinal)), Is.EquivalentTo(head));
    Assert.That(Read(image, n => n.Contains("/.zfs/", StringComparison.Ordinal)), Is.EquivalentTo(snapshot));
  }

  [Test]
  public void Given_AnEncryptedDataset_When_EditedWithoutItsKey_Then_TheEditIsRefusedUntouched() {
    var stream = ZfsReferencePools.Copy("cwbcrypt");
    var original = stream.ToArray();
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(stream, [ArchiveInputInfo.InMemory("gcm256/x.txt", [1, 2, 3])]));
    var wrong = new Dictionary<string, ZfsKeyMaterial> { ["cwbcrypt/gcm256"] = ZfsKeyMaterial.FromText("wrong") };
    Assert.Throws<NotSupportedException>(() => Descriptor.EditPool([stream], [ArchiveInputInfo.InMemory("gcm256/x.txt", [1, 2, 3])], [], null, wrong));
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Given_OnlyThePlainDataset_When_Edited_Then_NoKeyIsNeeded() {
    var stream = ZfsReferencePools.Copy("cwbcrypt");
    Descriptor.Add(stream, [ArchiveInputInfo.InMemory("other.txt", Text("x", 10))]);
    using var reader = new ZfsReader(new MemoryStream(stream.ToArray(), writable: false), null, Keys);
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(ZfsOracle.Sha(reader.Extract(reader.Entries.Single(e => e.Name == "other.txt"))), Is.EqualTo(ZfsOracle.Sha(Text("x", 10))));
  }

  private const string LoadKeys =
    "printf 0123456789abcdef0123456789abcdef > /tmp/raw.key; " +
    "printf 00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff > /tmp/hex.key; " +
    "echo gcm-256-passphrase | zfs load-key cwbcrypt/gcm256 && zfs load-key -L file:///tmp/raw.key cwbcrypt/ccm128 && " +
    "zfs load-key -L file:///tmp/hex.key cwbcrypt/gcm192 && echo KEYS-LOADED";

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_EditedEncryptedDatasets_When_TheKernelLoadsTheKeys_Then_ItServesEveryFileAndWritesOn() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (image, head, snapshot) = Edited();
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbcrypt");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbcrypt");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbcrypt");
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbcrypt", out var log,
        before: LoadKeys,
        after: "( cd $M/gcm256/.zfs/snapshot/s1 && find . -type f | sort | while read -r p; do echo \"SNAP $(sha256sum < \"$p\" | cut -d' ' -f1) ${p#./}\"; done ); " +
          "for d in gcm256 gcm256/child ccm128 gcm192; do seq 1 3000 > $M/$d/kernel.txt; rm -f $M/$d/tiny.txt; done; zpool sync cwbcrypt && echo WROTE",
        download: true);
      Assert.That(log, Does.Contain("KEYS-LOADED").And.Contain("WROTE"), log);
      Assert.That(served, Is.EquivalentTo(head));
      var kernelSnapshot = log.Split('\n').Where(l => l.StartsWith("SNAP ", StringComparison.Ordinal))
        .ToDictionary(l => "gcm256/.zfs/snapshot/s1/" + l[70..].TrimEnd('\r'), l => l[5..69], StringComparer.Ordinal);
      Assert.That(kernelSnapshot, Is.EquivalentTo(snapshot));
      ZfsOracle.AssertNoLeaks(wsl, "cwbcrypt");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbcrypt");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
