using System.Reflection;
using Compression.Core.Dictionary.BriefLz;
using Compression.Core.Dictionary.Lizard;
using Compression.Core.Dictionary.Lzg;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// <c>BB_BriefLz</c>, <c>BB_Lzg</c> and <c>BB_Lizard</c> were codecs of their own, which the
/// reference libraries rejected and which rejected the libraries' streams. The formats were
/// already real. These blocks are now the format codecs: the streams the reference tools wrote
/// decode through them (see FormatBlockVectors/README.md).
/// </summary>
[TestFixture]
public sealed class BriefLzLzgLizardBlockTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FormatBlockVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource FormatBlockVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static IBuildingBlock Block(string tool) => tool switch {
    "blzpack" => new BriefLzBuildingBlock(),
    "lzg" => new LzgBuildingBlock(),
    _ => new LizardBuildingBlock(),
  };

  [TestCase("blzpack", "text", TestName = "GivenBlzpacksText_WhenTheBriefLzBlockDecodes_ThenItIsTheSample")]
  [TestCase("blzpack", "small", TestName = "GivenBlzpacksShortInput_WhenTheBriefLzBlockDecodes_ThenItIsTheSample")]
  [TestCase("lzg", "text", TestName = "GivenLzgsText_WhenTheLzgBlockDecodes_ThenItIsTheSample")]
  [TestCase("lzg", "small", TestName = "GivenLzgsShortInput_WhenTheLzgBlockDecodes_ThenItIsTheSample")]
  [TestCase("lizard", "text", TestName = "GivenLizardsText_WhenTheLizardBlockDecodes_ThenItIsTheSample")]
  [TestCase("lizard", "small", TestName = "GivenLizardsShortInput_WhenTheLizardBlockDecodes_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void ToolStreamDecodes(string tool, string sample)
    => Assert.That(Block(tool).Decompress(Vector($"{tool}.{sample}.bin")), Is.EqualTo(Vector($"sample-{sample}.bin")));

  [TestCase("blzpack", TestName = "GivenTheBriefLzBlock_WhenItCompresses_ThenItWritesTheBlzpackSignature")]
  [TestCase("lzg", TestName = "GivenTheLzgBlock_WhenItCompresses_ThenItWritesTheLzgHeader")]
  [TestCase("lizard", TestName = "GivenTheLizardBlock_WhenItCompresses_ThenItWritesTheLizardFrameMagic")]
  [Category("HappyPath")]
  public void TheBlockWritesTheFormat(string tool) {
    var stream = Block(tool).Compress(Vector("sample-text.bin"));
    var expected = tool switch {
      "blzpack" => "blz\u001a"u8.ToArray(),
      "lzg" => "LZG"u8.ToArray(),
      _ => new byte[] { 0x06, 0x22, 0x4D, 0x18 },
    };
    Assert.Multiple(() => {
      Assert.That(stream[..expected.Length], Is.EqualTo(expected));
      Assert.That(Block(tool).Decompress(stream), Is.EqualTo(Vector("sample-text.bin")));
    });
  }
}
