using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// NRV2B/D/E streams end with UCL's end-of-stream code: a match whose offset decodes to
/// 0xFFFFFFFF. UCL's decoders, and the UPX stubs built from them, stop only at that code.
/// Our encoders left it out, so UCL 1.03 failed every stream we wrote with an input overrun.
/// Asked for more bytes than the stream holds, an exact decode now reports the early end, and a
/// bounded decode stops cleanly at the code instead of running off the end of the stream.
/// </summary>
[TestFixture]
public sealed class NrvEndOfStreamTests {

  private static byte[] Text(int length) {
    var words = "upx packs the sections and the stub unpacks them again at run time "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(8) == 0 ? (byte)random.Next(256) : words[(i * 3 + i / 9) % words.Length];
    return data;
  }

  public static IEnumerable<TestCaseData> Cases() {
    foreach (var length in new[] { 0, 1, 23, 5_000, 70_000 }) {
      yield return new TestCaseData("NRV2B", length).SetName($"GivenNrv2bOf{length}Bytes_WhenDecodedPastItsEnd_ThenItStopsAtTheEndCode");
      yield return new TestCaseData("NRV2D", length).SetName($"GivenNrv2dOf{length}Bytes_WhenDecodedPastItsEnd_ThenItStopsAtTheEndCode");
      yield return new TestCaseData("NRV2E", length).SetName($"GivenNrv2eOf{length}Bytes_WhenDecodedPastItsEnd_ThenItStopsAtTheEndCode");
    }
  }

  [TestCaseSource(nameof(Cases)), Category("BoundaryCase")]
  public void TheStreamEndsWithTheEndCode(string method, int length) {
    var data = Text(length);
    var (block, exact, upTo) = method switch {
      "NRV2B" => (new Nrv2bBuildingBlock().Compress(data), (Func<byte[], int, byte[]>)((s, n) => Nrv2bBuildingBlock.DecompressRaw(s, n)), (Func<byte[], int, byte[]>)((s, n) => Nrv2bBuildingBlock.DecompressRawUpTo(s, n))),
      "NRV2D" => (new Nrv2dBuildingBlock().Compress(data), (s, n) => Nrv2dBuildingBlock.DecompressRaw(s, n), (s, n) => Nrv2dBuildingBlock.DecompressRawUpTo(s, n)),
      _ => (new Nrv2eBuildingBlock().Compress(data), (s, n) => Nrv2eBuildingBlock.DecompressRaw(s, n), (s, n) => Nrv2eBuildingBlock.DecompressRawUpTo(s, n)),
    };
    var bare = block[4..];

    Assert.Multiple(() => {
      Assert.That(exact(bare, length), Is.EqualTo(data), "exact size");
      Assert.That(upTo(bare, length + 64), Is.EqualTo(data), "stops at the end code");
      Assert.That(() => exact(bare, length + 1), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("end of stream"), "reports the early end");
    });
  }
}
