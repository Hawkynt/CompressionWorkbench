using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

/// <summary>
/// The streaming branch converters: converting a stream in arbitrary pieces gives exactly the
/// bytes of converting it whole, decoding inverts encoding, and the converters agree with the
/// whole-buffer filters wherever those are the same algorithm.
/// </summary>
[TestFixture]
public class BranchConverterTests {
  private static readonly BranchArchitecture[] All = Enum.GetValues<BranchArchitecture>();

  /// <summary>Code-like input: branch opcodes for every architecture, at every alignment.</summary>
  private static byte[] CodeLike(int size, int seed) {
    var random = new Random(seed);
    var data = new byte[size];
    random.NextBytes(data);
    for (var i = 0; i + 16 <= size; i += random.Next(1, 9)) {
      switch (random.Next(8)) {
        case 0: data[i] = 0xE8; data[i + 4] = (byte)(random.Next(2) == 0 ? 0x00 : 0xFF); break;
        case 1: data[i] = 0x0F; data[i + 1] = 0x85; break;
        case 2: data[i + 3] = 0xEB; break;
        case 3: data[i + 1] = 0xF0; data[i + 3] = 0xF8; break;
        case 4: data[i] = 0x48; data[i + 3] = 0x01; break;
        case 5: data[i] = 0x40; data[i + 1] = 0x00; break;
        case 6: data[i + 3] = 0x94; break;
        default: data[i] = 0xEF; data[i + 1] = 0x00; break;
      }
    }
    return data;
  }

  /// <summary>Converts <paramref name="data"/> the way a stream filter does: pieces, carry-over, final pass-through.</summary>
  private static byte[] Streamed(BranchArchitecture architecture, byte[] data, bool decoding, int seed) {
    var random = new Random(seed);
    var converter = BranchConverter.Create(architecture, decoding);
    var output = new List<byte>();
    var pending = new List<byte>();
    var offset = 0;
    while (offset < data.Length) {
      var take = Math.Min(random.Next(1, 300), data.Length - offset);
      pending.AddRange(data.AsSpan(offset, take).ToArray());
      offset += take;
      var buffer = pending.ToArray();
      var done = converter.Convert(buffer);
      output.AddRange(buffer.AsSpan(0, done).ToArray());
      pending = [.. buffer.AsSpan(done).ToArray()];
    }
    output.AddRange(pending);
    return [.. output];
  }

  private static byte[] Whole(BranchArchitecture architecture, byte[] data, bool decoding) {
    var copy = data.ToArray();
    BranchConverter.ConvertAll(architecture, copy, decoding);
    return copy;
  }

  [TestCaseSource(nameof(All))]
  public void Given_AnyArchitecture_When_ConvertedInRandomPieces_Then_TheResultEqualsTheWholeBufferConversion(BranchArchitecture architecture) {
    var data = CodeLike(50_000, (int)architecture);
    foreach (var decoding in new[] { false, true })
      for (var seed = 0; seed < 4; ++seed)
        Assert.That(Streamed(architecture, data, decoding, seed), Is.EqualTo(Whole(architecture, data, decoding)), $"decoding={decoding} seed={seed}");
  }

  [TestCaseSource(nameof(All))]
  public void Given_AnyArchitecture_When_EncodedThenDecoded_Then_TheOriginalComesBack(BranchArchitecture architecture) {
    var data = CodeLike(20_000, 100 + (int)architecture);
    Assert.That(Whole(architecture, Whole(architecture, data, decoding: false), decoding: true), Is.EqualTo(data));
  }

  [TestCaseSource(nameof(All))]
  public void Given_AnyArchitecture_When_TheBufferIsTooShortForAnInstruction_Then_NothingIsFinalYet(BranchArchitecture architecture) {
    var converter = BranchConverter.Create(architecture, decoding: true);
    Assert.That(converter.Convert(new byte[1]), Is.LessThanOrEqualTo(1));
    Assert.That(converter.Convert([]), Is.Zero);
  }

  [TestCase(BranchArchitecture.ArmThumb)]
  [TestCase(BranchArchitecture.Arm64)]
  [TestCase(BranchArchitecture.RiscV)]
  public void Given_AFilterBothImplementationsTakeFromLiblzma_When_Converted_Then_TheyAgree(BranchArchitecture architecture) {
    var data = CodeLike(30_000, 7);
    var (expectedEncode, expectedDecode) = architecture switch {
      BranchArchitecture.ArmThumb => (BcjFilter.EncodeArmThumb(data), BcjFilter.DecodeArmThumb(data)),
      BranchArchitecture.Arm64 => (BcjFilter.EncodeArm64(data), BcjFilter.DecodeArm64(data)),
      _ => (BcjFilter.EncodeRiscV(data), BcjFilter.DecodeRiscV(data)),
    };
    Assert.That(Whole(architecture, data, decoding: false), Is.EqualTo(expectedEncode));
    Assert.That(Whole(architecture, data, decoding: true), Is.EqualTo(expectedDecode));
  }

  [Test]
  public void Given_AnX86CallWithANearTarget_When_Encoded_Then_TheOperandBecomesAbsolute() {
    // CALL rel32 = +0x10 at offset 0: the absolute target is 0 + 5 + 0x10.
    byte[] data = [0xE8, 0x10, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90];
    var encoded = Whole(BranchArchitecture.X86, data, decoding: false);
    Assert.That(encoded[..5], Is.EqualTo(new byte[] { 0xE8, 0x15, 0x00, 0x00, 0x00 }));
  }

  [Test]
  public void Given_AnX86CallWithAFarOperand_When_Encoded_Then_ItIsLeftAlone() {
    // The operand's top byte is neither 0x00 nor 0xFF: not a plausible near call.
    byte[] data = [0xE8, 0x10, 0x00, 0x00, 0x42, 0x90, 0x90, 0x90, 0x90];
    Assert.That(Whole(BranchArchitecture.X86, data, decoding: false), Is.EqualTo(data));
  }
}
