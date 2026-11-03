using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Tests.CodecProof;
using FileFormat.ApLib;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.ApLib;

/// <summary>
/// Third-party proof for the AP32 stream format against aPLib 1.1.1 (Jørgen Ibsen,
/// https://ibsensoftware.com/products_aPLib.html): the streams below were written by its
/// <c>appack</c> example (<c>appack c</c>, which calls <c>aPsafe_pack</c>), and they decode here.
/// Where <c>appack</c> is on the PATH or named by <c>CWB_TOOL_APPACK</c>, it also unpacks every
/// file we write (<c>appack d</c>, <c>aPsafe_depack</c>). Neither aPLib function accepts an
/// empty input, so we refuse one as well.
/// </summary>
[TestFixture]
public sealed class ApLibAppackOracleTests {

  private const string AppackText1K =
    "QVAzMhgAAACiAQAAXHj+oQAEAAAp2L11ZQ5zY2FwoSBvcmTrn2N0aHkU/m5vZwzOb7QU0AoHc3ltYudssHF1aWN9aykwCggfaoVtcHMwJWxhen" +
    "t5HDc5MYYKb3YfhlV9bYFsWQ0ILzEcNTgzgwpmb3jTY6BudGXvz8VQMVKQTJA2mu8cXIQhjojRfyB8fXI/d24HMjM3NSUKZE8mPkWNeDRfQRUz" +
    "MTY3yo2lsh11C19Di0JVDDM5NQQKqhwSUhEGNJ3WMRUK+tpdOqMyfahADOhISAZIJkg3SIahFuc1EzQzjJ1TqYA1RjYoJDUyKGhi0hJEtC+ssB" +
    "k3MTIGCl7sahE48giySlqSrojLPk1dGSQNB15PQxUyOMYwQuIaDRJsmiIzLUOPMUVqJjbRrZRUEjAyEP+hpCR9VENq9GBOMjk0kWtIKUqweqop" +
    "4FKyTAhTnRI4DTcKNg1TI7nQK9AH1FbJdrrbBS8xHNmkbSFQ6GxNVVu8SF3kaJgymFMkMDFK6Z4+pwOhVCEcNKHOIklEkjl3I1TBMzQ2MiGgCR" +
    "cJeDZ3y3YUGqfCIVTKUHg0Mkw1zklh0m0PTWmAAA==";

  private const string AppackMixed600 =
    "QVAzMhgAAACPAgAAfgIgLlgCAADuMApUGQw+Ol18BDcP0L82VV1B7rTTDy7L6F+K3j7oB/R2vw/YweA6YEnqjpq59Hx1ph9rc5LH0Hp+F0yRST" +
    "qd8CklAEQZD2U9pv4dd9gAzv191/bBfGgHd+Wwzy6TlJigiag2ehkA4uxigeAPLL0Pyzta6ADAhysUMwDPmgWf++0MtACYpIbYxuXqKQDxaRyf" +
    "vtPg+AAbDHiXxl3KoQ+9UXD23zDyKkl87eBVTw4DInjYQINl3PvqAJIWuzC11L+dB+EdwY6t0ByZ2HIAZ4aY9i3GjUB7XwM+kHhdGTibO6DC9P" +
    "IAEXzuMewry+p39gAG+hCkw+DTmXMkDn2cg58Ax/xWdV0Apky+5i9OpM8A8++1CCef2A0chorhPI+0ZekAutm/jkkAtf6TstPszecA9myLVd6s" +
    "BsAARWQZhhguih57PQDYWjD59xbgpgVB8bbQ7x+bwCMTqcjAXQCwoMKCoe72t+S4AFt64Pui2LU0AFPGpq9uvA0sfKEH1pMy5gXeogjuckBFDP" +
    "T69h3FmLfA0BiScHGQei8ACc59Smm0RQsAeTQjQkmJ0wMA4Pwb1i5+J+cA1fQsvwa6cK4AzZg67/gEh6YAt0FbUnFgf8YHDx5Yyjkwa2WdFwBQ" +
    "EjFdDKFomxvrCvvy2JnE4x9JZYBD+J28g+izELsAdpUxzRdjp08HbvZW4qLDKEfXMEteoQEAINN661Y42vkOpgMWTSCz0i7DRg4bm4yr4Dgva1" +
    "5lM4TyihHRPl3tCdj1iQ8XNs2tYEt88A/Y4vwOkpfJ6GCfJJYpogDBCDp+iOJ7muwnAJtRPlRzHLdKCr6uLUynB9yV5EEl9C+Abjjf/tUodwOn" +
    "LbjXWymoacmRHrDSDEeJ7Wo8AA==";

  private const string AppackAbracadabra = "QVAzMhgAAAAOAAAA+m7WZxcAAABODhAFYTlicrpjqGQHUiAM2AA=";

  // appack c of "ABCABCABC"; our encoder writes the same 31 bytes.
  private const string AppackAbc = "QVAzMhgAAAAHAAAAHYEhKQkAAAD3Vu8CQSlCQwMwAA==";

  private static byte[] Abracadabra => Encoding.ASCII.GetBytes("abracadabra abracadabra");

  private static IEnumerable<TestCaseData> AppackFiles() {
    yield return new TestCaseData(AppackText1K, CodecProofData.Text(1024)).SetName("GivenAppacksText_WhenDecoded_ThenItIsTheSample");
    yield return new TestCaseData(AppackMixed600, CodecProofData.Mixed(600)).SetName("GivenAppacksMixedData_WhenDecoded_ThenItIsTheSample");
    yield return new TestCaseData(AppackAbracadabra, Abracadabra).SetName("GivenAppacksShortPhrase_WhenDecoded_ThenItIsThePhrase");
    yield return new TestCaseData(AppackAbc, "ABCABCABC"u8.ToArray()).SetName("GivenAppacksRepeat_WhenDecoded_ThenItIsTheRepeat");
  }

  [TestCaseSource(nameof(AppackFiles)), Category("HappyPath")]
  public void DecodesAppack(string file, byte[] expected)
    => Assert.That(Decompress(Convert.FromBase64String(file)), Is.EqualTo(expected));

  [Test, Category("HappyPath")]
  public void GivenARepeat_WhenCompressed_ThenItIsWhatAppackWrites()
    => Assert.That(Compress("ABCABCABC"u8.ToArray()), Is.EqualTo(Convert.FromBase64String(AppackAbc)));

  [Test, Category("ExceptionalCase")]
  public void GivenNoBytes_WhenCompressed_ThenItIsRefusedLikeAPsafePack()
    => Assert.Throws<ArgumentException>(() => Compress([]));

  [Test, Category("ExceptionalCase")]
  public void GivenAZeroOriginalSize_WhenDecoded_ThenItIsRefused() {
    var header = new byte[24];
    BinaryPrimitives.WriteUInt32LittleEndian(header, 0x32335041u);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 24);
    Assert.Throws<InvalidDataException>(() => Decompress(header));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAZeroHeaderSize_WhenDecoded_ThenItIsRefused() {
    // The private dialect this codec once wrote put zero in the header-size field; aPLib reads no such file.
    var file = Convert.FromBase64String(AppackAbc);
    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0);
    Assert.Throws<InvalidDataException>(() => Decompress(file));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenACorruptPayload_WhenDecoded_ThenTheCrcRefusesIt() {
    var file = Convert.FromBase64String(AppackText1K);
    file[40] ^= 0x10;
    Assert.Throws<InvalidDataException>(() => Decompress(file));
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void AppackUnpacksOurFiles() {
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], Abracadabra, new byte[] { 0x42 } }) {
      AppackUnpacks(Compress(sample), sample);
      using var input = new MemoryStream(sample);
      using var output = new MemoryStream();
      ApLibStream.CompressOptimal(input, output);
      AppackUnpacks(output.ToArray(), sample);
    }
  }

  private static byte[] Compress(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    ApLibStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    ApLibStream.Decompress(input, output);
    return output.ToArray();
  }

  private static void AppackUnpacks(byte[] packed, byte[] expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_APPACK");
    if (string.IsNullOrEmpty(tool))
      tool = "appack";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-appack-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.ap32");
      var unpackedPath = Path.Combine(dir, "unpacked.bin");
      File.WriteAllBytes(packedPath, packed);
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach (var argument in new[] { "d", packedPath, unpackedPath })
        start.ArgumentList.Add(argument);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("appack is not on the PATH (or set CWB_TOOL_APPACK).");
        return;
      }

      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), $"appack refused our file:\n{stdout.Result}{stderr}");
      }

      Assert.That(File.ReadAllBytes(unpackedPath), Is.EqualTo(expected));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
