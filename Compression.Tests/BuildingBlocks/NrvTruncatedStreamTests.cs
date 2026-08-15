using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// A bare NRV stream carries no length or magic of its own, so packer handlers
/// identify one by decoding candidate offsets and seeing which attempt survives.
/// Those attempts feed the decoders arbitrary bytes, which makes running out of
/// input a routine event that must fail fast.
/// </summary>
/// <remarks>
/// The variable-length offset and length codes all terminate on a set bit. A
/// reader that answered "0" forever once its input was exhausted therefore never
/// left those loops: probing an NSPack sample whose section names merely look
/// like NRV candidates spun on a single 53 KB file for over ten minutes without
/// ever producing a result.
/// </remarks>
[TestFixture]
public class NrvTruncatedStreamTests {

  // All-zero bits drive every variable-length code down its "keep reading"
  // branch, so this is the exact shape that used to loop forever.
  private static readonly byte[] AllZero = new byte[16];

  private const int Target = 1 << 20;

  [Test, Category("EdgeCase"), CancelAfter(30000)]
  public void Nrv2bLe32_AllZeroInput_FailsInsteadOfSpinning()
    => Assert.That(() => Nrv2bBuildingBlock.DecompressRaw(AllZero, Target), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("EdgeCase"), CancelAfter(30000)]
  public void Nrv2bLe16_AllZeroInput_FailsInsteadOfSpinning()
    => Assert.That(() => Nrv2bBuildingBlock.DecompressRawLe16(AllZero, Target), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("EdgeCase"), CancelAfter(30000)]
  public void Nrv2bByte_AllZeroInput_FailsInsteadOfSpinning()
    => Assert.That(() => Nrv2bBuildingBlock.DecompressRawByte(AllZero, Target), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("EdgeCase"), CancelAfter(30000)]
  public void Nrv2d_AllZeroInput_FailsInsteadOfSpinning()
    => Assert.That(() => Nrv2dBuildingBlock.DecompressRaw(AllZero, Target), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("EdgeCase"), CancelAfter(30000)]
  public void Nrv2e_AllZeroInput_FailsInsteadOfSpinning()
    => Assert.That(() => Nrv2eBuildingBlock.DecompressRaw(AllZero, Target), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("EdgeCase"), CancelAfter(60000)]
  public void RandomNoise_AlwaysTerminates() {
    var random = new Random(20240815);
    for (var attempt = 0; attempt < 256; ++attempt) {
      var noise = new byte[32];
      random.NextBytes(noise);

      // Either the bytes are rejected or the requested size is produced; what a
      // decoder must never do is read past the end of its input forever looking
      // for a set bit.
      AssertTerminates(() => Nrv2bBuildingBlock.DecompressRaw(noise, 64 * 1024));
      AssertTerminates(() => Nrv2dBuildingBlock.DecompressRaw(noise, 64 * 1024));
      AssertTerminates(() => Nrv2eBuildingBlock.DecompressRaw(noise, 64 * 1024));
    }
  }

  private static void AssertTerminates(Func<byte[]> decode) {
    try {
      Assert.That(decode(), Has.Length.EqualTo(64 * 1024));
    } catch (InvalidDataException) {
      // Expected for input that is not a real NRV stream.
    }
  }
}
