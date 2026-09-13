namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter0;

/// <summary>
/// Represents a chapter 0 long.
/// </summary>
public class Chapter0Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         5, 8,13,10,17,11,11,15, 7, 2, 4, 5, 8, 7, 9,16,
        13, 4, 3, 5, 6, 8,11,20,10, 4, 5, 5, 7, 6, 8,18,
        15, 7, 6, 7, 8,10,14,20,10, 6, 7, 6, 9, 7, 8,17,
         9, 8,10, 8,10, 5, 4,11,12,17,19,14,16,10, 7,12,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
