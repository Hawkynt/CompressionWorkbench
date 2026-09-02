namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter6;

/// <summary>
/// Represents a chapter 6 short.
/// </summary>
public class Chapter6Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         3, 9,11,11,13,14,19,17,17,19, 5, 4, 5, 8,10,10,
        13,16,18,19, 7, 4, 4, 5, 8, 9,12,14,17,19, 8, 6,
         5, 5, 7, 7,10,13,16,18,10, 8, 7, 6, 5, 5, 8,11,
        17,19,11, 9, 7, 7, 5, 4, 5, 8,17,19,13,11, 8, 7,
         7, 5, 5, 7,16,18,14,13, 8, 6, 6, 5, 5, 7,16,18,
        18,16,10, 8, 8, 7, 7, 9,16,18,18,18,12,10,10, 9,
         9,10,17,18,
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
