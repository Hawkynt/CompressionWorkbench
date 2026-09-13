namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter1;

/// <summary>
/// Represents a managed chapter 1 short.
/// </summary>
public class ManagedChapter1Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         4, 7,13,14,14,15,16,18,18, 4, 2, 5, 8, 7, 9,12,
        15,15,10, 4, 5,10, 6, 8,11,15,17,12, 5, 7, 5, 6,
         8,11,14,17,11, 5, 6, 6, 5, 6, 9,13,17,12, 6, 7,
         6, 5, 6, 8,12,14,14, 7, 8, 6, 6, 7, 9,11,14,14,
         8, 9, 6, 5, 6, 9,11,13,16,10,10, 7, 6, 7, 8,10,
        11,
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
