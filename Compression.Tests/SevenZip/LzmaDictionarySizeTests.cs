using Compression.Lib;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// LZMA dictionary sizes snap to the values 7-Zip recognises (2^n or 3 x 2^(n-1)),
/// capped at 1 GiB. Every snapping helper must agree.
/// </summary>
[TestFixture]
public class LzmaDictionarySizeTests {

  private static IEnumerable<TestCaseData> Cases() {
    yield return new TestCaseData(1, 4096).SetName("{m}_Given_TinySize_When_Normalized_Then_Minimum4KiB");
    yield return new TestCaseData(4096, 4096).SetName("{m}_Given_4KiB_When_Normalized_Then_Unchanged");
    yield return new TestCaseData(5 << 20, 6 << 20).SetName("{m}_Given_5MiB_When_Normalized_Then_RoundsUpTo6MiB");
    yield return new TestCaseData(7 << 20, 8 << 20).SetName("{m}_Given_7MiB_When_Normalized_Then_RoundsUpTo8MiB");
    yield return new TestCaseData(768 << 20, 768 << 20).SetName("{m}_Given_768MiB_When_Normalized_Then_Unchanged");
    yield return new TestCaseData((768 << 20) + 1, 1 << 30).SetName("{m}_Given_JustAbove768MiB_When_Normalized_Then_1GiB");
    yield return new TestCaseData(1 << 30, 1 << 30).SetName("{m}_Given_1GiB_When_Normalized_Then_Unchanged");
    yield return new TestCaseData(int.MaxValue, 1 << 30).SetName("{m}_Given_IntMax_When_Normalized_Then_1GiB");
    yield return new TestCaseData(0, 4096).SetName("{m}_Given_Zero_When_Normalized_Then_Minimum4KiB");
    yield return new TestCaseData(-1, 4096).SetName("{m}_Given_Negative_When_Normalized_Then_Minimum4KiB");
    yield return new TestCaseData(4097, 6144).SetName("{m}_Given_JustAbove4KiB_When_Normalized_Then_6KiB");
    yield return new TestCaseData(6 << 20, 6 << 20).SetName("{m}_Given_ThreeHalvesSize_When_Normalized_Then_Unchanged");
  }

  [TestCaseSource(nameof(Cases))]
  [Category("Boundary")]
  public void CompressionOptions_NormalizeDictSize(int size, int expected)
    => Assert.That(CompressionOptions.NormalizeDictSize(size), Is.EqualTo(expected));

  [TestCaseSource(nameof(Cases))]
  [Category("Boundary")]
  public void ZipOptionsResolver_NormalizeDictSize(int size, int expected)
    => Assert.That(FileFormat.Zip.ZipOptionsResolver.NormalizeDictSize(size), Is.EqualTo(expected));

  [TestCaseSource(nameof(Cases))]
  [Category("Boundary")]
  public void SevenZipOptionsResolver_NormalizeDictSize(int size, int expected)
    => Assert.That(SevenZipOptionsResolver.NormalizeDictSize(size), Is.EqualTo(expected));

  [Test]
  [Category("Boundary")]
  public void Given_RequestAbove1GiB_When_Resolved_Then_CappedAt1GiB() {
    Assert.That(new CompressionOptions { DictSize = 4L << 30 }.ResolveLzmaDictSize(), Is.EqualTo(1 << 30));
    Assert.That(SevenZipOptionsResolver.ResolveLzmaDictSize(4L << 30, optimize: false), Is.EqualTo(1 << 30));
  }
}
