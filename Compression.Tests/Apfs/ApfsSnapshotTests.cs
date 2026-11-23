using System.Security.Cryptography;
using System.Text;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// APFS snapshots on a volume macOS wrote with 512 of them (dissect.apfs's
/// <c>snapshot.bin</c>, downloaded at test time), compared with what apfs-fuse read when
/// mounting the live volume and individual snapshots (<c>-o snap=&lt;xid&gt;</c>):
/// <c>ReferenceVectors/dissect-snapshot.manifest</c>.
/// </summary>
[TestFixture]
public sealed class ApfsSnapshotTests {

  private static ApfsReader Open() => new(new MemoryStream(DissectApfsSamples.Snapshot(), writable: false));

  [Test, Category("ExternalInterop")]
  public void GivenAVolumeWithSnapshots_WhenRead_ThenTheLiveTreeAndEachSampledSnapshotMatchApfsFuse() {
    using var reader = Open();
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    var manifest = DissectApfsSamples.Manifest("dissect-snapshot.manifest").ToList();
    Assert.Multiple(() => {
      foreach (var (kind, value, path) in manifest) {
        switch (kind) {
          case 'n':
            Assert.That(reader.Snapshots, Has.Count.EqualTo(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
            break;
          case 's':
            Assert.That(reader.Snapshots.Select(s => s.Name), Does.Contain(path));
            break;
          case 'f':
            Assert.That(entries.TryGetValue(path, out var file), Is.True, $"{path} is listed");
            if (file is not null)
              Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(file))), Is.EqualTo(value), path);
            break;
          case 'l':
            Assert.That(entries.TryGetValue(path, out var link) ? link.LinkTarget : null, Is.EqualTo(value), path);
            break;
        }
      }
    });
  }

  [Test, Category("ExternalInterop")]
  public void GivenAVolumeWithSnapshots_WhenEverySnapshotIsRead_ThenItsFileHoldsItsOwnName() {
    // dissect.apfs wrote "Snapshot i\n" into `file` before taking snapshot i.
    using var reader = Open();
    var files = reader.Entries.Where(e => e.Name.EndsWith("/file", StringComparison.Ordinal))
      .ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(reader.Snapshots, Has.Count.EqualTo(512));
    Assert.Multiple(() => {
      foreach (var (name, _) in reader.Snapshots) {
        var path = $"{ApfsReader.SnapshotFolder}/{name}/file";
        Assert.That(files.TryGetValue(path, out var file), Is.True, path);
        if (file is not null)
          Assert.That(Encoding.ASCII.GetString(reader.Extract(file)), Is.EqualTo(name + "\n"), path);
      }
    });
  }

  [Test, Category("ExternalInterop")]
  public void GivenAVolumeWithSnapshots_WhenListed_ThenSnapshotsAreOrderedByTransaction() {
    using var reader = Open();
    Assert.That(reader.Snapshots.Select(s => s.Xid), Is.Ordered.Ascending);
  }
}
