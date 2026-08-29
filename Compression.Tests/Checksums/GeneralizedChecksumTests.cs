using Hawkynt.Algorithms.Checksums;
using NUnit.Framework;

namespace Compression.Tests.Checksums;

[TestFixture]
public sealed class GeneralizedChecksumTests {
  [TestCase(2, "00")]
  [TestCase(4, "01")]
  [TestCase(8, "7A")]
  [TestCase(16, "4BE3")]
  [TestCase(24, "91E1DE")]
  [TestCase(32, "091E01DE")]
  [TestCase(40, "0091E001DE")]
  [TestCase(64, "0000091E000001DE")]
  [TestCase(72, "00000091E0000001DE")]
  [TestCase(128, "000000000000091E00000000000001DE")]
  [TestCase(136, "0000000000000091E000000000000001DE")]
  [TestCase(144, "00000000000000091E0000000000000001DE")]
  public void AdlerSupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(Convert.ToHexString(Adler.Compute("123456789"u8, checksumSizeBits)), Is.EqualTo(expectedHex));
  }

  [TestCase(2, "00")]
  [TestCase(4, "00")]
  [TestCase(8, "0C")]
  [TestCase(16, "1EDE")]
  [TestCase(24, "9151DD")]
  [TestCase(32, "091501DD")]
  [TestCase(40, "00915001DD")]
  [TestCase(64, "00000915000001DD")]
  [TestCase(128, "000000000000091500000000000001DD")]
  [TestCase(144, "0000000000000009150000000000000001DD")]
  public void FletcherSupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(Convert.ToHexString(Fletcher.Compute("123456789"u8, checksumSizeBits)), Is.EqualTo(expectedHex));
  }

  [TestCase(1, "01")]
  [TestCase(2, "01")]
  [TestCase(4, "0D")]
  [TestCase(8, "DD")]
  [TestCase(24, "0001DD")]
  [TestCase(40, "00000001DD")]
  public void SumSupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(Convert.ToHexString(SumChecksum.Compute("123456789"u8, checksumSizeBits)), Is.EqualTo(expectedHex));
  }

  [TestCase(1, "01")]
  [TestCase(2, "03")]
  [TestCase(4, "03")]
  [TestCase(8, "23")]
  [TestCase(24, "FFFE23")]
  [TestCase(40, "FFFFFFFE23")]
  public void TwosComplementSupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(
      Convert.ToHexString(ComplementChecksum.Compute("123456789"u8, checksumSizeBits, ComplementKind.TwosComplement)),
      Is.EqualTo(expectedHex)
    );
  }

  [TestCase(1, "00")]
  [TestCase(2, "00")]
  [TestCase(4, "03")]
  [TestCase(8, "21")]
  [TestCase(16, "F62A")]
  [TestCase(24, "63605D")]
  [TestCase(40, "98969492CA")]
  public void OnesComplementSupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(
      Convert.ToHexString(ComplementChecksum.Compute("123456789"u8, checksumSizeBits, ComplementKind.OnesComplement)),
      Is.EqualTo(expectedHex)
    );
  }

  [TestCase(1, "01")]
  [TestCase(2, "02")]
  [TestCase(4, "02")]
  [TestCase(8, "31")]
  [TestCase(16, "3908")]
  [TestCase(24, "323F3C")]
  [TestCase(40, "07050B0D35")]
  public void ParitySupportsGeneralizedWidths(int checksumSizeBits, string expectedHex) {
    Assert.That(Convert.ToHexString(Parity.Compute("123456789"u8, checksumSizeBits)), Is.EqualTo(expectedHex));
  }

  [Test]
  public void GeneralizedEntryPointsPreserveLegacyWidths() {
    ReadOnlySpan<byte> data = "123456789"u8;

    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(Adler.Compute(data, 16)), Is.EqualTo($"{Adler.Compute16(data):X4}"));
      Assert.That(Convert.ToHexString(Adler.Compute(data, 32)), Is.EqualTo($"{Adler.Compute32(data):X8}"));
      Assert.That(Convert.ToHexString(Adler.Compute(data, 64)), Is.EqualTo($"{Adler.Compute64(data):X16}"));

      Assert.That(Convert.ToHexString(Fletcher.Compute(data, 8)), Is.EqualTo($"{Fletcher.Compute8(data):X2}"));
      Assert.That(Convert.ToHexString(Fletcher.Compute(data, 16)), Is.EqualTo($"{Fletcher.Compute16(data):X4}"));
      Assert.That(Convert.ToHexString(Fletcher.Compute(data, 32)), Is.EqualTo($"{Fletcher.Compute32(data):X8}"));
      Assert.That(Convert.ToHexString(Fletcher.Compute(data, 64)), Is.EqualTo($"{Fletcher.Compute64(data):X16}"));

      Assert.That(Convert.ToHexString(SumChecksum.Compute(data, 8)), Is.EqualTo($"{SumChecksum.Compute8(data):X2}"));
      Assert.That(Convert.ToHexString(SumChecksum.Compute(data, 16)), Is.EqualTo($"{SumChecksum.Compute16(data):X4}"));
      Assert.That(Convert.ToHexString(SumChecksum.Compute(data, 32)), Is.EqualTo($"{SumChecksum.Compute32(data):X8}"));

      Assert.That(Convert.ToHexString(ComplementChecksum.Compute(data, 8)), Is.EqualTo($"{ComplementChecksum.TwosComplement8(data):X2}"));
      Assert.That(Convert.ToHexString(ComplementChecksum.Compute(data, 16)), Is.EqualTo($"{ComplementChecksum.TwosComplement16(data):X4}"));
      Assert.That(Convert.ToHexString(ComplementChecksum.Compute(data, 16, ComplementKind.OnesComplement)), Is.EqualTo($"{ComplementChecksum.OnesComplement16(data):X4}"));

      Assert.That(Convert.ToHexString(Parity.Compute(data, 8)), Is.EqualTo($"{Parity.BlockParity(data):X2}"));
    });
  }

  [TestCase(AdlerFamily, 1)]
  [TestCase(FletcherFamily, 1)]
  [TestCase(SumFamily, 3)]
  [TestCase(ComplementFamily, 12)]
  [TestCase(ParityFamily, 20)]
  public void UnsupportedWidthsAreRejected(string family, int checksumSizeBits) {
    Assert.Throws<ArgumentOutOfRangeException>(() => {
      _ = family switch {
        AdlerFamily => Adler.Compute("x"u8, checksumSizeBits),
        FletcherFamily => Fletcher.Compute("x"u8, checksumSizeBits),
        SumFamily => SumChecksum.Compute("x"u8, checksumSizeBits),
        ComplementFamily => ComplementChecksum.Compute("x"u8, checksumSizeBits),
        ParityFamily => Parity.Compute("x"u8, checksumSizeBits),
        _ => throw new InvalidOperationException()
      };
    });
  }

  private const string AdlerFamily = "Adler";
  private const string FletcherFamily = "Fletcher";
  private const string SumFamily = "Sum";
  private const string ComplementFamily = "Complement";
  private const string ParityFamily = "Parity";
}
