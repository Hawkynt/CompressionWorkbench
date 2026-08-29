using Hawkynt.Algorithms.Hashing;
using NUnit.Framework;

namespace Compression.Tests.Hashing;

[TestFixture]
public sealed class UnifiedHashFamilyTests {
  private static readonly byte[] Data = "abc"u8.ToArray();

  [Test]
  public void RipemdFacadeMatchesLegacyWrappers() {
    Assert.Multiple(() => {
      Assert.That(Ripemd.Compute(Data, 128), Is.EqualTo(Ripemd128.Compute(Data)));
      Assert.That(Ripemd.Compute(Data, 160), Is.EqualTo(Ripemd160.Compute(Data)));
      Assert.That(Ripemd.Compute(Data, 256), Is.EqualTo(Ripemd256.Compute(Data)));
      Assert.That(Ripemd.Compute(Data, 320), Is.EqualTo(Ripemd320.Compute(Data)));
      Assert.That(Ripemd.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 128, 160, 256, 320 }));
    });
  }

  [Test]
  public void EchoFacadeMatchesLegacyWrappers() {
    Assert.Multiple(() => {
      Assert.That(Echo.Compute(Data, 224), Is.EqualTo(Echo224.Compute(Data)));
      Assert.That(Echo.Compute(Data, 256), Is.EqualTo(Echo256.Compute(Data)));
      Assert.That(Echo.Compute(Data, 384), Is.EqualTo(Echo384.Compute(Data)));
      Assert.That(Echo.Compute(Data, 512), Is.EqualTo(Echo512.Compute(Data)));
      Assert.That(Echo.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
    });
  }

  [Test]
  public void LuffaFacadeMatchesLegacyWrappers() {
    Assert.Multiple(() => {
      Assert.That(Luffa.Compute(Data, 224), Is.EqualTo(Luffa224.Compute(Data)));
      Assert.That(Luffa.Compute(Data, 256), Is.EqualTo(Luffa256.Compute(Data)));
      Assert.That(Luffa.Compute(Data, 384), Is.EqualTo(Luffa384.Compute(Data)));
      Assert.That(Luffa.Compute(Data, 512), Is.EqualTo(Luffa512.Compute(Data)));
      Assert.That(Luffa.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
    });
  }

  [Test]
  public void LshFacadesMatchLegacyWrappers() {
    Assert.Multiple(() => {
      Assert.That(Lsh256Family.Compute(Data, 224), Is.EqualTo(Lsh224.Compute(Data)));
      Assert.That(Lsh256Family.Compute(Data, 256), Is.EqualTo(Lsh256.Compute(Data)));
      Assert.That(Lsh512Family.Compute(Data, 256), Is.EqualTo(Lsh512_256.Compute(Data)));
      Assert.That(Lsh512Family.Compute(Data, 384), Is.EqualTo(Lsh384.Compute(Data)));
      Assert.That(Lsh512Family.Compute(Data, 512), Is.EqualTo(Lsh512.Compute(Data)));
      Assert.That(Lsh256Family.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256 }));
      Assert.That(Lsh512Family.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256, 384, 512 }));
    });
  }

  [Test]
  public void Sha512FacadeMatchesCompatibilityMethods() {
    Assert.Multiple(() => {
      Assert.That(Sha512Family.Compute(Data, 224), Is.EqualTo(Sha512Family.Compute512_224(Data)));
      Assert.That(Sha512Family.Compute(Data, 256), Is.EqualTo(Sha512Family.Compute512_256(Data)));
      Assert.That(Sha512Family.Compute(Data, 384), Is.EqualTo(Sha512Family.Compute384(Data)));
      Assert.That(Sha512Family.Compute(Data, 512), Is.EqualTo(Sha512Family.Compute512(Data)));
      Assert.That(Sha512Family.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
    });
  }

  [Test]
  public void RequestedFamiliesExposeEnumerableRangeMetadata() {
    Assert.Multiple(() => {
      Assert.That(HamsiFamily.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
      Assert.That(Kupyna.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 256, 384, 512 }));
      Assert.That(Fugue.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 224, 256, 384, 512 }));
      Assert.That(Tiger.SupportedHashSizes.EnumerateSizes(), Is.EqualTo(new[] { 192 }));
    });
  }

  [Test]
  public void RequestedFamilyRangesDriveOutputLength() {
    foreach (var bits in HamsiFamily.SupportedHashSizes.EnumerateSizes())
      Assert.That(HamsiFamily.Compute(Data, bits), Has.Length.EqualTo(bits / 8), $"Hamsi-{bits}");
    foreach (var bits in Kupyna.SupportedHashSizes.EnumerateSizes())
      Assert.That(Kupyna.Compute(Data, bits), Has.Length.EqualTo(bits / 8), $"Kupyna-{bits}");
    foreach (var bits in Fugue.SupportedHashSizes.EnumerateSizes())
      Assert.That(Fugue.Compute(Data, bits), Has.Length.EqualTo(bits / 8), $"Fugue-{bits}");
    foreach (var bits in Tiger.SupportedHashSizes.EnumerateSizes())
      Assert.That(Tiger.Compute(Data, bits), Has.Length.EqualTo(bits / 8), $"Tiger-{bits}");
  }
}
