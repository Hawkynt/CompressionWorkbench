using Compression.Registry;

namespace Compression.Tests.FatPlus;

/// <summary>
/// A file must be removable wherever it sits in the root directory.
/// </summary>
/// <remarks>
/// <para>A FAT32 root directory is a cluster chain, and the remover searched
/// only its first cluster. With a one-sector cluster that is sixteen entries,
/// so the seventeenth file onwards could be listed, read and verified — and not
/// removed, because the search never reached the half of the directory holding
/// it.</para>
///
/// <para>The count is what gives it away: the boundary is not a property of the
/// file but of where it landed. Removing the seventeenth file first, on a
/// volume nothing had been done to, failed exactly as removing it last did.</para>
/// </remarks>
[TestFixture]
public class FatPlusRootChainTests {

  [Test, Category("Regression")]
  public void FilesPastTheRootsFirstClusterCanBeRemoved() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("FatPlus")!;

    // Twenty files puts several past the sixteen a one-sector cluster holds.
    var inputs = new List<ArchiveInputInfo>();
    for (var i = 0; i < 20; ++i) {
      var data = new byte[4096 + i];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + i * 7);
      inputs.Add(ArchiveInputInfo.InMemory($"F{i:D4}.BIN", data));
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    image.Position = 0;
    var listed = ((IArchiveFormatOperations)ops).List(image, null);
    Assert.That(listed, Has.Count.EqualTo(20), "every file should be on the volume to begin with");

    // The seventeenth, on a volume nothing has been done to.
    image.Position = 0;
    Assert.DoesNotThrow(() => ((IArchiveModifiable)ops).Remove(image, ["F0016.BIN"]),
      "a file past the root's first cluster should still be findable");

    image.Position = 0;
    Assert.That(((IArchiveFormatOperations)ops).List(image, null), Has.Count.EqualTo(19));

    // And the rest of the far half, in one call.
    image.Position = 0;
    Assert.DoesNotThrow(() => ((IArchiveModifiable)ops).Remove(image,
      ["F0017.BIN", "F0018.BIN", "F0019.BIN"]));

    image.Position = 0;
    var left = ((IArchiveFormatOperations)ops).List(image, null);
    Assert.That(left, Has.Count.EqualTo(16));
    Assert.That(left.Select(e => Path.GetFileName(e.Name)), Has.No.Member("F0016.BIN"));
  }
}
