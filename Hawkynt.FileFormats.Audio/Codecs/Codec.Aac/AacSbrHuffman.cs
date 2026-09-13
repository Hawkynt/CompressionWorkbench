#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Bit-serial decoder for the ten SBR Huffman codebooks (ISO/IEC 14496-3
/// §4.A.6.1, Tables 4.A.76–4.A.85). The codebooks are canonical prefix codes;
/// the first length at which the accumulated bits equal a stored code is the
/// unique match. A codeword's positional index maps to a signed delta by
/// subtracting the table's value bias (LAV).
/// </summary>
internal sealed class AacSbrHuffman {

  private readonly byte[] _bits;
  private readonly uint[] _codes;
  private readonly int _bias;
  private readonly int _maxLen;

  private AacSbrHuffman(byte[] bits, uint[] codes, int bias) {
    this._bits = bits;
    this._codes = codes;
    this._bias = bias;
    var max = 0;
    foreach (var b in bits)
      if (b > max) max = b;
    this._maxLen = max;
  }

  /// <summary>Reads one codeword and returns the signed delta (index − bias).</summary>
  public int Decode(AacBitReader reader) {
    uint acc = 0;
    var len = 0;
    while (len < this._maxLen) {
      acc = (acc << 1) | reader.ReadBits(1);
      ++len;
      for (var i = 0; i < this._codes.Length; ++i)
        if (this._bits[i] == len && this._codes[i] == acc)
          return i - this._bias;
    }
    throw new InvalidDataException($"AAC SBR: no Huffman match after {len} bits.");
  }

  // ── The ten codebooks (built once) ──────────────────────────────────────────

  public static readonly AacSbrHuffman TEnv15 = new(AacSbrTables.TEnv15Bits, AacSbrTables.TEnv15Codes, AacSbrTables.TEnv15Bias);
  public static readonly AacSbrHuffman FEnv15 = new(AacSbrTables.FEnv15Bits, AacSbrTables.FEnv15Codes, AacSbrTables.FEnv15Bias);
  public static readonly AacSbrHuffman TEnvBal15 = new(AacSbrTables.TEnvBal15Bits, AacSbrTables.TEnvBal15Codes, AacSbrTables.TEnvBal15Bias);
  public static readonly AacSbrHuffman FEnvBal15 = new(AacSbrTables.FEnvBal15Bits, AacSbrTables.FEnvBal15Codes, AacSbrTables.FEnvBal15Bias);
  public static readonly AacSbrHuffman TEnv30 = new(AacSbrTables.TEnv30Bits, AacSbrTables.TEnv30Codes, AacSbrTables.TEnv30Bias);
  public static readonly AacSbrHuffman FEnv30 = new(AacSbrTables.FEnv30Bits, AacSbrTables.FEnv30Codes, AacSbrTables.FEnv30Bias);
  public static readonly AacSbrHuffman TEnvBal30 = new(AacSbrTables.TEnvBal30Bits, AacSbrTables.TEnvBal30Codes, AacSbrTables.TEnvBal30Bias);
  public static readonly AacSbrHuffman FEnvBal30 = new(AacSbrTables.FEnvBal30Bits, AacSbrTables.FEnvBal30Codes, AacSbrTables.FEnvBal30Bias);
  public static readonly AacSbrHuffman TNoise30 = new(AacSbrTables.TNoise30Bits, AacSbrTables.TNoise30Codes, AacSbrTables.TNoise30Bias);
  public static readonly AacSbrHuffman TNoiseBal30 = new(AacSbrTables.TNoiseBal30Bits, AacSbrTables.TNoiseBal30Codes, AacSbrTables.TNoiseBal30Bias);
}
