#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Represents a py pe packer executable packer handler.
/// </summary>
public sealed class PyPePackerExecutablePackerHandler : IExecutablePackerHandler {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "pypepacker";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PyPePacker Python PE wrapper";

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsPe;

    /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (!PackerScanner.IsPe(image))
      return new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PyPePacker: not a valid PE wrapper.", true)]);

    return TryRecover(image, out _, out _)
      ? new(true, this.Id, 0.96, [])
      : new(false, this.Id, 0, [new(ExecutableDiagnosticCode.NotPackedExecutable, "PyPePacker zipapp payload was not found or could not be decoded.", true)]);
  }

    /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var imageBytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(
      this.Id,
      imageBytes,
      detection,
      info,
      this.Capabilities,
      new Dictionary<string, string> {
        ["packer"] = "PyPePacker",
        ["container"] = info.Container.ToString(),
        ["architecture"] = info.Architecture.ToString(),
      });
  }

    /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", packed.OriginalImage, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();

    if (!TryRecover(packed.OriginalImage, out var script, out var decoded)) {
      diagnostics.Add(new(ExecutableDiagnosticCode.DecompressionFailed,
        "PyPePacker payload could not be decoded through EntropyEncoding v2, RC6-CBC and gzip.", true));
      var failed = new UnpackResult(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, failed), "stored"));
      return failed with { Artifacts = artifacts };
    }

    artifacts.Add(new("compressed_payload.py", script, "zipapp-python"));
    artifacts.Add(new("decompressed_payload.bin", decoded, "entropy-rc6-gzip"));
    artifacts.Add(new("reconstructed/reconstructed.exe", decoded, "stored"));
    diagnostics.Add(new(ExecutableDiagnosticCode.RunnableRebuildNotGuaranteed,
      "PyPePacker embeds the original PE in a Python zipapp. The reconstructed executable is the decoded PE bytes; no wrapper code was executed."));

    var innerInfo = ExecutableContainerParsers.ParseBestEffort(decoded);
    var caps = this.Capabilities;
    caps |= innerInfo.Architecture switch {
      CpuArchitecture.X86 => ExecutableUnpackCapabilities.SupportsX86,
      CpuArchitecture.X64 => ExecutableUnpackCapabilities.SupportsX64,
      _ => ExecutableUnpackCapabilities.None,
    };

    var result = new UnpackResult(ExecutableUnpackLevel.RebuiltExecutable, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, innerInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  internal static bool TryRecover(ReadOnlySpan<byte> image, out byte[] scriptBytes, out byte[] decodedPe) {
    scriptBytes = [];
    decodedPe = [];
    try {
      if (!TryReadZipAppMain(image, out scriptBytes))
        return false;

      var source = Encoding.UTF8.GetString(scriptBytes);
      if (!TryExtractLiterals(source, out var key, out var encodedPayload, out var iv))
        return false;

      var encrypted = EntropyDecode2(encodedPayload);
      var gzip = Rc6CbcDecrypt(encrypted, key, iv);
      using var gzipStream = new GZipStream(new MemoryStream(gzip, writable: false), CompressionMode.Decompress);
      using var decoded = new MemoryStream();
      gzipStream.CopyTo(decoded);
      decodedPe = decoded.ToArray();
      return PackerScanner.IsPe(decodedPe);
    } catch (InvalidDataException) {
      return false;
    } catch (IOException) {
      return false;
    } catch (ArgumentException) {
      return false;
    } catch (IndexOutOfRangeException) {
      return false;
    }
  }

  private static bool TryReadZipAppMain(ReadOnlySpan<byte> image, out byte[] scriptBytes) {
    scriptBytes = [];
    for (var i = 0; i <= image.Length - 4; i++) {
      if (image[i] != 0x50 ||
          image[i + 1] != 0x4B ||
          image[i + 2] != 0x03 ||
          image[i + 3] != 0x04)
        continue;

      try {
        using var archive = new ZipArchive(new MemoryStream(image[i..].ToArray(), writable: false), ZipArchiveMode.Read);
        var entry = archive.GetEntry("__main__.py");
        if (entry == null)
          continue;

        using var entryStream = entry.Open();
        using var script = new MemoryStream();
        entryStream.CopyTo(script);
        scriptBytes = script.ToArray();
        return scriptBytes.Length > 0;
      } catch (InvalidDataException) {
      } catch (ArgumentException) {
      }
    }

    return false;
  }

  private static bool TryExtractLiterals(string source, out byte[] key, out byte[] payload, out byte[] iv) {
    key = payload = iv = [];
    var keyCall = source.IndexOf("RC6Encryption(", StringComparison.Ordinal);
    if (keyCall < 0 || !TryReadPythonBytesLiteral(source, keyCall + "RC6Encryption(".Length, out key, out _))
      return false;

    var payloadCall = source.IndexOf("entropy_decode2(bytearray(", StringComparison.Ordinal);
    if (payloadCall < 0 ||
        !TryReadPythonBytesLiteral(source, payloadCall + "entropy_decode2(bytearray(".Length, out payload, out var payloadEnd))
      return false;

    var comma = source.IndexOf(',', payloadEnd);
    if (comma < 0 || !TryReadPythonBytesLiteral(source, comma + 1, out iv, out _))
      return false;
    return key.Length > 0 && payload.Length > 0 && iv.Length == 16;
  }

  private static bool TryReadPythonBytesLiteral(string source, int start, out byte[] bytes, out int end) {
    bytes = [];
    end = start;
    while (end < source.Length && char.IsWhiteSpace(source[end])) end++;
    if (end >= source.Length || source[end] != 'b')
      return false;
    end++;
    if (end >= source.Length || (source[end] != '\'' && source[end] != '"'))
      return false;
    var quote = source[end++];
    var result = new List<byte>();
    while (end < source.Length) {
      var c = source[end++];
      if (c == quote) {
        bytes = result.ToArray();
        return true;
      }
      if (c != '\\') {
        result.Add((byte)c);
        continue;
      }
      if (end >= source.Length)
        return false;
      var esc = source[end++];
      result.Add(esc switch {
        '\\' => (byte)'\\',
        '\'' => (byte)'\'',
        '"' => (byte)'"',
        'n' => (byte)'\n',
        'r' => (byte)'\r',
        't' => (byte)'\t',
        'a' => 0x07,
        'b' => 0x08,
        'f' => 0x0C,
        'v' => 0x0B,
        'x' when end + 1 < source.Length => ParseHexByte(source[end++], source[end++]),
        _ when esc is >= '0' and <= '7' => ParseOctalByte(esc, source, ref end),
        _ => (byte)esc,
      });
    }
    return false;
  }

  private static byte ParseHexByte(char high, char low) =>
    (byte)((HexValue(high) << 4) | HexValue(low));

  private static int HexValue(char c) => c switch {
    >= '0' and <= '9' => c - '0',
    >= 'a' and <= 'f' => c - 'a' + 10,
    >= 'A' and <= 'F' => c - 'A' + 10,
    _ => throw new ArgumentException("Invalid hex escape.")
  };

  private static byte ParseOctalByte(char first, string source, ref int end) {
    var value = first - '0';
    for (var i = 0; i < 2 && end < source.Length && source[end] is >= '0' and <= '7'; i++)
      value = (value << 3) | (source[end++] - '0');
    return (byte)value;
  }

  private static byte[] EntropyDecode2(byte[] data) {
    var map = new Dictionary<byte, byte>();
    var offset = 0;
    while (map.Count < 70) {
      var baseChar = data[offset++];
      var first = data[offset++];
      var second = data[offset++];
      while (first == second)
        second = data[offset++];
      map[first] = baseChar;
      map[second] = baseChar;
      while (offset < data.Length && data[offset] == second)
        offset++;
    }

    var base32 = new byte[data.Length - offset];
    for (var i = 0; i < base32.Length; i++)
      base32[i] = map[data[offset + i]];
    return DecodeBase32(base32);
  }

  private static byte[] DecodeBase32(byte[] encoded) {
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    var output = new List<byte>();
    var buffer = 0;
    var bits = 0;
    foreach (var b in encoded) {
      if (b == (byte)'=')
        break;
      var value = alphabet.IndexOf((char)b, StringComparison.Ordinal);
      if (value < 0)
        throw new ArgumentException("Invalid Base32 character.");
      buffer = (buffer << 5) | value;
      bits += 5;
      if (bits < 8)
        continue;
      bits -= 8;
      output.Add((byte)((buffer >> bits) & 0xFF));
    }
    return output.ToArray();
  }

  private static byte[] Rc6CbcDecrypt(byte[] encrypted, byte[] key, byte[] iv) {
    if (encrypted.Length == 0 || encrypted.Length % 16 != 0 || iv.Length != 16)
      throw new InvalidDataException("Invalid RC6-CBC data length.");
    var rc6 = new Rc6(key);
    var previous = ToWords(iv);
    var plain = new byte[encrypted.Length];
    for (var offset = 0; offset < encrypted.Length; offset += 16) {
      var block = ToWords(encrypted.AsSpan(offset, 16));
      var decrypted = rc6.Decrypt(block);
      for (var i = 0; i < 4; i++)
        decrypted[i] ^= previous[i];
      WriteWords(decrypted, plain.AsSpan(offset, 16));
      previous = block;
    }
    var padding = plain[^1];
    if (padding == 0 || padding > 16 || padding > plain.Length)
      throw new InvalidDataException("Invalid RC6-CBC PKCS padding.");
    for (var i = plain.Length - padding; i < plain.Length; i++) {
      if (plain[i] != padding)
        throw new InvalidDataException("Invalid RC6-CBC PKCS padding.");
    }
    return plain[..^padding];
  }

  private static uint[] ToWords(ReadOnlySpan<byte> data) => [
    BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]),
    BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
    BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]),
    BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]),
  ];

  private static void WriteWords(uint[] words, Span<byte> output) {
    BinaryPrimitives.WriteUInt32LittleEndian(output[0..4], words[0]);
    BinaryPrimitives.WriteUInt32LittleEndian(output[4..8], words[1]);
    BinaryPrimitives.WriteUInt32LittleEndian(output[8..12], words[2]);
    BinaryPrimitives.WriteUInt32LittleEndian(output[12..16], words[3]);
  }

  private sealed class Rc6 {
    private const int Rounds = 20;
    private const uint P32 = 0xB7E15163;
    private const uint Q32 = 0x9E3779B9;
    private readonly uint[] _s = new uint[2 * Rounds + 4];

    public Rc6(byte[] key) {
      var c = Math.Max(1, (key.Length + 3) / 4);
      var l = new uint[c];
      for (var i = 0; i < key.Length; i++)
        l[i / 4] |= (uint)key[i] << (8 * (i % 4));

      this._s[0] = P32;
      for (var i = 1; i < this._s.Length; i++)
        this._s[i] = unchecked(this._s[i - 1] + Q32);

      uint a = 0, b = 0;
      var ii = 0;
      var jj = 0;
      for (var k = 0; k < 3 * Math.Max(c, this._s.Length); k++) {
        a = this._s[ii] = RotateLeft(unchecked(this._s[ii] + a + b), 3);
        b = l[jj] = RotateLeft(unchecked(l[jj] + a + b), (int)((a + b) & 31));
        ii = (ii + 1) % this._s.Length;
        jj = (jj + 1) % c;
      }
    }

    public uint[] Decrypt(uint[] block) {
      var a = block[0];
      var b = block[1];
      var c = block[2];
      var d = block[3];
      c = unchecked(c - this._s[2 * Rounds + 3]);
      a = unchecked(a - this._s[2 * Rounds + 2]);
      for (var i = Rounds; i >= 1; i--) {
        (a, b, c, d) = (d, a, b, c);
        var u = RotateLeft(unchecked(d * (2 * d + 1)), 5);
        var t = RotateLeft(unchecked(b * (2 * b + 1)), 5);
        c = unchecked(RotateRight(c - this._s[2 * i + 1], (int)(t & 31)) ^ u);
        a = unchecked(RotateRight(a - this._s[2 * i], (int)(u & 31)) ^ t);
      }
      d = unchecked(d - this._s[1]);
      b = unchecked(b - this._s[0]);
      return [a, b, c, d];
    }

    private static uint RotateLeft(uint value, int bits) =>
      (value << bits) | (value >> (32 - bits));

    private static uint RotateRight(uint value, int bits) =>
      (value >> bits) | (value << (32 - bits));
  }

  private static byte[] BuildMetadataJson(PackedExecutable packed) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    sb.Append("  \"packer\": \"pypepacker\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"container\": \"{(packed.ImageInfo?.Container.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"architecture\": \"{(packed.ImageInfo?.Architecture.ToString() ?? "unknown").ToLowerInvariant()}\",\n");
    sb.Append("  \"transform\": \"zipapp-entropyencoding2-rc6-cbc-gzip\",\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"imageSize\": {packed.OriginalImage.LongLength}\n");
    sb.Append("}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
