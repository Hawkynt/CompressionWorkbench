using System.Diagnostics;
using System.Security.Cryptography;
using Compression.Tests.CodecProof;
using FileFormat.MacBinary;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.MacBinary;

/// <summary>
/// Third-party proof for MacBinary against CiderPress II 1.2.0 (fadden, Apache-2.0).
/// <list type="bullet">
/// <item><c>simple_45.sit.bin</c> from CiderPress II's test corpus (<c>TestData/stuffit/</c>,
/// revision <c>49b67b405a7f82a5b8be369351e381ca72b36796</c>, a StuffIt archive in MacBinary,
/// with a resource fork) decodes to the 175-byte data fork <c>cp2 extract --raw</c> wrote.</item>
/// <item>Where <c>cp2</c> is on the PATH or named by <c>CWB_TOOL_CP2</c>, it tests and extracts
/// the MacBinary files we write.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class MacBinaryCiderPressOracleTests {

  private const string StuffItInMacBinary =
    "AA1BcmNoaXZlNDUuc2l0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABTSVREU0lUIQAAAAAAAAAAAA" +
    "AAAACvAAABtOb/pSDm/6VrAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABTSVQhAAEAAACvckxhdQLHAAAAFpoKAA0KbHp3LXNhbXBsZWUw" +
    "LjA5LmltYWdlAAAAAAAAAAAAAALICYYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////VEVYVENQSUkBAOb+mvzm/pr8AAAAAAAAAtsAAAAAAA" +
    "AAKQAAFDsAAAAAAAAcD1G6YBBEIQoAd8gLQhCwQXc2yHIHfKCt20NS4OUIoQezk4X73kQtAQAAU0lUIQABAAAAr3JMYXUCxwAAABaaCgANCmx6" +
    "dy1zYW1wbGVlMC4wOS5pbWFnZQAAAAAAAAAAAAACyAmGAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAVoAAABaAAAAWkpAVsBEAEiAFgBKA2" +
    "cIDG8ACAAIbBBKA2YIDG8ABAAIDUFyY2hpdmU0NS5zaXRvbHMgUHJlZnNlqiA0LjVwZGYCBlNJVERTSVQhAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAA5v+lIAAAAK8AAAG0cAAQKABGNUAABCBXNWgASAAGPysARKiHPysASqiKcAAQKwBGPwCoiD88AAGoiVhPTN8MAE51LwpZTyRvAAxIV6h0IF" +
    "c0qABEIFc1aABKAAIgV3AAECgARjVAAAQgVzVoAEgABkJnqIdCZ6iKQmeoiD88AAGoiVhPJF9OdS8KWU8kbwAAAChUaGlzIGlzIGEgY29tbWVu" +
    "dCBvbiB0aGUgYXJjaGl2ZS4NDVlheS4NAAAAFgABAAAAAAAMAAoAAQAAAAkAAAAAAAAAAAAQACsABQEYAfYAAAAAAAADAAAAAQAAAAFaAAAAWg" +
    "AAAFoDr/RoExoAAAAcAFoAAlNpdEMAAAAac3R5bAAAACZTZXRnAAAAMgAA//8AAAAAAAAAAAAA//8AAAAsAAAAAAAA//8AAABGA7BUDAAAAQAA" +
    "AAFaAAAAWgAAAFpKQFbARABIgBYASgNnCAxvAAgACGwQSgNmCAxvAAQACA1BcmNoaXZlNDUuc2l0b2xzIFByZWZzZaogNC4=";

  [Test, Category("HappyPath")]
  public void GivenARealMacBinaryFile_WhenDecoded_ThenTheDataForkIsWhatCiderPressExtracts() {
    using var output = new MemoryStream();
    new MacBinaryFormatDescriptor().Decompress(new MemoryStream(Convert.FromBase64String(StuffItInMacBinary)), output);
    var data = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(data, Has.Length.EqualTo(175));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo("d3abb1f15cc71b0d2fe773d8e52c7618e003c1474debb0360c389bfd8898a5f8"));
    });
  }

  [TestCase(1, TestName = "GivenOneByte_WhenWrittenAsMacBinary_ThenCiderPressExtractsIt")]
  [TestCase(24_000, TestName = "GivenText_WhenWrittenAsMacBinary_ThenCiderPressExtractsIt")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void CiderPressReadsOurMacBinary(int length) {
    var cp2 = FindCp2();
    if (cp2 is null) Assert.Ignore("CiderPress II's cp2 is not on the PATH (or set CWB_TOOL_CP2).");
    var data = CodecProofData.Text(length);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-mb-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var file = Path.Combine(dir, "ours.bin");
      using (var output = File.Create(file))
        new MacBinaryFormatDescriptor().Compress(new MemoryStream(data), output);
      Assert.That(Run(cp2!, dir, "test", file), Does.Contain("no failures"));
      Run(cp2!, dir, "extract", "--raw", file);
      var extracted = Directory.GetFiles(dir).Single(f => !f.EndsWith("ours.bin", StringComparison.Ordinal));
      Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  private static string? FindCp2() {
    var configured = Environment.GetEnvironmentVariable("CWB_TOOL_CP2");
    if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
      return configured;
    try {
      Run("cp2", Path.GetTempPath(), "version");
      return "cp2";
    } catch (Win32Exception) {
      return null;
    }
  }

  private static string Run(string tool, string workingDirectory, params string[] arguments) {
    var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.EqualTo(0), $"cp2 {string.Join(' ', arguments)} failed:\n{stderr}");
    return stdout.Result;
  }
}
