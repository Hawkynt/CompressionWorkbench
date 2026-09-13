#pragma warning disable CS1591
namespace Codec.SmackerAudio;

/// <summary>
/// One per-frame Smacker Huffman tree, reproducing FFmpeg's <c>smacker_decode_tree</c>
/// followed by <c>ff_vlc_init_from_lengths(..., VLC_INIT_OUTPUT_LE)</c> (smacker.c).
///
/// <para>The tree is read recursively from the LSB-first bitstream: a <c>0</c> bit marks a
/// leaf, after which an 8-bit symbol is read and recorded with the current depth as its
/// code length; a <c>1</c> bit marks a node, whose two children follow. The depth-first
/// leaf order yields a (symbol, length) list.</para>
///
/// <para>Codes are then assigned canonically exactly as <c>ff_vlc_init_from_lengths</c>
/// does — in leaf order, the first code is 0 and each subsequent code adds
/// <c>1 &lt;&lt; (32 − len)</c> — giving an MSB-justified 32-bit canonical code per symbol.
/// Because the VLC is built with <c>VLC_INIT_OUTPUT_LE</c> and decoded from an LSB-first
/// reader, decoding reads <c>len</c> bits LSB-first and the value to match is the bit
/// reversal of the top <c>len</c> bits of the canonical code (i.e. <c>bitswap32(code)</c>
/// truncated to <c>len</c> bits). A degenerate single-leaf tree carries no codes and always
/// returns that leaf's value without consuming bits.</para>
/// </summary>
internal sealed class SmackerHuffman {

  private readonly int[] _lengths;
  private readonly int[] _symbols;
  private readonly uint[] _leMatch; // bit-reversed canonical code, right-aligned to its length
  private readonly bool _single;
  private readonly int _singleValue;

  private const int MaxDepth = 27; // FFMIN(32, 3 * SMKTREE_BITS), SMKTREE_BITS == 9

  private SmackerHuffman(List<(int Value, int Length)> leaves) {
    if (leaves.Count <= 1) {
      this._single = true;
      this._singleValue = leaves.Count == 1 ? leaves[0].Value : 0;
      this._lengths = [];
      this._symbols = [];
      this._leMatch = [];
      return;
    }

    var n = leaves.Count;
    this._lengths = new int[n];
    this._symbols = new int[n];
    this._leMatch = new uint[n];

    uint code = 0;
    for (var i = 0; i < n; ++i) {
      var len = leaves[i].Length;
      this._lengths[i] = len;
      this._symbols[i] = leaves[i].Value;
      this._leMatch[i] = BitSwap32(code) & ((len >= 32 ? 0xFFFFFFFFu : (1u << len) - 1));
      code += len >= 32 ? 0u : 1u << (32 - len);
    }
  }

  /// <summary>True if the tree degenerated to (at most) a single leaf — no bits are read to decode.</summary>
  public bool IsSingle => this._single;

  /// <summary>The value of a single-leaf tree (<see cref="IsSingle"/>).</summary>
  public int SingleValue => this._singleValue;

  /// <summary>
  /// Reads the tree definition from <paramref name="reader"/> (the leading marker bit and
  /// trailing marker bit handled by the caller, per smacker.c <c>skip_bits1</c>) and builds
  /// the VLC. Returns <see langword="null"/> if the tree is malformed (over-deep recursion,
  /// or more than 256 leaves).
  /// </summary>
  public static SmackerHuffman? Build(SmackerBitReader reader) {
    var leaves = new List<(int Value, int Length)>();
    if (!DecodeTree(reader, leaves, 0))
      return null;
    return new SmackerHuffman(leaves);
  }

  private static bool DecodeTree(SmackerBitReader reader, List<(int, int)> leaves, int length) {
    if (length > MaxDepth)
      return false;
    if (reader.GetBit() == 0) { // leaf
      if (leaves.Count >= 256)
        return false;
      if (reader.BitsLeft < 8)
        return false;
      var value = (int)reader.GetBits(8);
      leaves.Add((value, length));
      return true;
    }
    // node
    return DecodeTree(reader, leaves, length + 1) && DecodeTree(reader, leaves, length + 1);
  }

  /// <summary>
  /// Decodes one symbol LSB-first (<c>get_vlc2</c>). Accumulates bits with the first bit in
  /// the least-significant position and matches against the right-aligned bit-reversed
  /// canonical codes. A single-leaf tree returns its value immediately.
  /// </summary>
  public int Decode(SmackerBitReader reader) {
    if (this._single)
      return this._singleValue;

    uint acc = 0;
    var bits = 0;
    while (bits < 32) {
      acc |= (uint)reader.GetBit() << bits;
      ++bits;
      for (var i = 0; i < this._lengths.Length; ++i)
        if (this._lengths[i] == bits && this._leMatch[i] == acc)
          return this._symbols[i];
    }
    return this._symbols.Length > 0 ? this._symbols[0] : 0;
  }

  private static uint BitSwap32(uint v) {
    v = ((v & 0x55555555u) << 1) | ((v >> 1) & 0x55555555u);
    v = ((v & 0x33333333u) << 2) | ((v >> 2) & 0x33333333u);
    v = ((v & 0x0F0F0F0Fu) << 4) | ((v >> 4) & 0x0F0F0F0Fu);
    v = ((v & 0x00FF00FFu) << 8) | ((v >> 8) & 0x00FF00FFu);
    return (v << 16) | (v >> 16);
  }
}
