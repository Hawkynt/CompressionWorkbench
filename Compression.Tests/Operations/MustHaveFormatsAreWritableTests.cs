#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Regression guard for the UI drop-acceptance contract: every must-have
/// archive format must signal write-capability via at least one of:
/// <see cref="IArchiveCreatable"/>, <see cref="IArchiveModifiable"/>,
/// <see cref="FormatCapabilities.CanCreate"/>, or
/// <see cref="FormatCapabilities.CanModify"/>. The UI's
/// <c>EvaluateDropAgainstCurrentArchive</c> uses the same disjunction; if a
/// format passes this test it will not be flagged "read-only" on drop.
/// </summary>
[TestFixture]
public class MustHaveFormatsAreWritableTests {

  [TestCase("Zip")]
  [TestCase("SevenZip")]
  [TestCase("Rar")]
  [TestCase("Ace")]
  [TestCase("Cab")]
  [TestCase("Tar")]
  [TestCase("Lzh")]
  [TestCase("Arj")]
  public void DescriptorAdvertisesWriteCapability(string formatId) {
    FormatRegistration.EnsureInitialized();
    var desc = FormatRegistry.GetById(formatId);
    Assert.That(desc, Is.Not.Null, $"{formatId} descriptor not registered");

    var ops = FormatRegistry.GetArchiveOps(formatId);
    var caps = desc!.Capabilities;
    var hasInterface = ops is IArchiveCreatable or IArchiveModifiable;
    var hasFlag = caps.HasFlag(FormatCapabilities.CanCreate)
               || caps.HasFlag(FormatCapabilities.CanModify);

    Assert.That(hasInterface || hasFlag, Is.True,
      $"{formatId} does not advertise write capability — UI will block drops " +
      "with 'archive format is read-only'. Add IArchiveCreatable, " +
      "IArchiveModifiable, CanCreate, or CanModify.");
  }
}
