using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Volumes (zvols): the pool <c>cwbzvol</c> that OpenZFS 2.2.2 wrote holds three volumes with
/// different block sizes and compressions, one below a file system, and a snapshot of one taken
/// before it was overwritten. Each is listed as one file holding exactly the bytes the kernel's
/// <c>/dev/zvol</c> device read when the pool was made (the manifest's SHA-256 and length).
/// </summary>
[TestFixture]
public sealed class ZfsVolumeTests {

  private const string Pool = "cwbzvol";

  [Test, Category("HappyPath")]
  public void GivenAPoolOfVolumes_WhenRead_ThenEveryVolumeAndSnapshotIsTheDevicesBytes() {
    using var reader = new ZfsReader(ZfsReferencePools.Open(Pool));
    ZfsReferencePools.AssertMatchesManifest(reader, Pool);
  }

  [Test, Category("HappyPath")]
  public void GivenAPoolOfVolumes_WhenListingDatasets_ThenVolumesAreListedNotRefused() {
    using var reader = new ZfsReader(ZfsReferencePools.Open(Pool));
    var byName = reader.Datasets.ToDictionary(static d => d.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(byName["cwbzvol/vol"].Kind, Is.EqualTo("volume"));
      Assert.That(byName["cwbzvol/vol"].Refusal, Is.Null);
      Assert.That(byName["cwbzvol/vol@s1"].Kind, Is.EqualTo("snapshot"));
      Assert.That(byName["cwbzvol/sparse"].Refusal, Is.Null);
      Assert.That(byName["cwbzvol/fs/vol2"].Path, Is.EqualTo("fs/vol2"));
      Assert.That(reader.Warnings, Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeAndItsSnapshot_WhenRead_ThenTheyDifferOnlyWhereTheVolumeWasOverwritten() {
    using var reader = new ZfsReader(ZfsReferencePools.Open(Pool));
    var now = reader.Extract(reader.Entries.Single(static e => e.Name == "vol"));
    var then = reader.Extract(reader.Entries.Single(static e => e.Name == "vol@s1"));
    Assert.That(now.Length, Is.EqualTo(8 * 1024 * 1024));
    var differing = Enumerable.Range(0, now.Length).Where(i => now[i] != then[i]).ToList();
    Assert.That(differing, Is.Not.Empty);
    // The overwrite started at 4 KiB block 1600 and wrote less than 40 KiB.
    Assert.That(differing.Min(), Is.GreaterThanOrEqualTo(1600 * 4096));
    Assert.That(differing.Max(), Is.LessThan(1600 * 4096 + 40 * 1024));
  }

  [Test, Category("EdgeCase")]
  public void GivenASparseVolume_WhenRead_ThenItsHolesAreZerosAndItsIslandsAreWhereTheyWereWritten() {
    using var reader = new ZfsReader(ZfsReferencePools.Open(Pool));
    var sparse = reader.Extract(reader.Entries.Single(static e => e.Name == "sparse"));
    Assert.That(sparse.Length, Is.EqualTo(32 * 1024 * 1024));
    Assert.That(System.Text.Encoding.ASCII.GetString(sparse, 1048576, 11), Is.EqualTo("island one\n"));
    Assert.That(sparse.AsSpan(0, 1048576).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
    Assert.That(sparse.AsSpan(1048576 + 11, 25000000 - 1048576 - 11).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
  }

  [Test, Category("Exception")]
  public void GivenAVolume_WhenAskedToRemoveIt_ThenTheEditIsRefusedAndThePoolUntouched() {
    var ops = (IArchiveModifiable)FormatRegistry.GetArchiveOps("Zfs")!;
    using var image = ZfsReferencePools.Copy(Pool);
    var before = image.ToArray();
    Assert.Catch(() => ops.Remove(image, ["vol"]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("HappyPath")]
  public void GivenAPoolOfVolumes_WhenListedThroughTheDescriptor_ThenVolumesAreFilesBesideTheFileSystem() {
    var ops = FormatRegistry.GetArchiveOps("Zfs")!;
    using var image = ZfsReferencePools.Open(Pool);
    var entries = ops.List(image, null).ToDictionary(static e => e.Name.Replace('\\', '/').TrimStart('/'), StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(entries["vol"].IsDirectory, Is.False);
      Assert.That(entries["vol"].OriginalSize, Is.EqualTo(8L * 1024 * 1024));
      Assert.That(entries["fs/vol2"].IsDirectory, Is.False);
      Assert.That(entries["fs"].IsDirectory, Is.True);
      Assert.That(entries.ContainsKey("fs/note.txt"), Is.True);
    });
  }
}
