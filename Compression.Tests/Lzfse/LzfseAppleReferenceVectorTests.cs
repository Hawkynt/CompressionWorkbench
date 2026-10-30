using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Lzfse;
using Compression.Core.Dictionary.Lzvn;
using FileFormat.Lzfse;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Lzfse;

/// <summary>
/// LZFSE and LZVN against Apple's own library. The <c>BB_Lzfse</c> and <c>BB_Lzvn</c> building
/// blocks used to be codecs of their own, shaped like Apple's but read by nothing else; they are
/// now the <see cref="LzfseStream"/> codec. The fixtures are streams Apple's lzfse wrote (see
/// ReferenceVectors/README.md); where Python with pyliblzfse, a build of Apple's lzfse, is
/// installed, Apple's decoder also reads what we write.
/// </summary>
[TestFixture]
public sealed class LzfseAppleReferenceVectorTests {

  private static readonly LzfseBuildingBlock LzfseBlock = new();
  private static readonly LzvnBuildingBlock LzvnBlock = new();

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LzfseVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource LzfseVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  public static IEnumerable<TestCaseData> AppleStreams() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2])
        .SetName($"GivenAppleLzfse_{fields[0]}_WhenDecoded_ThenItIsTheInput");
    }
  }

  [TestCaseSource(nameof(AppleStreams)), Category("HappyPath")]
  public void AppleStreamDecodes(string file, int length, string sha256) {
    var decoded = LzfseBlock.Decompress(Resource(file));
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheLzvnApplePutInABvxnBlock_WhenDecodedByTheLzvnBlock_ThenItIsTheBlocksData() {
    // apple-text-3k.lzfse is one bvxn block: magic, raw size, payload size, payload; then bvx$.
    var stream = Resource("apple-text-3k.lzfse");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(stream), Is.EqualTo(0x6E787662u));
    var rawBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(4));
    var payloadBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(8));
    var decoded = LzvnBlock.Decompress(stream.AsSpan(12, payloadBytes));
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(rawBytes));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo("eb862be6e046b7bcbeaf68646d4141e5bb46d4b447649cd7912aaba8c8006179"));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenTheStreamAppleWritesForNoInput_WhenDecoded_ThenItIsEmpty()
    // liblzfse.compress(b""): an empty stored block, then the end of stream. pyliblzfse
    // reports an empty result as an error, so our empty stream has no oracle check.
    => Assert.That(LzfseBlock.Decompress(Convert.FromHexString("6276782d0000000062767824")), Is.Empty);

  [Test, Category("ExceptionalCase")]
  public void GivenAnLzvnStreamWithoutEos_WhenDecodedByTheLzvnBlock_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => LzvnBlock.Decompress(Convert.FromHexString("e3414243")));

  [Test, Category("BoundaryCase")]
  public void GivenNoInput_WhenTheLzvnBlockCompresses_ThenItIsJustTheEos() {
    var stream = LzvnBlock.Compress([]);
    Assert.Multiple(() => {
      Assert.That(stream, Is.EqualTo(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0 }));
      Assert.That(LzvnBlock.Decompress(stream), Is.Empty);
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAStreamThatOutgrowsTheFirstGuess_WhenDecodedByTheLzvnBlock_ThenItIsWhole() {
    var data = new byte[1 << 20];
    Assert.That(LzvnBlock.Decompress(LzvnBlock.Compress(data)), Is.EqualTo(data));
  }

  // ── Apple's decoder: pyliblzfse (Apple's lzfse built as a Python extension) ──

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import liblzfse");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  private static byte[] AppleDecompress(byte[] stream) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with Apple's lzfse on the PATH (`pip install pyliblzfse`).");
    var input = Path.GetTempFileName();
    var output = Path.GetTempFileName();
    try {
      File.WriteAllBytes(input, stream);
      var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add("import sys, liblzfse; open(sys.argv[2], 'wb').write(liblzfse.decompress(open(sys.argv[1], 'rb').read()))");
      start.ArgumentList.Add(input);
      start.ArgumentList.Add(output);
      using var process = Process.Start(start)!;
      var stderr = process.StandardError.ReadToEndAsync();
      process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.EqualTo(0), $"Apple's lzfse refused our stream:\n{stderr.Result}");
      return File.ReadAllBytes(output);
    } finally {
      File.Delete(input);
      File.Delete(output);
    }
  }

  private static byte[] Text(int length) {
    var words = "the quick brown fox jumps over the lazy dog while apple compresses files "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(10) == 0 ? (byte)random.Next(256) : words[(i * 7 + i / 13) % words.Length];
    return data;
  }

  [TestCase(1, TestName = "GivenOneByte_WhenOurLzfseBlockIsDecodedByApple_ThenItIsTheInput")]
  [TestCase(3000, TestName = "GivenSmallText_WhenOurLzfseBlockIsDecodedByApple_ThenItIsTheInput")]
  [TestCase(1_100_000, TestName = "GivenMultiBlockText_WhenOurLzfseBlockIsDecodedByApple_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void AppleReadsOurLzfse(int length) {
    var data = Text(length);
    Assert.That(AppleDecompress(LzfseBlock.Compress(data)), Is.EqualTo(data));
  }

  [TestCase(1, TestName = "GivenOneByte_WhenOurLzvnBlockIsDecodedByApple_ThenItIsTheInput")]
  [TestCase(3000, TestName = "GivenSmallText_WhenOurLzvnBlockIsDecodedByApple_ThenItIsTheInput")]
  [TestCase(65536, TestName = "GivenADecmpfsChunk_WhenOurLzvnBlockIsDecodedByApple_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void AppleReadsOurLzvn(int length) {
    var data = Text(length);
    var payload = LzvnBlock.Compress(data);
    // Apple's API takes LZFSE streams, so the LZVN stream goes into a bvxn block as Apple frames it.
    var stream = new byte[12 + payload.Length + 4];
    BinaryPrimitives.WriteUInt32LittleEndian(stream, 0x6E787662u);
    BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(4), data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(8), payload.Length);
    payload.CopyTo(stream, 12);
    BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(12 + payload.Length), 0x24787662u);
    Assert.That(AppleDecompress(stream), Is.EqualTo(data));
  }
}
