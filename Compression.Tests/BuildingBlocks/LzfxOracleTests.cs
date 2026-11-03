using Compression.Core.Dictionary.Lzfx;
using Compression.Tests.CodecProof;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Lzfx</c> against Andrew Collette's lzfx (BSD-2-Clause; the
/// code.google.com export at github.com/AmritKumar/lzfx, revision
/// <c>ac7bc91754159cbd434d1783dbe8845965bffd74</c>), built with gcc 16.2 (MinGW-w64) and driven
/// through <c>lzfx_compress</c>/<c>lzfx_decompress</c> on raw buffers. LZFX streams carry no
/// header; the block adds a 4-byte little-endian length, which is not part of the stream.
/// <list type="bullet">
/// <item>Streams <c>lzfx_compress</c> wrote decode with our decoder.</item>
/// <item><c>lzfx_decompress</c> restored the stream our encoder writes for the same text.
/// Our parser differs from lzfx's (it compresses that text to 486 bytes, lzfx to 508), so the
/// stream is pinned: if the encoder changes, re-run lzfx on its new output.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class LzfxOracleTests {

  private const string LzfxText =
    "GWVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9noBNACQ5zeW1ib2wgcXVpY2sgMAqABwRqdW1wc6AkC2xhenkgNzkxOQpvdiBHAGWAVARtb2RlbM" +
    "AMgC4RMTU4MzgKZm94IGNvbnRleHQgYDCAUYCEgDUAZOAAgGBbgCCgh0A+QB8LYnJvd24gMjM3NTcKwE6APUAhoIQAamCmAGQgTkAUBTMxNjc2" +
    "CoBogFPgAl5gioBUBTM5NTk1CmAbgFGABQY0NzUxNApqYFJgLYBJwHxBP8ALgEeABYAlgDZAheADFQQ1NTQzM6FSQD+AaAQ2MzM1MmEngDvAVY" +
    "BDgLmAGQByYa8ENzEyNzGgmABkoOtAB4ApoTdgscBJQa0BMAqAPUCToCOgBsAlgI2gFAU4NzEwOQqgDABqYQRhIcAsQEaAMGBpoCiAgoCsBjk1" +
    "MDI4CmSgoIB8YEJgBIA7wE0CMjk0wWqAKIBIYClBKKHfgUtAUgUxMDg2MwpAGmAjQBKggqAqoAZApmCgQCFALgUxODc4MgqgHYBPgGtgKWMZAG" +
    "pg7YCgQBWAD4CXBDI2NzAxoUmAEQBkIOBgYaB/gFOAG4BPoGxgJGAEgCJgCoAhBTM0NjIwCoAWgHeAkwBqYIlgKGDmgFVAk+ADU2AagDYENDI1" +
    "MzmiYIBCoJUhaAF0ZQ==";

  private const string LzfxMixed =
    "Hxk+Ol18XXw30L82VTrQ7rTTDy4PN1+K3ugH0N6/3tjBH+A63knqjpq52HymmmtzkuDQen5+TGtJOp1zKSVEGQ9lHz2m/h3YD879fdf2wXxod+" +
    "Wwzy72lJguiag2Gc/i7GKBH+APLL3LO1ro4MCHKxQzz5oFn/vtDLSYpIbYxuXqKfFpHxyfvtPg+BsMeJfGXcqhvVFw9uAM3r8qSXwM4FVPAyJ4" +
    "H9h4g2Xc++qSFrswtdS/neEdwY6tkhyZ2HJnhpj2LcaNH0Bf2D6QeF0ZOF07GcL08hF87jHsK8vq9u4G+hCkw+DTH5kk032cg58Ax/xWdV2mTL" +
    "7mL06kz/PvtQgnn9gNhorhHwAItGXphrrZv45Jtf6TstPszef2bItV3qwGwEVkGYYYHy6KHj0G2Fow+fcW4KZB8bbQ70l82CMTqcjAXbCgwoKh" +
    "H+72t/a4W3rg+6LYtTRTxqavbrwNLKHY1pMy5gWmkwjuH3K/3gz0k/bFmLcM0BiScHGQL5gJzn1KabRFC3k0I0JJH4nTA+D8G9Yufifn1fQsvw" +
    "a6cK7NmDrv+ASHprdBW1JxH2B/xg8eWMo5WGtlnRdQEjFdDKFom+sK+6HuMZnE40llH4Cb+J28g+6zELt2lTHNF2OnT272VuKilShHpyhLXqEB" +
    "HyDTeutWONr5pgMWTU2z0i7DRhubjKsuOC9rXmWE8u4xHxE4Pl1rCRH1iRc2za0+S3zwD9jY/JKXyeiXnySWKaLBHwg6fojie5o6J5tRPlRzHL" +
    "dKvq4tTL8G3JXkBiX0L/WJFzjf/tUod6ctuNdbKS1pyZGw0gxbie1qiQ==";

  private const string OursText =
    "GWVzY2FwZSBvcmRlciB0aGUgcmFuZ2UgZG9noBNACQ5zeW1ib2wgcXVpY2sgMAqABwRqdW1wc6AkC2xhenkgNzkxOQpvdiBHoFQFbW9kZWwgoA" +
    "yALhExNTgzOApmb3ggY29udGV4dCBgMIBRgISANUB2gA9gW4AgoIdAPkAfC2Jyb3duIDIzNzU3CsBOgD1AIeAEd0AuQBQFMzE2NzYKgFiAU+AC" +
    "XmCKgFQFMzk1OTUKYBuAUYAFBTQ3NTE0CoDKYC2AScB8QT/AC4BHgAWAJYA2QIXgAxUENTU0MzOhUkA/gGgENjMzNTJhJ4A7wFWAQ4C5gBmBrw" +
    "Q3MTI3MaCYwOtAB4ApoTdgscBJQa0BMAqAPUCToCOgBsAlgI2gFAU4NzEwOQqgDICJYSHALEBGgDBgaaAogIKArAU5NTAyOApAmEArgHxgQmAE" +
    "gDvATQIyOTTBaoAogEhgKUEood+BS0BSBTEwODYzCkAaYCNAEqCCoCqgBkB6YKBAIUAuBTE4NzgyCqAdgE+Aa2ApYxmA7YCgQBWAD4CXBDI2Nz" +
    "AxoUmAEUBYYGGgf4BTgBuBCKBsYCRgBIAiYAqAIQUzNDYyMAqAFoB3gJOAeWAoYOaAVUCT4ANTYBqANgQ0MjUzOaJggEKglWFo";

  [Test, Category("HappyPath")]
  public void GivenLzfxText_WhenDecoded_ThenItIsTheSample()
    => Assert.That(LzfxDecompressor.Decompress(Convert.FromBase64String(LzfxText), 1024), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenLzfxMixedBytes_WhenDecoded_ThenItIsTheSample()
    => Assert.That(LzfxDecompressor.Decompress(Convert.FromBase64String(LzfxMixed), 600), Is.EqualTo(CodecProofData.Mixed(600)));

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressed_ThenItIsTheStreamLzfxRestored()
    => Assert.That(LzfxCompressor.Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(OursText)));

  [Test, Category("HappyPath")]
  public void GivenTheSampleMixedBytes_WhenCompressed_ThenItIsWhatLzfxWrites()
    => Assert.That(LzfxCompressor.Compress(CodecProofData.Mixed(600)), Is.EqualTo(Convert.FromBase64String(LzfxMixed)));

  [Test, Category("RoundTrip")]
  public void GivenLzfxText_WhenFramedForTheBlock_ThenTheBlockDecodesIt() {
    var stream = Convert.FromBase64String(LzfxText);
    var framed = new byte[4 + stream.Length];
    BitConverter.TryWriteBytes(framed, 1024);
    stream.CopyTo(framed, 4);
    Assert.That(new LzfxBuildingBlock().Decompress(framed), Is.EqualTo(CodecProofData.Text(1024)));
  }
}
