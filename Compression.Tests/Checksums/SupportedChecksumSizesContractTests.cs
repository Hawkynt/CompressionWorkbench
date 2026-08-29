using System.Reflection;
using Compression.Core.Checksums;
using Hawkynt.Algorithms.Checksums;
using NUnit.Framework;

namespace Compression.Tests.Checksums;

[TestFixture]
public sealed class SupportedChecksumSizesContractTests {
  private static readonly HashSet<Type> NonChecksumOutputTypes = [
    typeof(ReedSolomon)
  ];

  // Reflection discovers byte-oriented checksum APIs independently. The accessor table intentionally
  // references Type.SupportedChecksumSizes at compile time: a newly discovered checksum omitted from
  // this table fails the test, while a table entry without the metadata fails compilation.
  private static readonly IReadOnlyDictionary<Type, Func<IReadOnlyList<ChecksumSizeRange>>> MetadataAccessors =
    new Dictionary<Type, Func<IReadOnlyList<ChecksumSizeRange>>> {
      [typeof(Adler)] = static () => Adler.SupportedChecksumSizes,
      [typeof(Fletcher)] = static () => Fletcher.SupportedChecksumSizes,
      [typeof(BsdChecksum)] = static () => BsdChecksum.SupportedChecksumSizes,
      [typeof(SysVChecksum)] = static () => SysVChecksum.SupportedChecksumSizes,
      [typeof(SumChecksum)] = static () => SumChecksum.SupportedChecksumSizes,
      [typeof(Lrc)] = static () => Lrc.SupportedChecksumSizes,
      [typeof(XorChecksum)] = static () => XorChecksum.SupportedChecksumSizes,
      [typeof(InternetChecksum)] = static () => InternetChecksum.SupportedChecksumSizes,
      [typeof(ComplementChecksum)] = static () => ComplementChecksum.SupportedChecksumSizes,
      [typeof(Parity)] = static () => Parity.SupportedChecksumSizes,
      [typeof(Nmea0183)] = static () => Nmea0183.SupportedChecksumSizes,
      [typeof(Crc)] = static () => Crc.SupportedChecksumSizes,
      [typeof(Crc128)] = static () => Crc128.SupportedChecksumSizes,
      [typeof(Adler32)] = static () => Adler32.SupportedChecksumSizes,
      [typeof(Crc16)] = static () => Crc16.SupportedChecksumSizes,
      [typeof(Crc16Ccitt)] = static () => Crc16Ccitt.SupportedChecksumSizes,
      [typeof(Crc32)] = static () => Crc32.SupportedChecksumSizes,
      [typeof(Crc64)] = static () => Crc64.SupportedChecksumSizes
    };

  [Test]
  public void EveryBitOrientedChecksumExposesSupportedChecksumSizes() {
    var candidates = typeof(ChecksumSizeRange).Assembly
      .GetExportedTypes()
      .Where(IsChecksumApiType)
      .OrderBy(static type => type.FullName, StringComparer.Ordinal)
      .ToArray();

    var missing = candidates
      .Where(type => !MetadataAccessors.ContainsKey(type))
      .Select(static type => type.FullName)
      .ToArray();

    Assert.That(missing, Is.Empty,
      "Every public byte-oriented checksum must expose SupportedChecksumSizes. Missing: " + string.Join(", ", missing));

    Assert.Multiple(() => {
      foreach (var type in candidates) {
        var ranges = MetadataAccessors[type]();
        Assert.That(ranges, Is.Not.Null.And.Not.Empty, type.FullName);
        Assert.That(ranges.SelectMany(static range => range).Distinct().All(static bits => bits > 0),
          Is.True, $"{type.FullName} advertises an invalid checksum size");
      }
    });
  }

  [Test]
  public void FamilyMetadataMatchesImplementedWidths() {
    Assert.Multiple(() => {
      Assert.That(Adler.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {16, 32, 64}));
      Assert.That(Fletcher.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {8, 16, 32, 64}));
      Assert.That(SumChecksum.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {8, 16, 32}));
      Assert.That(ComplementChecksum.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {8, 16}));
      Assert.That(Parity.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {1, 8}));
      Assert.That(Crc.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(Enumerable.Range(8, 57)));
      Assert.That(Crc128.SupportedChecksumSizes.EnumerateSizes(), Is.EqualTo(new[] {128}));
    });
  }

  private static bool IsChecksumApiType(Type type) {
    if (NonChecksumOutputTypes.Contains(type))
      return false;
    if (type.Namespace is not ("Hawkynt.Algorithms.Checksums" or "Compression.Core.Checksums"))
      return false;

    return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Any(static method =>
        IsChecksumReturnType(method.ReturnType) &&
        method.GetParameters().Any(static parameter => parameter.ParameterType == typeof(ReadOnlySpan<byte>)));
  }

  private static bool IsChecksumReturnType(Type type) =>
    type == typeof(byte) ||
    type == typeof(ushort) ||
    type == typeof(uint) ||
    type == typeof(ulong) ||
    type == typeof(UInt128);
}
