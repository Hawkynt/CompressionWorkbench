namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter9;

/// <summary>
/// Represents a page 5 1.
/// </summary>
public class Page5_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         4, 5, 5, 6, 6, 7, 7, 7, 7, 7, 7,11, 5, 5, 6, 6,
         7, 7, 7, 7, 8, 8,11, 5, 5, 6, 6, 7, 7, 7, 7, 8,
         8,11, 5, 5, 6, 6, 7, 7, 8, 8, 8, 8,11,11,11, 6,
         6, 7, 7, 7, 8, 8, 8,11,11,11, 6, 6, 7, 7, 7, 8,
         8, 8,11,11,11, 6, 6, 7, 7, 7, 7, 8, 8,11,11,11,
         7, 7, 7, 7, 7, 7, 8, 8,11,11,11,10,10, 7, 7, 7,
         7, 8, 8,11,11,11,11,11, 7, 7, 7, 7, 7, 7,11,11,
        11,11,11, 7, 7, 7, 7, 7, 7,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -531365888;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1611661312;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        5,
        4,
        6,
        3,
        7,
        2,
        8,
        1,
        9,
        0,
        10,
    };
}
