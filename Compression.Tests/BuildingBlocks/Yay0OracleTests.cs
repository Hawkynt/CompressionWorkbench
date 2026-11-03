using System.Diagnostics;
using Compression.Core.Dictionary.Nintendo;
using Compression.Tests.CodecProof;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Yay0</c>. The oracle is crunch64 0.6.2 (decompals, MIT), the
/// compressor the N64 ROM-rebuild projects use to rebuild byte-identical ROMs, so its Yay0
/// is the one Nintendo shipped. Its streams below decode with our block; where Python with
/// crunch64 is installed, crunch64 decodes ours.
/// </summary>
[TestFixture]
public sealed class Yay0OracleTests {

  private static readonly Yay0BuildingBlock Block = new();

  // crunch64.yay0.compress(CodecProofData.Text(1024)), crunch64 0.6.2.
  private const string Crunch64Text =
    "WWF5MAAABAAAAAA4AAABJv///8//77/8+f//gB/+B+D8fgAfHwfgYH4A/BwH4E/A+AH4h8AAAABQEyAJQAdQOBBHUFRgDEAuMDBAWUCEQDWAgD" +
    "BbQHpQhyA+IKBgTkBzIGCwdyDPIHVA7UDOkF4wu0BUMNZAxUDLUPkg6UE3YMshP2DXQJxAokFdQTAg+6AVUVIhf0E0MSfAYUF0oS5Br8DrIcOx" +
    "NzGbYXchrUHtIY9R41HqwU5R/1IMQf4x+GHKIdZB8zIFsjVCYGGMQmoyO5FpYhhhakKTka8iqFK5QgQiryLDMo6SuFLkUusxVVLaIvKwT0LqgX" +
    "9C7IJcoJdRSZM9kQJCqENZQ2ljdjJDgnaSU0LiQ0izdmN1gcGgU5K5U7OzRjOBZXNjYXBlIG9yZGVyIHRoZSByYW5nZSBkb2dzeW1ib2wgcXVp" +
    "Y2sgMApqdW1wc2xhenkgNzkxOQpvdm1vZGVsMTU4MzgKZm94IGNvbnRleHQgYnJvd24gMjM3NTcKMzE2NzYKMzk1OTUKNDc1MTQKNTU0MzM2Mz" +
    "M1MjcxMjcxCjAKODcxMDkKOTUwMjgKMjk0MTA4NjMKdjE4NzgyCjI2NzAxMzQ2MjAKdjQyNTM5";

  // crunch64.yay0.compress(CodecProofData.Mixed(600)), crunch64 0.6.2.
  private const string Crunch64Mixed =
    "WWF5MAAAAlgAAABcAAAAXP////////////////////////////////////////////////////////////////////////////////////////" +
    "///////////wAZPjpdfF18N9C/NlU60O600w8uDzdfit7oB9Dev97YweA63knqjpq52HymmmtzkuDQen5+TGtJOp1zKSVEGQ9lPab+HdgPzv19" +
    "1/bBfGh35bDPLvaUmC6JqDYZz+LsYoHgDyy9yzta6ODAhysUM8+aBZ/77Qy0mKSG2Mbl6inxaRyfvtPg+BsMeJfGXcqhvVFw9uAM3r8qSXwM4F" +
    "VPAyJ42HiDZdz76pIWuzC11L+d4R3Bjq2SHJnYcmeGmPYtxo1AX9g+kHhdGThdOxnC9PIRfO4x7CvL6vbuBvoQpMPg05kk032cg58Ax/xWdV2m" +
    "TL7mL06kz/PvtQgnn9gNhorhAAi0ZemGutm/jkm1/pOy0+zN5/Zsi1XerAbARWQZhhguih49BthaMPn3FuCmQfG20O9JfNgjE6nIwF2woMKCoe" +
    "72t/a4W3rg+6LYtTRTxqavbrwNLKHY1pMy5gWmkwjucr/eDPST9sWYtwzQGJJwcZAvmAnOfUpptEULeTQjQkmJ0wPg/BvWLn4n59X0LL8GunCu" +
    "zZg67/gEh6a3QVtScWB/xg8eWMo5WGtlnRdQEjFdDKFom+sK+6HuMZnE40llgJv4nbyD7rMQu3aVMc0XY6dPbvZW4qKVKEenKEteoQEg03rrVj" +
    "ja+aYDFk1Ns9Iuw0Ybm4yrLjgva15lhPLuMRE4Pl1rCRH1iRc2za0+S3zwD9jY/JKXyeiXnySWKaLBCDp+iOJ7mjonm1E+VHMct0q+ri1Mvwbc" +
    "leQGJfQv9Yk43/7VKHenLbjXWyktacmRsNIMW4ntaok=";

  [Test, Category("HappyPath")]
  public void GivenCrunch64Text_WhenDecoded_ThenItIsTheSample()
    => Assert.That(Block.Decompress(Convert.FromBase64String(Crunch64Text)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenCrunch64MixedBytes_WhenDecoded_ThenItIsTheSample()
    => Assert.That(Block.Decompress(Convert.FromBase64String(Crunch64Mixed)), Is.EqualTo(CodecProofData.Mixed(600)));

  [Test, Category("ExceptionalCase")]
  public void GivenCrunch64Text_WhenTheDeclaredSizeIsTooLarge_ThenDecodingFails() {
    var stream = Convert.FromBase64String(Crunch64Text);
    stream[6] = 0x08; // declared size 0x400 -> 0x800; the tables run out first
    Assert.Throws<InvalidDataException>(() => Block.Decompress(stream));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenCrunch64Text_WhenTheChunkTableIsTruncated_ThenDecodingFails() {
    var stream = Convert.FromBase64String(Crunch64Text);
    Assert.Throws<InvalidDataException>(() => Block.Decompress(stream.AsSpan(0, stream.Length - 40)));
  }

  [TestCase(0, TestName = "GivenNoBytes_WhenOurYay0IsDecodedByCrunch64_ThenItIsEmpty")]
  [TestCase(1, TestName = "GivenOneByte_WhenOurYay0IsDecodedByCrunch64_ThenItIsTheInput")]
  [TestCase(4096, TestName = "GivenText_WhenOurYay0IsDecodedByCrunch64_ThenItIsTheInput")]
  [TestCase(-1, TestName = "GivenALongRun_WhenOurYay0IsDecodedByCrunch64_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void Crunch64ReadsOurYay0(int length) {
    var data = length < 0 ? Enumerable.Repeat((byte)'z', 5000).ToArray() : CodecProofData.Text(length);
    Assert.That(Crunch64Decompress(Block.Compress(data)), Is.EqualTo(data));
  }

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import crunch64");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on the PATH
      }
    }
    return null;
  });

  private static byte[] Crunch64Decompress(byte[] stream) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with crunch64 on the PATH (`pip install crunch64`).");
    var input = Path.GetTempFileName();
    try {
      File.WriteAllBytes(input, stream);
      var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add("import sys, crunch64; sys.stdout.write(crunch64.yay0.decompress(open(sys.argv[1], 'rb').read()).hex())");
      start.ArgumentList.Add(input);
      using var process = Process.Start(start)!;
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.EqualTo(0), $"crunch64 refused our stream:\n{stderr}");
      return Convert.FromHexString(stdout.Result);
    } finally {
      File.Delete(input);
    }
  }
}
