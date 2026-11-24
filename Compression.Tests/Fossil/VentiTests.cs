using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Fossil;

namespace Compression.Tests.Fossil;

/// <summary>
/// Venti: an arena partition plan9port's <c>venti</c> server filled from <c>vac</c> and from a
/// Fossil server archiving a snapshot, and a Fossil volume <c>flfmt -v</c> made from that archive
/// (every block on Venti). Compared with what <c>unvac</c> and the Fossil server read back
/// (<c>Fossil/ReferenceVectors/README.md</c>, <c>venti-gen.sh</c>).
/// </summary>
[TestFixture]
public class VentiTests {

  private static byte[] Resource(string name) {
    using var res = typeof(VentiTests).Assembly.GetManifestResourceStream("FossilVectors." + name)
      ?? throw new InvalidOperationException(name + " not embedded");
    using var ms = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gz = new GZipStream(res, CompressionMode.Decompress);
      gz.CopyTo(ms);
    } else res.CopyTo(ms);
    return ms.ToArray();
  }

  private static VentiArenaStore Store() => new(new MemoryStream(Resource("venti.arenas.gz"), writable: false));

  private static string[] Lines(string name) => System.Text.Encoding.UTF8.GetString(Resource(name))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.TrimEnd('\r')).ToArray();

  private static string Sha(byte[] d) => Convert.ToHexStringLower(SHA256.HashData(d));

  /// <summary>"sha256  payload/path" lines as unvac extracted the vac archive.</summary>
  private static Dictionary<string, string> VacManifest()
    => Lines("vac.manifest").Select(static l => l.Split("  ", 2)).ToDictionary(static p => p[1], static p => p[0]);

  [Test, Category("HappyPath")]
  public void GivenTheArenaPartition_WhenEveryClumpIsRead_ThenEachHashesToItsScore() {
    var store = Store();
    Assert.Multiple(() => {
      Assert.That(store.Arenas, Has.Count.GreaterThan(1));
      Assert.That(store.Clumps.Count(static c => c.Compressed), Is.GreaterThan(10), "whack-compressed clumps are exercised");
      Assert.That(store.Clumps.Count(static c => !c.Compressed), Is.GreaterThan(5), "plain clumps are exercised");
      foreach (var clump in store.Clumps)
        Assert.That(SHA1.HashData(store.Read(clump.Score)), Is.EqualTo(clump.Score));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheVacArchive_WhenListedThroughTheVentiDescriptor_ThenEveryFileIsWhatUnvacExtracted() {
    var manifest = VacManifest();
    var store = Store();
    var vacRoot = Lines("vac.score")[0].Trim();
    var tree = FossilReader.OpenVac(store, Convert.FromHexString(vacRoot));
    Assert.That(VentiFormatDescriptor.Trees(store).Select(static t => t.Folder), Does.Contain(vacRoot));
    var files = tree.Entries.Where(static e => !e.IsDirectory).ToDictionary(static e => e.Path);
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    Assert.Multiple(() => {
      foreach (var (path, sha) in manifest) Assert.That(Sha(tree.Extract(files[path])), Is.EqualTo(sha), path);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFossilVolumeWhoseTreeIsOnVenti_WhenReadWithTheArenas_ThenItMatchesWhatTheFossilServerReadBack() {
    // "F path size sha256 mode" and "D path mode" lines, as the server listed /active over 9P
    var expected = Lines("restored.manifest").Where(static l => l.StartsWith("F ", StringComparison.Ordinal))
      .Select(static l => l.Split(' ')).ToDictionary(static f => f[1], static f => (Size: long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), Sha: f[3]));
    Assume.That(expected, Is.Not.Empty);
    var reader = new FossilReader(new MemoryStream(Resource("restored.img.gz"), writable: false), Store());
    var files = reader.Entries.Where(static e => !e.IsDirectory && e.Path.StartsWith("active/", StringComparison.Ordinal))
      .ToDictionary(static e => e.Path["active/".Length..]);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var (path, (size, sha)) in expected) {
        Assert.That(files[path].Size, Is.EqualTo(size), path);
        Assert.That(Sha(reader.Extract(files[path])), Is.EqualTo(sha), path);
      }
    });
  }

  [Test, Category("Exception")]
  public void GivenAFossilVolumeWhoseTreeIsOnVenti_WhenReadWithoutTheArenas_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => _ = new FossilReader(new MemoryStream(Resource("restored.img.gz"), writable: false)));

  [Test, Category("Exception")]
  public void GivenADamagedClump_WhenItIsRead_ThenItIsRefusedNotReturned() {
    var bytes = Resource("venti.arenas.gz");
    var store = new VentiArenaStore(new MemoryStream(bytes, writable: false));
    var plain = store.Clumps.First(static c => !c.Compressed && c.StoredSize > 100);
    var packed = store.Clumps.First(static c => c.Compressed && c.StoredSize > 20);
    bytes[plain.Offset + 50] ^= 0x40;
    bytes[packed.Offset + 10] ^= 0x40;
    var damaged = new VentiArenaStore(new MemoryStream(bytes, writable: false));
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() => damaged.Read(plain.Score));
      Assert.Throws<InvalidDataException>(() => damaged.Read(packed.Score));
    });
  }

  [Test, Category("Boundary")]
  public void GivenAScoreNoArenaHolds_WhenRead_ThenKeyNotFoundAndTheEmptyBlockStillReads() {
    var store = Store();
    Assert.Multiple(() => {
      Assert.Throws<KeyNotFoundException>(() => store.Read(new byte[20]));
      Assert.That(store.Read(VentiArenaStore.ZeroScore), Is.Empty);
      Assert.That(store.Contains(VentiArenaStore.ZeroScore), Is.True);
    });
  }

  [Test, Category("Exception")]
  public void GivenSomethingThatIsNotAnArenaPartition_WhenOpened_ThenInvalidData() {
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() => _ = new VentiArenaStore(new MemoryStream(new byte[300 * 1024])));
      Assert.Throws<InvalidDataException>(() => _ = new VentiArenaStore(new MemoryStream(new byte[1000])));
    });
  }

  [Test, Category("Boundary")]
  public void GivenWhackDataCutShort_WhenDecoded_ThenItIsRefused() {
    var store = Store();
    var packed = store.Clumps.First(static c => c.Compressed && c.Size > 1000);
    var bytes = Resource("venti.arenas.gz");
    var stored = bytes.AsSpan((int)packed.Offset, packed.StoredSize / 2).ToArray();
    var target = new byte[packed.Size];
    Assert.That(() => {
      var n = VentiArenaStore.Unwhack(target, stored);
      if (n != packed.Size) throw new InvalidDataException("short");
    }, Throws.InstanceOf<InvalidDataException>());
  }
}
