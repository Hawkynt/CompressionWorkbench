using Compression.Registry;

namespace Compression.Tests.BenchmarkMatrix;

[TestFixture]
public class BenchmarkMatrixTests
{
    private static IEnumerable<TestCaseData> AllBlockPatterns()
    {
        Compression.Lib.FormatRegistration.EnsureInitialized();
        var blocks = BuildingBlockRegistry.All.OrderBy(b => b.DisplayName);

        var patterns = new (string Name, Func<byte[]> Generator)[]
        {
            ("Zeroes", () => new byte[4096]),
            ("Alternating", () =>
            {
                var b = new byte[4096];
                for (int i = 0; i < 4096; i++) b[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
                return b;
            }),
            ("Incrementing", () =>
            {
                var b = new byte[4096];
                for (int i = 0; i < 4096; i++) b[i] = (byte)(i & 0xFF);
                return b;
            }),
            ("Random", () =>
            {
                var r = new Random(42);
                var b = new byte[4096];
                r.NextBytes(b);
                return b;
            }),
            ("Text", () =>
            {
                var t = System.Text.Encoding.UTF8.GetBytes(
                    "The quick brown fox jumps over the lazy dog. ");
                var b = new byte[4096];
                for (int i = 0; i < 4096; i++) b[i] = t[i % t.Length];
                return b;
            }),
            ("SingleByte", () =>
            {
                var b = new byte[4096];
                Array.Fill(b, (byte)0x42);
                return b;
            }),
        };

        foreach (var block in blocks)
            foreach (var (name, gen) in patterns)
                yield return new TestCaseData(block.Id, block.DisplayName, name, gen())
                    .SetName($"{block.DisplayName} / {name}");
    }

    [Test]
    [TestCaseSource(nameof(AllBlockPatterns))]
    [CancelAfter(30000)]
    public void RoundTrip(string blockId, string displayName, string patternName, byte[] input)
    {
        var block = BuildingBlockRegistry.GetById(blockId)!;

        var compressed = block.Compress(input);

        Assert.That(compressed.Length, Is.GreaterThan(0),
            $"{displayName} produced empty compressed output on {patternName}");

        var decompressed = block.Decompress(compressed);

        Assert.That(decompressed, Is.EqualTo(input),
            $"{displayName} failed round-trip on {patternName} pattern " +
            $"(original={input.Length}, compressed={compressed.Length}, decompressed={decompressed.Length})");
    }
}
