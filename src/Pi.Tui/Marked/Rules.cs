// Ported from marked 18.0.5; see LICENSE in this directory.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Pi.Tui;

internal sealed class MarkedRuleSet
{
    internal BlockRules Block { get; } = new();
    internal InlineRules Inline { get; } = new();
    internal OtherRules Other { get; } = new();
}

internal sealed class BlockRules
{
    internal Regex Blockquote { get; } = MarkedRegex.Create("""^( {0,3}> ?(([^\n]+(?:\n(?! {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]+[^ \t\n]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)| +\n)[^\n]+)*)|[^\n]*)(?:\n|$))+""");
    internal Regex Code { get; } = MarkedRegex.Create("""^((?: {4}| {0,3}\t)[^\n]+(?:\n(?:[ \t]*(?:\n|$))*)?)+""");
    internal Regex Def { get; } = MarkedRegex.Create("""^ {0,3}\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]: *(?:\n[ \t]*)?([^<\s][^\s]*|<.*?>)(?:(?: +(?:\n[ \t]*)?| *\n[ \t]*)((?:\"(?:\\\"?|[^\"\\])*\"|'[^'\n]*(?:\n[^'\n]+)*\n?'|\([^()]*\))))? *(?:\n+|$)""");
    internal Regex Fences { get; } = MarkedRegex.Create("""^ {0,3}(`{3,}(?=[^`\n]*(?:\n|$))|~{3,})([^\n]*)(?:\n|$)(?:|([\s\S]*?)(?:\n|$))(?: {0,3}\1[~`]* *(?=\n|$)|$)""");
    internal Regex Heading { get; } = MarkedRegex.Create("""^ {0,3}(#{1,6})(?=\s|$)(.*)(?:\n+|$)""");
    internal Regex Hr { get; } = MarkedRegex.Create("""^ {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)""");
    internal Regex Html { get; } = MarkedRegex.Create("""^ {0,3}(?:<(script|pre|style|textarea)[\s>][\s\S]*?(?:<\/\1>[^\n]*\n+|$)|<!--(?:-?>|[\s\S]*?(?:-->|$))[^\n]*(\n+|$)|<\?[\s\S]*?(?:\?>\n*|$)|<![A-Z][\s\S]*?(?:>\n*|$)|<!\[CDATA\[[\s\S]*?(?:\]\]>\n*|$)|<\/?(address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)[\s\S]*?(?:(?:\n[ \t]*)+\n|$)|<(?!script|pre|style|textarea)([a-z][\w-]*)(?: +[a-zA-Z:_][\w.:-]*(?: *= *\"[^\"\n]*\"| *= *'[^'\n]*'| *= *[^\s\"'=<>`]+)?)*? *\/?>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ \t]*)+\n|$)|<\/(?!script|pre|style|textarea)[a-z][\w-]*\s*>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ \t]*)+\n|$))""", ignoreCase: true);
    internal Regex Lheading { get; } = MarkedRegex.Create("""^(?! {0,3}(?:[*+-]|\d{1,9}[.)]) |(?: {4}| {0,3}\t)| {0,3}(?:`{3,}|~{3,})| {0,3}>| {0,3}#{1,6}| {0,3}<[^\n>]+>\n| {0,3}\|?(?:[:\- ]*\|)+[\:\- ]*\n)((?:.|\n(?!\s*?\n| {0,3}(?:[*+-]|\d{1,9}[.)]) |(?: {4}| {0,3}\t)| {0,3}(?:`{3,}|~{3,})| {0,3}>| {0,3}#{1,6}| {0,3}<[^\n>]+>\n| {0,3}\|?(?:[:\- ]*\|)+[\:\- ]*\n))+?)\n {0,3}(=+|-+) *(?:\n+|$)""");
    internal Regex List { get; } = MarkedRegex.Create("""^( {0,3}(?:[*+-]|\d{1,9}[.)]))([ \t][^\n]*?)?(?:\n|$)""");
    internal Regex Newline { get; } = MarkedRegex.Create("""^(?:[ \t]*(?:\n|$))+""");
    internal Regex Paragraph { get; } = MarkedRegex.Create("""^([^\n]+(?:\n(?! {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]+[^ \t\n]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)| *([^\n ].*)\n {0,3}((?:\| *)?:?-+:? *(?:\| *:?-+:? *)*(?:\| *)?)(?:\n((?:(?! *\n| {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>|(?: {4}| {0,3}\t)[^\n]| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)).*(?:\n|$))*)\n*|$)| +\n)[^\n]+)*)""");
    internal Regex Table { get; } = MarkedRegex.Create("""^ *([^\n ].*)\n {0,3}((?:\| *)?:?-+:? *(?:\| *:?-+:? *)*(?:\| *)?)(?:\n((?:(?! *\n| {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>|(?: {4}| {0,3}\t)[^\n]| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)).*(?:\n|$))*)\n*|$)""");
    internal Regex Text { get; } = MarkedRegex.Create("""^[^\n]+""");
}

internal sealed class InlineRules
{
    internal Regex Backpedal { get; } = MarkedRegex.Create("""(?:[^?!.,:;*_'\"~()&]+|\([^)]*\)|&(?![a-zA-Z0-9]+;$)|[?!.,:;*_'\"~)]+(?!$))+""");
    internal Regex AnyPunctuation { get; } = MarkedRegex.Create("""\\([\p{P}\p{S}])""");
    internal Regex Autolink { get; } = MarkedRegex.Create("""^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s\x00-\x1f<>]*|[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+(@)[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+(?![-_]))>""");
    internal Regex BlockSkip { get; } = MarkedRegex.Create("""\[(?:[^\[\]`]|(?<a>`+)[^`]+\k<a>(?!`))*?\]\((?:\\[\s\S]|[^\\\(\)]|\((?:\\[\s\S]|[^\\\(\)])*\))*\)|(?<!`)()(?<b>`+)[^`]+\k<b>(?!`)|<(?! )[^<>]*?>""");
    internal Regex Br { get; } = MarkedRegex.Create("""^( {2,}|\\)\n(?!\s*$)""");
    internal Regex Code { get; } = MarkedRegex.Create("""^(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)""");
    internal Regex Del { get; } = MarkedRegex.Create("""^(~~?)(?=[^\s~])((?:\\[\s\S]|[^\\])*?(?:\\[\s\S]|[^\s~\\]))\1(?=[^~]|$)""");
    internal Regex DelLDelim { get; } = MarkedRegex.Create("""^~~?(?:((?!~)[\p{P}\p{S}])|[^\s~])""");
    internal Regex DelRDelim { get; } = MarkedRegex.Create("""^[^~]+(?=[^~])|(?!~)[\p{P}\p{S}](~~?)(?=[\s]|$)|[^\s\p{P}\p{S}](~~?)(?!~)(?=[\s\p{P}\p{S}]|$)|(?!~)[\s\p{P}\p{S}](~~?)(?=[^\s\p{P}\p{S}])|[\s](~~?)(?!~)(?=[\p{P}\p{S}])|(?!~)[\p{P}\p{S}](~~?)(?!~)(?=[\p{P}\p{S}])|[^\s\p{P}\p{S}](~~?)(?=[^\s\p{P}\p{S}])""");
    internal Regex EmStrongLeft { get; } = MarkedRegex.Create("""^(?:\*+(?:((?!\*)(?!~)[\p{P}\p{S}])|([^\s*]))?)|^_+(?:((?!_)(?!~)[\p{P}\p{S}])|([^\s_]))?""");
    internal Regex EmStrongRightAst { get; } = MarkedRegex.Create("""^[^_*]*?__[^_*]*?\*[^_*]*?(?=__)|[^*]+(?=[^*])|(?!\*)(?!~)[\p{P}\p{S}](\*+)(?=[\s]|$)|(?:[^\s\p{P}\p{S}]|~)(\*+)(?!\*)(?=(?!~)[\s\p{P}\p{S}]|$)|(?!\*)(?!~)[\s\p{P}\p{S}](\*+)(?=(?:[^\s\p{P}\p{S}]|~))|[\s](\*+)(?!\*)(?=(?!~)[\p{P}\p{S}])|(?!\*)(?!~)[\p{P}\p{S}](\*+)(?!\*)(?=(?!~)[\p{P}\p{S}])|(?:[^\s\p{P}\p{S}]|~)(\*+)(?=(?:[^\s\p{P}\p{S}]|~))""");
    internal Regex EmStrongRightUnd { get; } = MarkedRegex.Create("""^[^_*]*?\*\*[^_*]*?_[^_*]*?(?=\*\*)|[^_]+(?=[^_])|(?!_)[\p{P}\p{S}](_+)(?=[\s]|$)|[^\s\p{P}\p{S}](_+)(?!_)(?=[\s\p{P}\p{S}]|$)|(?!_)[\s\p{P}\p{S}](_+)(?=[^\s\p{P}\p{S}])|[\s](_+)(?!_)(?=[\p{P}\p{S}])|(?!_)[\p{P}\p{S}](_+)(?!_)(?=[\p{P}\p{S}])""");
    internal Regex Escape { get; } = MarkedRegex.Create("""^\\([!\"#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])""");
    internal Regex Link { get; } = MarkedRegex.Create("""^!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\(\s*(<(?:\\.|[^\n<>\\])+>|[^ \t\n\x00-\x1f]*)(?:(?:[ \t]+(?:\n[ \t]*)?|\n[ \t]*)(\"(?:\\\"?|[^\"\\])*\"|'(?:\\'?|[^'\\])*'|\((?:\\\)?|[^)\\])*\)))?\s*\)""");
    internal Regex NoLink { get; } = MarkedRegex.Create("""^!?\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\](?:\[\])?""");
    internal Regex Punctuation { get; } = MarkedRegex.Create("""^((?![*_])[\s\p{P}\p{S}])""");
    internal Regex RefLink { get; } = MarkedRegex.Create("""^!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]""");
    internal Regex RefLinkSearch { get; } = MarkedRegex.Create("""!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]|!?\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\](?:\[\])?(?!\()""");
    internal Regex Tag { get; } = MarkedRegex.Create("""^<!--(?:-?>|[\s\S]*?-->)|^<\/[a-zA-Z][\w:-]*\s*>|^<[a-zA-Z][\w-]*(?:\s+[a-zA-Z:_][\w.:-]*(?:\s*=\s*\"[^\"]*\"|\s*=\s*'[^']*'|\s*=\s*[^\s\"'=<>`]+)?)*?\s*\/?>|^<\?[\s\S]*?\?>|^<![a-zA-Z]+\s[\s\S]*?>|^<!\[CDATA\[[\s\S]*?\]\]>""");
    internal Regex Text { get; } = MarkedRegex.Create("""^([`~]+|[^`~])(?:(?= {2,}\n)|(?=[a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-]+@)|[\s\S]*?(?:(?=[\\<!\[`*~_]|\b_|[hH][tT][tT][pP][sS]?|[fF][tT][pP]:\/\/|www\.|$)|[^ ](?= {2,}\n)|[^a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-](?=[a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-]+@)))""");
    internal Regex Url { get; } = MarkedRegex.Create("""^((?:[hH][tT][tT][pP][sS]?|[fF][tT][pP]):\/\/|www\.)(?:[a-zA-Z0-9\-]+\.?)+[^\s<]*|^[A-Za-z0-9._+-]+(@)[a-zA-Z0-9-_]+(?:\.[a-zA-Z0-9-_]*[a-zA-Z0-9])+(?![-_])""");
}

internal sealed class OtherRules
{
    internal Regex CodeRemoveIndent { get; } = MarkedRegex.Create("""^(?: {1,4}| {0,3}\t)""", multiline: true);
    internal Regex OutputLinkReplace { get; } = MarkedRegex.Create("""\\([\[\]])""");
    internal Regex IndentCodeCompensation { get; } = MarkedRegex.Create("""^(\s+)(?:```)""");
    internal Regex BeginningSpace { get; } = MarkedRegex.Create("""^\s+""");
    internal Regex EndingHash { get; } = MarkedRegex.Create("""#$""");
    internal Regex StartingSpaceChar { get; } = MarkedRegex.Create("""^ """);
    internal Regex EndingSpaceChar { get; } = MarkedRegex.Create(""" $""");
    internal Regex NonSpaceChar { get; } = MarkedRegex.Create("""[^ ]""");
    internal Regex NewLineCharGlobal { get; } = MarkedRegex.Create("""\n""");
    internal Regex TabCharGlobal { get; } = MarkedRegex.Create("""\t""");
    internal Regex MultipleSpaceGlobal { get; } = MarkedRegex.Create("""\s+""");
    internal Regex BlankLine { get; } = MarkedRegex.Create("""^[ \t]*$""");
    internal Regex DoubleBlankLine { get; } = MarkedRegex.Create("""\n[ \t]*\n[ \t]*$""");
    internal Regex BlockquoteStart { get; } = MarkedRegex.Create("""^ {0,3}>""");
    internal Regex BlockquoteSetextReplace { get; } = MarkedRegex.Create("""\n {0,3}((?:=+|-+) *)(?=\n|$)""", multiline: true);
    internal Regex BlockquoteSetextReplace2 { get; } = MarkedRegex.Create("""^ {0,3}>[ \t]?""", multiline: true);
    internal Regex ListReplaceNesting { get; } = MarkedRegex.Create("""^ {1,4}(?=( {4})*[^ ])""", multiline: true);
    internal Regex ListIsTask { get; } = MarkedRegex.Create("""^\[[ xX]\] +\S""");
    internal Regex ListReplaceTask { get; } = MarkedRegex.Create("""^\[[ xX]\] +""");
    internal Regex ListTaskCheckbox { get; } = MarkedRegex.Create("""\[[ xX]\]""");
    internal Regex AnyLine { get; } = MarkedRegex.Create("""\n.*\n""");
    internal Regex HrefBrackets { get; } = MarkedRegex.Create("""^<(.*)>$""");
    internal Regex TableDelimiter { get; } = MarkedRegex.Create("""[:|]""");
    internal Regex TableAlignChars { get; } = MarkedRegex.Create("""^\||\| *$""", multiline: true);
    internal Regex TableRowBlankLine { get; } = MarkedRegex.Create("""\n[ \t]*$""");
    internal Regex TableAlignRight { get; } = MarkedRegex.Create("""^ *-+: *$""");
    internal Regex TableAlignCenter { get; } = MarkedRegex.Create("""^ *:-+: *$""");
    internal Regex TableAlignLeft { get; } = MarkedRegex.Create("""^ *:-+ *$""");
    internal Regex StartATag { get; } = MarkedRegex.Create("""^<a """, ignoreCase: true);
    internal Regex EndATag { get; } = MarkedRegex.Create("""^</a>""", ignoreCase: true);
    internal Regex StartPreScriptTag { get; } = MarkedRegex.Create("""^<(pre|code|kbd|script)(\s|>)""", ignoreCase: true);
    internal Regex EndPreScriptTag { get; } = MarkedRegex.Create("""^</(pre|code|kbd|script)(\s|>)""", ignoreCase: true);
    internal Regex StartAngleBracket { get; } = MarkedRegex.Create("""^<""");
    internal Regex EndAngleBracket { get; } = MarkedRegex.Create(""">$""");
    internal Regex PedanticHrefTitle { get; } = MarkedRegex.Create("""^([^'\"]*[^\s])\s+(['\"])(.*)\2""");
    internal Regex UnicodeAlphaNumeric { get; } = MarkedRegex.Create("""[\p{L}\p{N}]""");
    internal Regex EscapeTest { get; } = MarkedRegex.Create("""[&<>\"']""");
    internal Regex EscapeTestNoEncode { get; } = MarkedRegex.Create("""[<>\"']|&(?!(#\d{1,7}|#[Xx][a-fA-F0-9]{1,6}|[A-Za-z0-9_]+);)""");
    internal Regex PercentDecode { get; } = MarkedRegex.Create("""%25""");
    internal Regex FindPipe { get; } = MarkedRegex.Create("""\|""");
    internal Regex SlashPipe { get; } = MarkedRegex.Create("""\\\|""");
    internal Regex CarriageReturn { get; } = MarkedRegex.Create("""\r\n|\r""");
    internal Regex SpaceLine { get; } = MarkedRegex.Create("""^ +$""", multiline: true);
    internal Regex NotSpaceStart { get; } = MarkedRegex.Create("""^\S*""");
    internal Regex EndingNewline { get; } = MarkedRegex.Create("""\n$""");

    private readonly ConcurrentDictionary<string, Regex> _dynamic = new(StringComparer.Ordinal);

    private Regex Cached(string key, Func<Regex> factory) => _dynamic.GetOrAdd(key, _ => factory());
    internal Regex ListItemRegex(string bull) => Cached($"list:{bull}", () => MarkedRegex.Create($"^( {{0,3}}{bull})((?:[\\t ][^\\n]*)?(?:\\n|$))"));
    internal Regex NextBulletRegex(int indent) => Cached($"bullet:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}(?:[*+-]|\\d{{1,9}}[.)])((?:[ \\t][^\\n]*)?(?:\\n|$))"));
    internal Regex HrRegex(int indent) => Cached($"hr:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}((?:- *){{3,}}|(?:_ *){{3,}}|(?:\\* *){{3,}})(?:\\n+|$)"));
    internal Regex FencesBeginRegex(int indent) => Cached($"fence:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}(?:```|~~~)"));
    internal Regex HeadingBeginRegex(int indent) => Cached($"heading:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}#"));
    internal Regex HtmlBeginRegex(int indent) => Cached($"html:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}<(?:[a-z].*>|!--)", ignoreCase: true));
    internal Regex BlockquoteBeginRegex(int indent) => Cached($"quote:{indent}", () => MarkedRegex.Create($"^ {{0,{Math.Clamp(indent - 1, 0, 3)}}}>"));
}

internal static class MarkedRegex
{
    internal static Regex Create(string pattern, bool ignoreCase = false, bool multiline = false)
    {
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Multiline;
        return new Regex(TranslateJavaScriptAsciiClasses(pattern), options);
    }

    private static string TranslateJavaScriptAsciiClasses(string pattern)
    {
        var result = new System.Text.StringBuilder(pattern.Length + 32);
        var inClass = false;
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];
            if (escaped)
            {
                result.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] is 'w' or 'd')
                {
                    var asciiClass = pattern[++i] == 'w' ? "A-Za-z0-9_" : "0-9";
                    if (inClass) result.Append(asciiClass);
                    else result.Append('[').Append(asciiClass).Append(']');
                }
                else
                {
                    result.Append(character);
                    escaped = true;
                }
                continue;
            }
            if (character == '[') inClass = true;
            else if (character == ']' && inClass) inClass = false;
            result.Append(character);
        }
        return result.ToString().Replace(@"\b_", "(?<![A-Za-z0-9_])_", StringComparison.Ordinal);
    }
}
