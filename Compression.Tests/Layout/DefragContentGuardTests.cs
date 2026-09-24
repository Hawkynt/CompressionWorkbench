#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

[TestFixture]
public class DefragContentGuardTests {

  [Test]
  public void IdentitySnapshot_RejectsSamePayloadUnderAnotherPath() {
    using var image = new MemoryStream([1, 2, 3]);
    var rebuilt = false;
    var changed = false;

    DefragContentGuard.RunOrRebuild(
      image,
      readEntries: _ => [
        new DefragContentGuard.DefragContentEntry(
          changed ? "other/file.bin" : "dir/file.bin", false, [9])
      ],
      inPlace: () => changed = true,
      rebuild: () => rebuilt = true);

    Assert.That(rebuilt, Is.True);
  }

  [Test]
  public void IdentitySnapshot_PreservesDirectories() {
    using var image = new MemoryStream([1]);
    var rebuilt = false;
    var changed = false;

    DefragContentGuard.RunOrRebuild(
      image,
      readEntries: _ => changed
        ? [new DefragContentGuard.DefragContentEntry("file", false, [1])]
        : [new DefragContentGuard.DefragContentEntry("folder", true, [])],
      inPlace: () => changed = true,
      rebuild: () => rebuilt = true);

    Assert.That(rebuilt, Is.True);
  }

  [Test]
  public void IdentitySnapshot_RejectsMetadataChanges() {
    using var image = new MemoryStream([1]);
    var rebuilt = false;
    var changed = false;

    DefragContentGuard.RunOrRebuild(
      image,
      readEntries: _ => [new DefragContentGuard.DefragContentEntry(
        "file", false, [1], NativeAttributes: changed ? 0x20u : 0x01u)],
      inPlace: () => changed = true,
      rebuild: () => rebuilt = true);

    Assert.That(rebuilt, Is.True);
  }

  [Test]
  public void TupleReaderOverload_PreservesPathIdentity() {
    using var image = new MemoryStream([1]);
    var rebuilt = false;
    var changed = false;

    DefragContentGuard.RunOrRebuild(
      image,
      readEntries: _ => changed
        ? new[] { ("other/file", new byte[] { 1 }) }
        : new[] { ("dir/file", new byte[] { 1 }) },
      inPlace: () => changed = true,
      rebuild: () => rebuilt = true);

    Assert.That(rebuilt, Is.True);
  }
}
