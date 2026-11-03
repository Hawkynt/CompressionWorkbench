// ACE 2.0 SOUND mode, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

namespace Compression.Core.Dictionary.Ace;

/// <summary>ACE 2.0 SOUND decoding: a per-channel adaptive predictor over Huffman-coded residuals.</summary>
internal sealed class AceSound {
  private const int RunLenCodes = 32;
  private const int TypeCode = 256 + RunLenCodes;
  private const int NumCodes = 256 + RunLenCodes + 1;
  private const int MaxCodeWidth = 10;
  private static readonly int[] NumChannels = [1, 2, 3, 3];
  private static readonly int[][] UseChannels = [[0, 0, 0, 0], [0, 1, 0, 1], [0, 1, 0, 2], [1, 0, 2, 0]];

  private static readonly int[] Quantizer = BuildQuantizer();

  private static int[] BuildQuantizer() {
    var q = new int[256];
    for (var i = 1; i < 129; ++i)
      q[i] = q[(256 - i) & 0xFF] = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)i);
    return q;
  }

  private static int SChar(int i) => (sbyte)(byte)i;
  private static int UChar(int i) => i & 0xFF;

  private sealed class SymbolReader(int numModels) {
    private readonly AceHuffmanTree[] _trees = new AceHuffmanTree[numModels];
    private int _symsToRead;

    public int ReadSymbol(AceBitStream bs, int model) {
      if (this._symsToRead == 0) {
        for (var i = 0; i < this._trees.Length; ++i)
          this._trees[i] = AceHuffman.ReadTree(bs, MaxCodeWidth, NumCodes);
        this._symsToRead = (int)bs.ReadBits(15);
      }
      --this._symsToRead;
      return this._trees[model].ReadSymbol(bs);
    }
  }

  private sealed class Channel(SymbolReader reader, int channelIndex) {
    private readonly int _modelBase = 3 * channelIndex;
    private readonly int[] _predDifCnt = new int[2];
    private readonly int[] _lastPredDifCnt = new int[2];
    private readonly int[] _rarDifCnt = new int[4];
    private readonly int[] _rarCoeff = new int[4];
    private readonly int[] _rarDif = new int[9];
    private int _byteCount;
    private int _lastSample;
    private int _lastDelta;
    private int _adaptModelCnt;
    private int _adaptModelUse;
    private int _getState;
    private int _getCode;

    private int GetSymbol(AceBitStream bs) {
      var model = this._getState << 1;
      if (model == 0)
        model += this._adaptModelUse;
      return reader.ReadSymbol(bs, model + this._modelBase);
    }

    /// <summary>The next residual, or null when a mode instruction (stored in <paramref name="mode"/>) ends the block.</summary>
    public int? Get(AceBitStream bs, out AceMode? mode) {
      mode = null;
      if (this._getState != 2) {
        this._getCode = this.GetSymbol(bs);
        if (this._getCode == TypeCode) {
          mode = AceMode.ReadFrom(bs);
          return null;
        }
      }
      var value = 0;
      if (this._getState == 0) {
        if (this._getCode >= RunLenCodes) {
          value = this._getCode - RunLenCodes;
          this._adaptModelCnt = (this._adaptModelCnt * 7 >> 3) + value;
          this._adaptModelUse = this._adaptModelCnt > 40 ? 1 : 0;
        } else
          this._getState = 2;
      } else if (this._getState == 1) {
        value = this._getCode;
        this._getState = 0;
      }
      if (this._getState == 2) {
        if (this._getCode == 0)
          this._getState = 1;
        else
          --this._getCode;
        value = 0;
      }
      return (value & 1) != 0 ? 255 - (value >> 1) : value >> 1;
    }

    public int RarPredict() => this._predDifCnt[0] > this._predDifCnt[1] ? this._lastSample : this.PredictedSample();

    public void RarAdjust(int sample) {
      ++this._byteCount;
      var predSample = this.PredictedSample();
      var predDif = SChar(predSample - sample) << 3;
      this._rarDif[0] += Math.Abs(predDif - this._rarDifCnt[0]);
      this._rarDif[1] += Math.Abs(predDif + this._rarDifCnt[0]);
      this._rarDif[2] += Math.Abs(predDif - this._rarDifCnt[1]);
      this._rarDif[3] += Math.Abs(predDif + this._rarDifCnt[1]);
      this._rarDif[4] += Math.Abs(predDif - this._rarDifCnt[2]);
      this._rarDif[5] += Math.Abs(predDif + this._rarDifCnt[2]);
      this._rarDif[6] += Math.Abs(predDif - this._rarDifCnt[3]);
      this._rarDif[7] += Math.Abs(predDif + this._rarDifCnt[3]);
      this._rarDif[8] += Math.Abs(predDif);

      this._lastDelta = SChar(sample - this._lastSample);
      this._predDifCnt[0] += Quantizer[(predDif >> 3) & 0xFF];
      this._predDifCnt[1] += Quantizer[(this._lastSample - sample) & 0xFF];
      this._lastSample = sample;

      if ((this._byteCount & 0x1F) == 0) {
        var minDif = 0xFFFF;
        var minDifPos = 0;
        for (var i = 8; i >= 0; --i) {
          if (this._rarDif[i] <= minDif) {
            minDif = this._rarDif[i];
            minDifPos = i;
          }
          this._rarDif[i] = 0;
        }
        if (minDifPos != 8) {
          var i = minDifPos >> 1;
          if ((minDifPos & 1) == 0) {
            if (this._rarCoeff[i] >= -16)
              --this._rarCoeff[i];
          } else if (this._rarCoeff[i] <= 16)
            ++this._rarCoeff[i];
        }
        if ((this._byteCount & 0xFF) == 0)
          for (var i = 0; i < 2; ++i) {
            this._predDifCnt[i] -= this._lastPredDifCnt[i];
            this._lastPredDifCnt[i] = this._predDifCnt[i];
          }
      }
      this._rarDifCnt[3] = this._rarDifCnt[2];
      this._rarDifCnt[2] = this._rarDifCnt[1];
      this._rarDifCnt[1] = this._lastDelta - this._rarDifCnt[0];
      this._rarDifCnt[0] = this._lastDelta;
    }

    private int PredictedSample()
      => UChar((8 * this._lastSample
               + this._rarCoeff[0] * this._rarDifCnt[0]
               + this._rarCoeff[1] * this._rarDifCnt[1]
               + this._rarCoeff[2] * this._rarDifCnt[2]
               + this._rarCoeff[3] * this._rarDifCnt[3]) >> 3);
  }

  private int _mode;
  private Channel[] _channels = [];

  public void Reinit(int mode) {
    this._mode = mode - AceConstants.ModeSound8;
    var numChannels = NumChannels[this._mode];
    var reader = new SymbolReader(numChannels * 3);
    this._channels = new Channel[numChannels];
    for (var i = 0; i < numChannels; ++i)
      this._channels[i] = new Channel(reader, i);
  }

  public (byte[] Chunk, AceMode? NextMode) Read(AceBitStream bs, int wantSize) {
    var chunk = new List<byte>();
    AceMode? nextMode = null;
    var count = wantSize & ~3;
    for (var i = 0; i < count; ++i) {
      var channel = this._channels[UseChannels[this._mode][i % 4]];
      var value = channel.Get(bs, out var mode);
      if (value == null) {
        nextMode = mode;
        break;
      }
      var sample = UChar(value.Value + channel.RarPredict());
      chunk.Add((byte)sample);
      channel.RarAdjust(SChar(sample));
    }
    return ([.. chunk], nextMode);
  }
}
