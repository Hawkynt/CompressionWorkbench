namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter4;

/// <summary>
/// Represents a chapter 4 long.
/// </summary>
public class Chapter4Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         4, 7,11,11,11,11,10,11,12,11, 5, 2,11, 5, 6, 6,
         7, 9,11,12,11, 9, 6,10, 6, 7, 8, 9,10,11,11, 5,
        11, 7, 8, 8, 9,11,13,14,11, 6, 5, 8, 4, 5, 7, 8,
        10,11,10, 6, 7, 7, 5, 5, 6, 8, 9,11,10, 7, 8, 9,
         6, 6, 6, 7, 8, 9,11, 9, 9,11, 7, 7, 6, 6, 7, 9,
        12,12,10,13, 9, 8, 7, 7, 7, 8,11,13,11,14,11,10,
         9, 8, 7, 7,
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
