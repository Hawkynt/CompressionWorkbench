using System.Diagnostics;
using Compression.Tests.CodecProof;
using FileFormat.BinHex;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.BinHex;

/// <summary>
/// Third-party proof for BinHex 4.0 against the Perl module Convert::BinHex 1.125 (Eryq and
/// Dianne Skoll, https://metacpan.org/pod/Convert::BinHex). The files below were written by its
/// encoder and decode here, every CRC checked; where <c>perl</c> with Convert::BinHex is
/// available, its decoder reads the data fork of every file we write.
/// </summary>
[TestFixture]
public sealed class BinHexConvertBinHexOracleTests {

  private const string ConvertBinHexText1K =
    "KFRoaXMgZmlsZSBtdXN0IGJlIGNvbnZlcnRlZCB3aXRoIEJpbkhleCA0LjApCjoiJzRLRyclITklOUI5KDRkSCgzISEhISEiISEhISEhIUpgUF" +
    "BGZjBLRic4SkVoKk5DQSlKRydLUCkoKktFUUcKUCknNFtDYiJbRlE0UEZMIk5FZkZKRmhQWUJRcFgpKCZlREAwVikkIStGQTlUQmZYSkRSOVlG" +
    "KC1KRWgqTkNBKQpKRScma0g1IWgxNiVqI1FwZkNBKUpDQTBNQkEiUCknZVtDJzlYKSc5Y0JmJmBDNSJVR0BlYEZiIWEwNkpjMSFUClFFaEpKQm" +
    "ZwWkcnOWlHIyJbR1E5YikoJmVEQDBWKSdwYkMnOWIpJ2VbQyc5WCknNFtDYiJbRlE0UEZMIlhCQVQKaikoJmVEQDBWKSgwakVAKltFIyJRRWhK" +
    "SkMncFIpJypiRWhHWikkKWMwYzhoI1EwW0VSNFBIKDNKRUBwTkNAYApKQ1FwaSknOWNCZiZgQzUiVUdAZWBGYiJORWZGSkNRcGkpJC1hME1GZi" +
    "NRcGJDJzliKSgmZURAMFYpJ2FLSFJOCkpGQTlUQmZYSkVoQ1BGTCJMRlFwaEVMIWMxNjhqMDNUWEJBVGopJ2VbQyc5WCknZVtDJzlYKSQzaDA2" +
    "JWQjUVQKZUVBImMpJ3BmQ0EpSkVoKk5DQSlKQmZwWkcnOWlHIyJkRCc4SkJmcFpHJzlpRyMiTEZRcGhFTCJMRlFwaEVMIgpbRlE0UEZMIlVHQG" +
    "VgRmIiUUVoSkpCUipbR2ZpSkVoKk5DQSlKMDY4ZC1jLStGQTlUQmZYSkcnS1ApJ2VbQyc5ClgpJEJjLWM4YiNRQ1tIIyJbRlE0UEZMIk1FZmpk" +
    "Q0FLZCknVGVFQSJjKSgmZURAMFYpJ3BiQyc5YikoKktFUUcKUCkkRmEtTUZhI1FUZUVBImMpJzRbQ2IiUUVoSkpDJ3BSKSgmZURAMFYpKDBqRU" +
    "AqW0UjIltHUTliKScwW0VSNApQSCgzSjBjTmExNiErRlEmWkNmOEpDUXBpKSgwakVAKltFIyJjSEBlTEVmYEpCZnBaRyc5aUcjIllFZjRQRSMi" +
    "CmNIQGVMRWZgSjEkRmEtJE4rRmhQWUJRcFgpJ1RlRUEiYyknYUtIUk5KQmZwWkcnOWlHIyJRRWhKSkVAcE5DQGAKSkVoQ1BGTCJjSEBlTEVmYE" +
    "pGQTlUQmZYSkVoKk5DQSlKMTY4YC1NSitDJ3BSKSdDW0gjImJCQGpSQzUiWEJBVApqKSdhS0hSTkpFQHBOQ0BgSkJmcFpHJzlpRyMhYjE2M2Qj" +
    "UVRlRUEiYykoKktFUUdQKSgmZURAMFYpJ2FLSFJOCkpHJ0tQKSc5Y0JmJmBDNSJMRlFwaEVMIk5FZkZKLTYhaTBNLStHJ0tQKSdhS0hSTkpDJ3" +
    "BSKSgwakVAKltFIyIKUEZmMEtGJzhKQ0EwTUJBIlApJ0NbSCMiW0dROWIpJzRbQ2IiZEQnOEotNkpoMSQpK0NBME1CQSJQKScqYkVoRwpaKSgm" +
    "ZURAMFYpJ0NbSCMiW0ZRNFBGTCJVR0BlYEZiIllFZjRQRSMiUUVoSkpEUjlZRigtSkZRJlpDZjhKLU1CCmgtJCUrRlEmWkNmOEpGUSZaQ2Y4Sk" +
    "MncFIpJ3BmQ0EpSkZoUFlCUXBYKScqYkVoR1opKCpLRVFHUCkncGJDJzkKYiknOWNCZiZgQzUiW0dROWIpJ3BmQ0EpSkJSKltHZmlKRWhDUEZM" +
    "IltGUTRQRkwhYzAkQmItIVRMRlFwaEVMIgpZRWY0UEUjImFHQFBNRGIiVUdAZWBGYiJbR1E5YiknYUtIUk5KRlEmWkNmOEpDUXBpKSc5Y0JmJm" +
    "BDNSJbR1E5CmIpJ2FLSFJOSkVAcE5DQGBKMCQpZS1jTitGQTlUQmZYSkZBOVRCZlhKRmhQWUJRcFgpJzBbRVI0UE0nMyEhISEKIToK";

  private const string ConvertBinHexAbc =
    "KFRoaXMgZmlsZSBtdXN0IGJlIGNvbnZlcnRlZCB3aXRoIEJpbkhleCA0LjApCjoiJzRLRyclITklOUI5KDRkSCgzISEhISEhIU4hISEhISxHNC" +
    "IzTjAiM04wIjNOMHFFMyEhOgo=";

  private const string ConvertBinHexEmpty =
    "KFRoaXMgZmlsZSBtdXN0IGJlIGNvbnZlcnRlZCB3aXRoIEJpbkhleCA0LjApCjoiJzRLRyclITklOUI5KDRkSCgzISEhISEhISEhISEhIUtESi" +
    "EhISEhOgo=";

  // Decodes the data fork of a BinHex file to a raw file: perl -e <script> <in> <out>.
  private const string PerlDecoder =
    "use Convert::BinHex; open(my $f,'<:raw',$ARGV[0]) or die; my $h=Convert::BinHex->open(FH=>$f) or die; " +
    "$h->read_header; my @d=$h->read_data; my @r=$h->read_resource; open(my $o,'>:raw',$ARGV[1]) or die; print $o join('',@d); close $o;";

  private static IEnumerable<TestCaseData> TheirFiles() {
    yield return new TestCaseData(ConvertBinHexText1K, CodecProofData.Text(1024)).SetName("GivenConvertBinHexsText_WhenDecoded_ThenItIsTheSample");
    yield return new TestCaseData(ConvertBinHexAbc, "ABCABCABC"u8.ToArray()).SetName("GivenConvertBinHexsShortFile_WhenDecoded_ThenItIsTheData");
    yield return new TestCaseData(ConvertBinHexEmpty, Array.Empty<byte>()).SetName("GivenConvertBinHexsEmptyFile_WhenDecoded_ThenTheForkIsEmpty");
  }

  [TestCaseSource(nameof(TheirFiles)), Category("HappyPath")]
  public void DecodesConvertBinHex(string file, byte[] expected) {
    var decoded = BinHexReader.Decode(new MemoryStream(Convert.FromBase64String(file)));
    Assert.Multiple(() => {
      Assert.That(decoded.DataFork, Is.EqualTo(expected));
      Assert.That(decoded.ResourceFork, Is.Empty);
      Assert.That(decoded.FileName, Is.EqualTo("data"));
      Assert.That(decoded.FileType, Is.EqualTo("TEXT"));
      Assert.That(decoded.FileCreator, Is.EqualTo("ttxt"));
    });
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void ConvertBinHexReadsOurFiles() {
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], "abracadabra abracadabra"u8.ToArray(), new byte[] { 0x90, 0x90, 0x90 }, Array.Empty<byte>() }) {
      using var file = new MemoryStream();
      BinHexWriter.Write(file, "data", sample);
      PerlReads(file.ToArray(), sample);
    }
  }

  private static void PerlReads(byte[] file, byte[] expected) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-binhex-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.hqx");
      var unpackedPath = Path.Combine(dir, "unpacked.bin");
      File.WriteAllBytes(packedPath, file);
      var start = new ProcessStartInfo("perl") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach (var argument in new[] { "-e", PerlDecoder, packedPath, unpackedPath })
        start.ArgumentList.Add(argument);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("perl is not on the PATH.");
        return;
      }

      string stderr;
      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (stderr.Contains("Can't locate Convert/BinHex.pm", StringComparison.Ordinal))
          Assert.Ignore("The Perl module Convert::BinHex is not installed.");
        Assert.That(process.ExitCode, Is.EqualTo(0), $"Convert::BinHex refused our file:\n{stdout.Result}{stderr}");
      }

      Assert.That(File.ReadAllBytes(unpackedPath), Is.EqualTo(expected));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
