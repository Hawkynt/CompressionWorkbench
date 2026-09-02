namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a residue template.
/// </summary>
public class ResidueTemplate : IResidueTemplate
{
    /// <summary>
    /// Initializes a new instance of <see cref="ResidueTemplate"/>.
    /// </summary>
public ResidueTemplate(
        ResidueType residueType,
        ResidueLimitType limitType,
        int grouping,
        ResidueEntry residue,
        IStaticCodeBook bookAux,
        IStaticCodeBook booxAuxManaged,
        IStaticBookBlock booksBase,
        IStaticBookBlock booksBaseManaged)
    {
        ResidueType = residueType;
        LimitType = limitType;
        Residue = residue;
        BookAux = bookAux;
        BookAuxManaged = booxAuxManaged;
        BooksBase = booksBase;
        BooksBaseManaged = booksBaseManaged;
        Grouping = grouping;
    }

    /// <summary>
    /// Gets the residue type.
    /// </summary>
public ResidueType ResidueType { get; }
    /// <summary>
    /// Gets the limit type.
    /// </summary>
public ResidueLimitType LimitType { get; }
    /// <summary>
    /// Gets the grouping.
    /// </summary>
public int Grouping { get; }
    /// <summary>
    /// Gets the residue.
    /// </summary>
public ResidueEntry Residue { get; }
    /// <summary>
    /// Gets the book aux.
    /// </summary>
public IStaticCodeBook BookAux { get; }
    /// <summary>
    /// Gets the book aux managed.
    /// </summary>
public IStaticCodeBook BookAuxManaged { get; }
    /// <summary>
    /// Gets the books base.
    /// </summary>
public IStaticBookBlock BooksBase { get; }
    /// <summary>
    /// Gets the books base managed.
    /// </summary>
public IStaticBookBlock BooksBaseManaged { get; }
}
