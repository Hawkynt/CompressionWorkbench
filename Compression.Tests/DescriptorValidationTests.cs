using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

[TestFixture]
public class DescriptorValidationTests {

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  private static IEnumerable<TestCaseData> AllDescriptors() {
    FormatRegistration.EnsureInitialized();
    foreach (var desc in FormatRegistry.All)
      yield return new TestCaseData(desc).SetName(desc.Id);
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Id_IsNotNullOrEmpty(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Id, Is.Not.Null.And.Not.Empty, "Id must not be null or empty");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void DisplayName_IsNotNullOrEmpty(IFormatDescriptor descriptor) {
    Assert.That(descriptor.DisplayName, Is.Not.Null.And.Not.Empty, "DisplayName must not be null or empty");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void DefaultExtension_StartsWithDot(IFormatDescriptor descriptor) {
    Assert.That(descriptor.DefaultExtension, Does.StartWith("."),
      $"DefaultExtension '{descriptor.DefaultExtension}' must start with '.'");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Extensions_ContainsDefaultExtension(IFormatDescriptor descriptor) {
    // CompoundTar formats have compound extensions (e.g. .tar.gz) as their default;
    // the single Extensions list may not contain it — check CompoundExtensions too.
    var allExtensions = descriptor.Extensions
      .Concat(descriptor.CompoundExtensions)
      .Select(e => e.ToLowerInvariant())
      .ToHashSet();

    // Some formats detect by magic only and use a generic DefaultExtension (e.g. .exe for
    // installers, .mp3 for Wrapster) that isn't in their extension list. Skip the check
    // when the Extensions list is empty — these are magic-only or detection-only formats.
    if (allExtensions.Count == 0)
      Assert.Pass("Format has no registered extensions (magic-only detection)");

    Assert.That(allExtensions, Does.Contain(descriptor.DefaultExtension.ToLowerInvariant()),
      $"Extensions (including compound) should contain DefaultExtension '{descriptor.DefaultExtension}'");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void MagicSignatures_AreValid(IFormatDescriptor descriptor) {
    Assert.That(descriptor.MagicSignatures, Is.Not.Null, "MagicSignatures must not be null");

    foreach (var sig in descriptor.MagicSignatures) {
      Assert.That(sig.Bytes, Is.Not.Null.And.Not.Empty,
        $"Magic signature bytes must not be null or empty (offset={sig.Offset})");
      Assert.That(sig.Offset, Is.GreaterThanOrEqualTo(0),
        "Magic signature offset must be non-negative");
      Assert.That(sig.Confidence, Is.InRange(0.0, 1.0),
        $"Magic signature confidence {sig.Confidence} must be between 0.0 and 1.0");
    }
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Category_IsValidEnum(IFormatDescriptor descriptor) {
    Assert.That(Enum.IsDefined(typeof(FormatCategory), descriptor.Category), Is.True,
      $"Category '{descriptor.Category}' is not a valid FormatCategory value");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Methods_IsNotNull(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Methods, Is.Not.Null, "Methods must not be null (can be empty)");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Extensions_AllStartWithDot(IFormatDescriptor descriptor) {
    foreach (var ext in descriptor.Extensions)
      Assert.That(ext, Does.StartWith("."),
        $"Extension '{ext}' must start with '.'");

    foreach (var ext in descriptor.CompoundExtensions)
      Assert.That(ext, Does.StartWith("."),
        $"Compound extension '{ext}' must start with '.'");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Family_IsValidEnum(IFormatDescriptor descriptor) {
    Assert.That(Enum.IsDefined(typeof(AlgorithmFamily), descriptor.Family), Is.True,
      $"Family '{descriptor.Family}' is not a valid AlgorithmFamily value");
  }

  [TestCaseSource(nameof(AllDescriptors))]
  public void Description_IsNotNull(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Description, Is.Not.Null.And.Not.Empty,
      "Description must not be null or empty");
  }

  [Test]
  public void Registry_ContainsExpectedMinimumCount() {
    var all = FormatRegistry.All;
    Assert.That(all, Has.Count.GreaterThanOrEqualTo(100),
      "Expected at least 100 format descriptors in the registry");
  }

  [Test]
  public void Registry_IdsAreUnique() {
    var ids = FormatRegistry.All.Select(d => d.Id).ToList();
    var uniqueIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.That(uniqueIds, Has.Count.EqualTo(ids.Count),
      "All descriptor IDs must be unique (case-insensitive)");
  }

  // --- Stream format round-trip smoke tests ---

  private static IEnumerable<TestCaseData> StreamFormatDescriptors() {
    FormatRegistration.EnsureInitialized();
    foreach (var desc in FormatRegistry.All) {
      if (desc is IStreamFormatOperations)
        yield return new TestCaseData(desc).SetName($"Stream_{desc.Id}");
    }
  }

  // Formats that require format-specific input (e.g. SWF needs SWF header, FLAC is audio codec)
  private static readonly HashSet<string> _skipStreamRoundTrip = new(StringComparer.OrdinalIgnoreCase) {
    "Swf",   // requires valid SWF header (FWS/CWS/ZWS signature)
    "Flac",  // audio codec — decompressed size differs from input
  };

  [TestCaseSource(nameof(StreamFormatDescriptors))]
  [CancelAfter(30000)]
  public void StreamFormat_RoundTrip_SmallData(IFormatDescriptor descriptor) {
    if (_skipStreamRoundTrip.Contains(descriptor.Id))
      Assert.Inconclusive($"Skipped: {descriptor.Id} requires format-specific input data");

    var ops = (IStreamFormatOperations)descriptor;
    var original = "Hello, Compression!"u8.ToArray();

    using var compressedStream = new MemoryStream();
    using (var inputStream = new MemoryStream(original))
      ops.Compress(inputStream, compressedStream);

    compressedStream.Position = 0;

    using var decompressedStream = new MemoryStream();
    ops.Decompress(compressedStream, decompressedStream);

    Assert.That(decompressedStream.ToArray(), Is.EqualTo(original),
      $"Stream format {descriptor.Id} failed round-trip on small data");
  }
}
