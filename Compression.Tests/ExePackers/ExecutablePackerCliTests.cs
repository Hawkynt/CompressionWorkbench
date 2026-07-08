using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Core.Crypto;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Gzip;
using FileFormat.Xz;
using FileFormat.Zstd;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class ExecutablePackerCliTests {
  [Test, Category("HappyPath")]
  public void Inspect_UnpackCapabilities_ReportsExecutablePackerOutputs() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-gzexe");
    File.WriteAllBytes(input, BuildGzexeWrapper("#!/bin/sh\necho cli inspect\n"u8.ToArray()));

    var result = RunCli(cli, "inspect", input, "--unpack-capabilities");

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.EqualTo(0), result.StdErr);
      Assert.That(result.StdOut, Does.Contain("Packer: gzexe executable wrapper (gzexe)"));
      Assert.That(result.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(result.StdOut, Does.Contain("reconstructed/original_executable.bin"));
    });
  }

  [Test, Category("HappyPath")]
  public void Extract_StrictRebuild_WritesRequestedReconstructedExecutable() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-gzexe");
    var output = Path.Combine(temp.Path, "out");
    var original = "#!/bin/sh\necho cli extract\n"u8.ToArray();
    File.WriteAllBytes(input, BuildGzexeWrapper(original));

    var result = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/original_executable.bin");

    var reconstructed = Path.Combine(output, "reconstructed", "original_executable.bin");
    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.EqualTo(0), result.StdErr);
      Assert.That(File.Exists(reconstructed), Is.True);
      Assert.That(File.ReadAllBytes(reconstructed), Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Extract_StrictRebuild_RejectsUnsupportedInput() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "plain.bin");
    File.WriteAllBytes(input, "not a packed executable"u8.ToArray());

    var result = RunCli(cli, "extract", input, "--strict-rebuild", "-o", Path.Combine(temp.Path, "out"));

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.EqualTo(1));
      Assert.That(result.StdErr, Does.Contain("No supported executable packer was detected."));
    });
  }

  [Test, Category("HappyPath")]
  public void Inspect_AndExtract_StrictRebuild_HandlePapawWrapper() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-papaw");
    var output = Path.Combine(temp.Path, "out");
    var original = "#!/bin/sh\necho cli papaw\n"u8.ToArray();
    File.WriteAllBytes(input, BuildPapawWrapper(original));

    var inspect = RunCli(cli, "inspect", input, "--unpack-capabilities");
    var extract = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/original_executable.bin");

    var reconstructed = Path.Combine(output, "reconstructed", "original_executable.bin");
    Assert.Multiple(() => {
      Assert.That(inspect.ExitCode, Is.EqualTo(0), inspect.StdErr);
      Assert.That(inspect.StdOut, Does.Contain("Packer: Papaw executable wrapper (papaw)"));
      Assert.That(inspect.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(extract.ExitCode, Is.EqualTo(0), extract.StdErr);
      Assert.That(File.ReadAllBytes(reconstructed), Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Inspect_AndExtract_StrictRebuild_HandleGoPackerWrapper() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-gopacker");
    var output = Path.Combine(temp.Path, "out");
    var original = "#!/bin/sh\necho cli gopacker\n"u8.ToArray();
    File.WriteAllBytes(input, BuildGoPackerWrapper(original));

    var inspect = RunCli(cli, "inspect", input, "--unpack-capabilities");
    var extract = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/original_executable.bin");

    var reconstructed = Path.Combine(output, "reconstructed", "original_executable.bin");
    Assert.Multiple(() => {
      Assert.That(inspect.ExitCode, Is.EqualTo(0), inspect.StdErr);
      Assert.That(inspect.StdOut, Does.Contain("Packer: GoPacker executable wrapper (gopacker)"));
      Assert.That(inspect.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(extract.ExitCode, Is.EqualTo(0), extract.StdErr);
      Assert.That(File.ReadAllBytes(reconstructed), Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Inspect_AndExtract_StrictRebuild_HandleOrigamiWrapper() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-origami.exe");
    var output = Path.Combine(temp.Path, "out");
    var original = "MZ cli origami assembly"u8.ToArray();
    File.WriteAllBytes(input, BuildOrigamiWrapper(original));

    var inspect = RunCli(cli, "inspect", input, "--unpack-capabilities");
    var extract = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/original_assembly.bin");

    var reconstructed = Path.Combine(output, "reconstructed", "original_assembly.bin");
    Assert.Multiple(() => {
      Assert.That(inspect.ExitCode, Is.EqualTo(0), inspect.StdErr);
      Assert.That(inspect.StdOut, Does.Contain("Packer: Origami .NET executable wrapper (origami)"));
      Assert.That(inspect.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(extract.ExitCode, Is.EqualTo(0), extract.StdErr);
      Assert.That(File.ReadAllBytes(reconstructed), Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Inspect_AndExtract_StrictRebuild_HandleSilentPackerWrapper() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-silent-packer");
    var output = Path.Combine(temp.Path, "out");
    var originalText = "cli silent text"u8.ToArray();
    File.WriteAllBytes(input, BuildSilentPackerElf64(originalText));

    var inspect = RunCli(cli, "inspect", input, "--unpack-capabilities");
    var extract = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/reconstructed.elf");

    var reconstructed = Path.Combine(output, "reconstructed", "reconstructed.elf");
    Assert.Multiple(() => {
      Assert.That(inspect.ExitCode, Is.EqualTo(0), inspect.StdErr);
      Assert.That(inspect.StdOut, Does.Contain("Packer: Silent_Packer ELF XOR wrapper (silent_packer)"));
      Assert.That(inspect.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(extract.ExitCode, Is.EqualTo(0), extract.StdErr);
      Assert.That(File.ReadAllBytes(reconstructed).AsSpan(0x100, originalText.Length).ToArray(),
        Is.EqualTo(originalText).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Inspect_AndExtract_StrictRebuild_HandleHuanWrapper() {
    var cli = LocateCli();
    using var temp = new TempDirectory();
    var input = Path.Combine(temp.Path, "packed-huan.exe");
    var output = Path.Combine(temp.Path, "out");
    var original = MinimalPe();
    File.WriteAllBytes(input, BuildHuanWrapper(original));

    var inspect = RunCli(cli, "inspect", input, "--unpack-capabilities");
    var extract = RunCli(cli, "extract", input, "--strict-rebuild", "-o", output, "reconstructed/reconstructed.exe");

    var reconstructed = Path.Combine(output, "reconstructed", "reconstructed.exe");
    Assert.Multiple(() => {
      Assert.That(inspect.ExitCode, Is.EqualTo(0), inspect.StdErr);
      Assert.That(inspect.StdOut, Does.Contain("Packer: Huan PE64 encrypted loader (huan)"));
      Assert.That(inspect.StdOut, Does.Contain("Level: RebuiltExecutable"));
      Assert.That(extract.ExitCode, Is.EqualTo(0), extract.StdErr);
      Assert.That(File.ReadAllBytes(reconstructed), Is.EqualTo(original).AsCollection);
    });
  }

  private static string LocateCli() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("The CLI project currently builds the win-x64 host executable.");

    var configuration = IsDebug() ? "Debug" : "Release";
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var path = Path.Combine(root, "Compression.CLI", "bin", configuration, "net10.0", "win-x64", "cwb.exe");
    if (!File.Exists(path))
      Assert.Ignore($"CLI executable was not built at {path}.");
    return path;
  }

  private static CliResult RunCli(string cli, params string[] args) {
    var start = new ProcessStartInfo(cli) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (var arg in args)
      start.ArgumentList.Add(arg);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI process.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new(process.ExitCode, stdout, stderr);
  }

  private static byte[] BuildGzexeWrapper(byte[] original) {
    using var compressed = new MemoryStream();
    using (var gzip = new GzipStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      gzip.Write(original);

    var header = System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\ngzip -cd \"$0\"\n");
    var result = new byte[header.Length + compressed.Length];
    header.CopyTo(result.AsSpan());
    compressed.ToArray().CopyTo(result.AsSpan(header.Length));
    return result;
  }

  private static byte[] BuildPapawWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var xz = new XzStream(compressed, CompressionStreamMode.Compress, dictionarySize: 512 * 1024, checkType: 0, leaveOpen: true))
      xz.Write(original);

    var obfuscated = compressed.ToArray();
    obfuscated[0] = 0; obfuscated[1] = 0; obfuscated[2] = 0; obfuscated[3] = 0x08; obfuscated[4] = 0;
    obfuscated[^2] = 0; obfuscated[^1] = 0;

    var result = new byte[stub.Length + obfuscated.Length + 8];
    stub.CopyTo(result.AsSpan());
    obfuscated.CopyTo(result.AsSpan(stub.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 8), (uint)original.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), (uint)obfuscated.Length);
    return result;
  }

  private static byte[] BuildGoPackerWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var zstd = new ZstdStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(original);

    var compressedBytes = compressed.ToArray();
    var result = new byte[stub.Length + compressedBytes.Length + 16];
    stub.CopyTo(result.AsSpan());
    compressedBytes.CopyTo(result.AsSpan(stub.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(stub.Length + compressedBytes.Length), (ulong)compressedBytes.Length);
    "LALALALA"u8.CopyTo(result.AsSpan(result.Length - 8));
    return result;
  }

  private static byte[] BuildOrigamiWrapper(byte[] original) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int sectionOffset = optionalOffset + 0xE0;
    const int sectionRaw = 0x400;
    const uint sectionRva = 0x2000;
    const uint cliRva = 0x2000;
    const uint metadataRva = 0x2100;
    const uint methodRva = 0x2500;
    const uint payloadRva = 0x2600;
    const string key = "0123456789ABCDEF0123456789ABCDEF";

    var compressed = DeflateCompressor.Compress(original, DeflateCompressionLevel.Default);
    var encrypted = compressed.ToArray();
    var keyBytes = Encoding.UTF8.GetBytes(key);
    for (var i = 0; i < encrypted.Length; i++)
      encrypted[i] ^= keyBytes[i % keyBytes.Length];

    var image = new byte[0x4000];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), methodRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 56), 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 60), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 92), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8), cliRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8 + 4), 0x48);
    ".text\0\0\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), sectionRaw);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0x60000020);

    var cliOffset = sectionRaw + (int)(cliRva - sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset), 0x48);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 8), metadataRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 12), 0x300);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 20), 0x06000001);
    WriteOrigamiMetadata(image, sectionRaw + (int)(metadataRva - sectionRva), key, methodRva);

    var methodOffset = sectionRaw + (int)(methodRva - sectionRva);
    image[methodOffset] = (byte)((14 << 2) | 0x2);
    image[methodOffset + 1] = 0x21;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(methodOffset + 2), payloadRva);
    image[methodOffset + 10] = 0x20;
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(methodOffset + 11), encrypted.Length);
    encrypted.CopyTo(image.AsSpan(sectionRaw + (int)(payloadRva - sectionRva)));
    return image;
  }

  private static void WriteOrigamiMetadata(byte[] image, int offset, string key, uint methodRva) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), 0x424A5342);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 6), 1);
    var version = "v4.0.30319\0"u8.ToArray();
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 12), version.Length);
    version.CopyTo(image.AsSpan(offset + 16));
    var streamHeaderOffset = (offset + 16 + version.Length + 3) & ~3;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(streamHeaderOffset + 2), 2);
    var cursor = streamHeaderOffset + 4;
    WriteStreamHeader(image, ref cursor, 0x100, 0x80, "#~");
    WriteStreamHeader(image, ref cursor, 0x200, 0x80, "#Strings");
    var tables = offset + 0x100;
    image[tables + 4] = 2;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(tables + 8), (1UL << 0) | (1UL << 6));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 24), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 28), 1);
    var method = tables + 42;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(method), methodRva);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 6), 0x16);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 8), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 12), 1);
    Encoding.UTF8.GetBytes(key).CopyTo(image.AsSpan(offset + 0x201));
  }

  private static void WriteStreamHeader(byte[] image, ref int cursor, int offset, int size, string name) {
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor), offset);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor + 4), size);
    cursor += 8;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.CopyTo(image.AsSpan(cursor));
    cursor += nameBytes.Length;
    image[cursor++] = 0;
    cursor = (cursor + 3) & ~3;
  }

  private static byte[] BuildSilentPackerElf64(byte[] originalText) {
    const ulong key = 0x1122334455667788;
    const ulong textAddress = 0x401000;
    const int textOffset = 0x100;
    const ulong loaderAddress = 0x402000;
    const int loaderOffset = 0x200;
    const int loaderSize = 0x80;
    const int sectionHeaderOffset = 0x600;

    var image = new byte[0x800];
    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), loaderAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), sectionHeaderOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), 4);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 3);

    XorSilentPacker64(originalText, key).CopyTo(image.AsSpan(textOffset));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(loaderOffset + loaderSize - 36), checked((int)((long)textAddress - ((long)loaderAddress + loaderSize - 32))));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 32), key);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 24), textAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 16), (ulong)originalText.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 8), loaderAddress);

    var strings = "\0.text\0.dec\0.shstrtab\0"u8.ToArray();
    strings.CopyTo(image.AsSpan(0x500));
    WriteElf64Section(image, sectionHeaderOffset + 64, 1, textAddress, textOffset, originalText.Length);
    WriteElf64Section(image, sectionHeaderOffset + 128, 7, loaderAddress, loaderOffset, loaderSize);
    WriteElf64Section(image, sectionHeaderOffset + 192, 12, 0, 0x500, strings.Length);
    return image;
  }

  private static byte[] XorSilentPacker64(ReadOnlySpan<byte> data, ulong key) {
    var result = data.ToArray();
    var rolling = key;
    for (var i = 0; i < result.Length; i++) {
      result[i] ^= (byte)rolling;
      rolling = (rolling >> 8) | (rolling << 56);
    }
    return result;
  }

  private static void WriteElf64Section(byte[] image, int offset, uint nameIndex, ulong address, int fileOffset, int size) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), nameIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 8), 0x6);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 16), address);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 24), (ulong)fileOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 32), (ulong)size);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 48), 16);
  }

  private static byte[] MinimalPe() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    return buf;
  }

  private static byte[] BuildHuanWrapper(byte[] original) {
    var key = "0123456789ABCDEF"u8.ToArray();
    var iv = "FEDCBA9876543210"u8.ToArray();
    var encryptedLength = ((original.Length + 15) / 16) * 16;
    var padded = new byte[encryptedLength];
    original.CopyTo(padded.AsSpan());
    var encrypted = AesCryptor.EncryptCbcNoPaddingAny(padded, key, iv);
    var payloadLength = 40 + encrypted.Length;
    var rawSize = (payloadLength + 0x1FF) & ~0x1FF;

    var image = new byte[0x400 + rawSize];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
    "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xF0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x20B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xB8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xBC), 0x200);

    var section = 0x80 + 24 + 0xF0;
    ".huan\0\0\0"u8.CopyTo(image.AsSpan(section));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)payloadLength);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x40000040);

    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x400), original.Length);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x404), encrypted.Length);
    key.CopyTo(image.AsSpan(0x408));
    iv.CopyTo(image.AsSpan(0x418));
    encrypted.CopyTo(image.AsSpan(0x428));
    return image;
  }

  private static bool IsDebug() {
#if DEBUG
    return true;
#else
    return false;
#endif
  }

  private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

  private sealed class TempDirectory : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwb-cli-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(this.Path);

    public void Dispose() {
      try {
        Directory.Delete(this.Path, recursive: true);
      } catch {
        // Best-effort cleanup for process-level tests.
      }
    }
  }
}
