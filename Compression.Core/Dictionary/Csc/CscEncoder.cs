namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Drives the libcsc encoder: each raw chunk (2 MiB) is analyzed in 8 KiB steps, runs of equally
/// classified data become one typed block, and the chunk ends a segment so both entropy coders
/// restart. An end-of-stream block in a segment of its own closes the stream.
/// </summary>
internal sealed class CscEncoder {
  private readonly CscEncoderSettings _settings;
  private readonly CscEntropyEncoder _coder;
  private readonly CscModel _model;
  private readonly CscLzEncoder _lz;

  public CscEncoder(Stream output, in CscEncoderSettings settings) {
    this._settings = settings;
    this._coder = new(new(output, settings.CscBlockSize));
    this._model = new(this._coder);
    this._lz = new(settings, this._model);
  }

  /// <summary>
  /// Encodes <paramref name="input"/> to the end. <paramref name="prefix"/> holds bytes the caller
  /// already read from it (to learn the input length); they are encoded first.
  /// </summary>
  public void Encode(Stream input, ReadOnlySpan<byte> prefix) {
    // One buffer for the whole stream, like libcsc's: the analyzer's hashes read a few bytes past the
    // current chunk, and those must be the same stale bytes libcsc would see.
    var chunkSize = (int)this._settings.RawBlockSize;
    var buffer = new byte[chunkSize + 8];
    var pending = prefix;
    for (;;) {
      var size = Math.Min(pending.Length, chunkSize);
      pending[..size].CopyTo(buffer);
      pending = pending[size..];
      if (size < chunkSize)
        size += input.ReadAtLeast(buffer.AsSpan(size, chunkSize - size), chunkSize - size, throwOnEndOfStream: false);
      if (size == 0)
        break;

      this.CompressChunk(buffer, size);
    }

    this._model.EncodeInt(CscConstants.TypeEndOfStream);
    this._coder.Flush();
  }

  private void CompressChunk(byte[] buffer, int size) {
    var settings = this._settings;
    var lastType = CscConstants.TypeNormal;
    int lastBegin = 0, lastSize = 0;
    uint bitsPerByte = 0;

    for (var i = 0; i < size;) {
      var blockSize = Math.Min(CscConstants.MinBlockSize, size - i);
      var block = buffer.AsSpan(i, blockSize);
      var type = settings.UseFilters ? CscAnalyzer.Analyze(block, ref bitsPerByte) : CscConstants.TypeNormal;

      if (type == CscConstants.TypeSkip)
        type = lastType;

      if (type == CscConstants.TypeExecutable && !settings.ExecutableFilter
          || type == CscConstants.TypeEnglishText && !settings.TextFilter
          || type >= CscConstants.TypeDelta && !settings.DeltaFilter)
        type = CscConstants.TypeNormal;

      // A delta that does not cut the order-0 entropy by 5 % is not worth leaving the LZ path.
      if (type >= CscConstants.TypeDelta
          && CscAnalyzer.DeltaBitsPerByte(block, CscConstants.DeltaChannels[(int)(type - CscConstants.TypeDelta)]) >= bitsPerByte * 0.95)
        type = CscConstants.TypeNormal;

      // Data the LZ stage would skip still goes through it when it repeats something in the window.
      if (type >= CscConstants.TypeNoLz && this._lz.IsDuplicateBlock(buffer, i, blockSize))
        type = CscConstants.TypeNormal;

      if (lastType != type || lastSize + blockSize > settings.RawBlockSize) {
        if (lastSize != 0) {
          this.CompressBlock(buffer.AsSpan(lastBegin, lastSize), lastType);
          this._model.EncodeInt(0);
        }

        lastBegin = i;
        lastSize = 0;
      }

      lastType = type;
      lastSize += blockSize;
      i += blockSize;
    }

    if (lastSize == 0)
      return;

    this.CompressBlock(buffer.AsSpan(lastBegin, lastSize), lastType);
    this._model.EncodeInt(1);
    this._coder.Flush();
  }

  private void CompressBlock(Span<byte> block, uint type) {
    var lzMode = this._settings.LzMode;
    switch (type) {
      case CscConstants.TypeNormal:
      case CscConstants.TypeFast:
        // libcsc disabled its fast mode; such blocks are coded as normal ones.
        this._model.EncodeInt(CscConstants.TypeNormal);
        this._lz.EncodeNormal(block, lzMode);
        break;
      case CscConstants.TypeExecutable:
        this._model.EncodeInt(type);
        CscFilters.ForwardE89(block);
        this._lz.EncodeNormal(block, lzMode);
        break;
      case CscConstants.TypeEnglishText:
        if (CscFilters.ForwardDictionary(block)) {
          this._model.EncodeInt(type);
          this._model.EncodeInt((uint)block.Length);
        } else
          this._model.EncodeInt(CscConstants.TypeNormal);
        this._lz.EncodeNormal(block, lzMode);
        break;
      case CscConstants.TypeBad:
        this._model.EncodeInt(type);
        this._lz.EncodeNormal(block, 5);
        this._model.CompressBad(block);
        break;
      case CscConstants.TypeEntropy:
        this._model.EncodeInt(type);
        this._lz.EncodeNormal(block, 5);
        this._model.CompressLiterals(block);
        break;
      default:
        // Delta: the raw bytes enter the window, the filtered bytes go through the RLE coder.
        this._model.EncodeInt(type);
        this._lz.EncodeNormal(block, 5);
        CscFilters.ForwardDelta(block, CscConstants.DeltaChannels[(int)(type - CscConstants.TypeDelta)]);
        this._model.CompressRle(block);
        break;
    }
  }
}
