// Ported from marked 18.0.5; see LICENSE in this directory.
// Members mirror marked's rule names in rules.ts, so they carry no separate documentation (CS1591). They are
// instance members on purpose, to keep the shape of marked's this.rules object that tokenizer subclasses use (CA1822).
#pragma warning disable CS1591, CA1822
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// marked's rules on the GFM option path, as a tokenizer sees them through <c>this.rules</c>. Every pattern is a
/// source-generated translation (tools/marked-oracle/generate-regexes.mjs), built once and safe to share.
/// </summary>
public sealed class MarkedRules
{
    private MarkedRules() { }

    /// <summary>The single rule set.</summary>
    public static MarkedRules Instance { get; } = new();

    /// <summary><c>rules.block.gfm</c>.</summary>
    public MarkedBlockRules Block { get; } = new();

    /// <summary><c>rules.inline.gfm</c>.</summary>
    public MarkedInlineRules Inline { get; } = new();

    /// <summary><c>rules.other</c>.</summary>
    public MarkedOtherRules Other { get; } = new();
}

/// <summary>marked's GFM block rules.</summary>
public sealed class MarkedBlockRules
{
    internal MarkedBlockRules() { }

    public Regex Blockquote => MarkedRegexes.BlockBlockquote();
    public Regex Code => MarkedRegexes.BlockCode();
    public Regex Def => MarkedRegexes.BlockDef();
    public Regex Fences => MarkedRegexes.BlockFences();
    public Regex Heading => MarkedRegexes.BlockHeading();
    public Regex Hr => MarkedRegexes.BlockHr();
    public Regex Html => MarkedRegexes.BlockHtml();
    public Regex Lheading => MarkedRegexes.BlockLheading();
    public Regex List => MarkedRegexes.BlockList();
    public Regex Newline => MarkedRegexes.BlockNewline();
    public Regex Paragraph => MarkedRegexes.BlockParagraph();
    public Regex Table => MarkedRegexes.BlockTable();
    public Regex Text => MarkedRegexes.BlockText();
}

/// <summary>marked's GFM inline rules.</summary>
public sealed class MarkedInlineRules
{
    internal MarkedInlineRules() { }

    public Regex Backpedal => MarkedRegexes.InlineBackpedal();
    public Regex AnyPunctuation => MarkedRegexes.InlineAnyPunctuation();
    public Regex Autolink => MarkedRegexes.InlineAutolink();
    public Regex BlockSkip => MarkedRegexes.InlineBlockSkip();
    public Regex Br => MarkedRegexes.InlineBr();
    public Regex Code => MarkedRegexes.InlineCode();
    public Regex Del => MarkedRegexes.InlineDel();
    public Regex DelLDelim => MarkedRegexes.InlineDelLDelim();
    public Regex DelRDelim => MarkedRegexes.InlineDelRDelim();
    public Regex EmStrongLDelim => MarkedRegexes.InlineEmStrongLDelim();
    public Regex EmStrongRDelimAst => MarkedRegexes.InlineEmStrongRDelimAst();
    public Regex EmStrongRDelimUnd => MarkedRegexes.InlineEmStrongRDelimUnd();
    public Regex Escape => MarkedRegexes.InlineEscape();
    public Regex Link => MarkedRegexes.InlineLink();
    public Regex Nolink => MarkedRegexes.InlineNolink();
    public Regex Punctuation => MarkedRegexes.InlinePunctuation();
    public Regex Reflink => MarkedRegexes.InlineReflink();
    public Regex ReflinkSearch => MarkedRegexes.InlineReflinkSearch();
    public Regex Tag => MarkedRegexes.InlineTag();
    public Regex Text => MarkedRegexes.InlineText();
    public Regex Url => MarkedRegexes.InlineUrl();

    // Not marked rules: sticky patterns that tell which capture group of a delimiter scan matched, without reading
    // captures (tools/marked-oracle/generate-regexes.mjs).
    internal Regex DelRDelimGroups12 => MarkedRegexes.InlineDelRDelimGroups12();
    internal Regex DelRDelimGroups34 => MarkedRegexes.InlineDelRDelimGroups34();
    internal Regex EmStrongRDelimAstGroups12 => MarkedRegexes.InlineEmStrongRDelimAstGroups12();
    internal Regex EmStrongRDelimAstGroups34 => MarkedRegexes.InlineEmStrongRDelimAstGroups34();
    internal Regex EmStrongRDelimUndGroups12 => MarkedRegexes.InlineEmStrongRDelimUndGroups12();
    internal Regex EmStrongRDelimUndGroups34 => MarkedRegexes.InlineEmStrongRDelimUndGroups34();
}

/// <summary>marked's helper patterns, including the parameterised ones.</summary>
public sealed class MarkedOtherRules
{
    internal MarkedOtherRules() { }

    public Regex CodeRemoveIndent => MarkedRegexes.OtherCodeRemoveIndent();
    public Regex OutputLinkReplace => MarkedRegexes.OtherOutputLinkReplace();
    public Regex IndentCodeCompensation => MarkedRegexes.OtherIndentCodeCompensation();
    public Regex BeginningSpace => MarkedRegexes.OtherBeginningSpace();
    public Regex EndingHash => MarkedRegexes.OtherEndingHash();
    public Regex StartingSpaceChar => MarkedRegexes.OtherStartingSpaceChar();
    public Regex EndingSpaceChar => MarkedRegexes.OtherEndingSpaceChar();
    public Regex NonSpaceChar => MarkedRegexes.OtherNonSpaceChar();
    public Regex NewLineCharGlobal => MarkedRegexes.OtherNewLineCharGlobal();
    public Regex TabCharGlobal => MarkedRegexes.OtherTabCharGlobal();
    public Regex MultipleSpaceGlobal => MarkedRegexes.OtherMultipleSpaceGlobal();
    public Regex BlankLine => MarkedRegexes.OtherBlankLine();
    public Regex DoubleBlankLine => MarkedRegexes.OtherDoubleBlankLine();
    public Regex BlockquoteStart => MarkedRegexes.OtherBlockquoteStart();
    public Regex BlockquoteSetextReplace => MarkedRegexes.OtherBlockquoteSetextReplace();
    public Regex BlockquoteSetextReplace2 => MarkedRegexes.OtherBlockquoteSetextReplace2();
    public Regex ListIsTask => MarkedRegexes.OtherListIsTask();
    public Regex ListReplaceTask => MarkedRegexes.OtherListReplaceTask();
    public Regex ListTaskCheckbox => MarkedRegexes.OtherListTaskCheckbox();
    public Regex AnyLine => MarkedRegexes.OtherAnyLine();
    public Regex HrefBrackets => MarkedRegexes.OtherHrefBrackets();
    public Regex TableDelimiter => MarkedRegexes.OtherTableDelimiter();
    public Regex TableAlignChars => MarkedRegexes.OtherTableAlignChars();
    public Regex TableRowBlankLine => MarkedRegexes.OtherTableRowBlankLine();
    public Regex TableAlignRight => MarkedRegexes.OtherTableAlignRight();
    public Regex TableAlignCenter => MarkedRegexes.OtherTableAlignCenter();
    public Regex TableAlignLeft => MarkedRegexes.OtherTableAlignLeft();
    public Regex StartATag => MarkedRegexes.OtherStartATag();
    public Regex EndATag => MarkedRegexes.OtherEndATag();
    public Regex StartPreScriptTag => MarkedRegexes.OtherStartPreScriptTag();
    public Regex EndPreScriptTag => MarkedRegexes.OtherEndPreScriptTag();
    public Regex StartAngleBracket => MarkedRegexes.OtherStartAngleBracket();
    public Regex EndAngleBracket => MarkedRegexes.OtherEndAngleBracket();
    public Regex UnicodeAlphaNumeric => MarkedRegexes.OtherUnicodeAlphaNumeric();
    public Regex FindPipe => MarkedRegexes.OtherFindPipe();
    public Regex SplitPipe => MarkedRegexes.OtherSplitPipe();
    public Regex SlashPipe => MarkedRegexes.OtherSlashPipe();
    public Regex CarriageReturn => MarkedRegexes.OtherCarriageReturn();

    /// <summary><c>other.listItemRegex(bull)</c> for the bullets marked's list tokenizer builds.</summary>
    public Regex ListItemRegex(string bull) => bull switch
    {
        @"\d{1,9}\." => MarkedRegexes.OtherListItemOrderedDot(),
        @"\d{1,9}\)" => MarkedRegexes.OtherListItemOrderedParen(),
        @"\*" => MarkedRegexes.OtherListItemStar(),
        @"\+" => MarkedRegexes.OtherListItemPlus(),
        @"\-" => MarkedRegexes.OtherListItemDash(),
        _ => throw new ArgumentException("marked builds list item patterns only for its own bullets.", nameof(bull)),
    };

    public Regex NextBulletRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherNextBullet0(),
        1 => MarkedRegexes.OtherNextBullet1(),
        2 => MarkedRegexes.OtherNextBullet2(),
        _ => MarkedRegexes.OtherNextBullet3(),
    };

    public Regex HrRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherHr0(),
        1 => MarkedRegexes.OtherHr1(),
        2 => MarkedRegexes.OtherHr2(),
        _ => MarkedRegexes.OtherHr3(),
    };

    public Regex FencesBeginRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherFencesBegin0(),
        1 => MarkedRegexes.OtherFencesBegin1(),
        2 => MarkedRegexes.OtherFencesBegin2(),
        _ => MarkedRegexes.OtherFencesBegin3(),
    };

    public Regex HeadingBeginRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherHeadingBegin0(),
        1 => MarkedRegexes.OtherHeadingBegin1(),
        2 => MarkedRegexes.OtherHeadingBegin2(),
        _ => MarkedRegexes.OtherHeadingBegin3(),
    };

    public Regex HtmlBeginRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherHtmlBegin0(),
        1 => MarkedRegexes.OtherHtmlBegin1(),
        2 => MarkedRegexes.OtherHtmlBegin2(),
        _ => MarkedRegexes.OtherHtmlBegin3(),
    };

    public Regex BlockquoteBeginRegex(int indent) => CacheIndex(indent) switch
    {
        0 => MarkedRegexes.OtherBlockquoteBegin0(),
        1 => MarkedRegexes.OtherBlockquoteBegin1(),
        2 => MarkedRegexes.OtherBlockquoteBegin2(),
        _ => MarkedRegexes.OtherBlockquoteBegin3(),
    };

    // rules.ts cachedIndentRegex: Math.max(0, Math.min(3, indent - 1)).
    private static int CacheIndex(int indent) => Math.Max(0, Math.Min(3, indent - 1));
}
