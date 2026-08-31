using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Options for rendering a LaTeX expression as terminal-friendly text.</summary>
public sealed record RenderLatexOptions
{
    /// <summary>Stacks fractions and operator limits vertically for display math (default: false).</summary>
    public bool Display { get; init; }
}

/// <summary>Renders basic LaTeX math expressions as terminal-friendly Unicode text.</summary>
public static class Latex
{
    private static readonly IReadOnlyDictionary<string, string> _symbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ϵ",
        ["varepsilon"] = "ε",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["theta"] = "θ",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["varkappa"] = "ϰ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["pi"] = "π",
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",
        ["varsigma"] = "ς",
        ["tau"] = "τ",
        ["upsilon"] = "υ",
        ["phi"] = "ϕ",
        ["varphi"] = "φ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["omega"] = "ω",
        ["Gamma"] = "Γ",
        ["Delta"] = "Δ",
        ["Theta"] = "Θ",
        ["Lambda"] = "Λ",
        ["Xi"] = "Ξ",
        ["Pi"] = "Π",
        ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ",
        ["Phi"] = "Φ",
        ["Psi"] = "Ψ",
        ["Omega"] = "Ω",
        ["pm"] = "±",
        ["mp"] = "∓",
        ["times"] = "×",
        ["div"] = "÷",
        ["cdot"] = "·",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["circ"] = "∘",
        ["bullet"] = "•",
        ["oplus"] = "⊕",
        ["ominus"] = "⊖",
        ["otimes"] = "⊗",
        ["oslash"] = "⊘",
        ["odot"] = "⊙",
        ["bigcirc"] = "○",
        ["dagger"] = "†",
        ["ddagger"] = "‡",
        ["amalg"] = "⨿",
        ["uplus"] = "⊎",
        ["sqcap"] = "⊓",
        ["sqcup"] = "⊔",
        ["triangleleft"] = "◁",
        ["triangleright"] = "▷",
        ["wr"] = "≀",
        ["cap"] = "∩",
        ["cup"] = "∪",
        ["bigcap"] = "⋂",
        ["bigcup"] = "⋃",
        ["bigwedge"] = "⋀",
        ["bigvee"] = "⋁",
        ["bigsqcup"] = "⨆",
        ["biguplus"] = "⨄",
        ["bigoplus"] = "⨁",
        ["bigotimes"] = "⨂",
        ["bigodot"] = "⨀",
        ["setminus"] = "∖",
        ["in"] = "∈",
        ["notin"] = "∉",
        ["ni"] = "∋",
        ["subset"] = "⊂",
        ["supset"] = "⊃",
        ["subseteq"] = "⊆",
        ["supseteq"] = "⊇",
        ["sqsubset"] = "⊏",
        ["sqsupset"] = "⊐",
        ["sqsubseteq"] = "⊑",
        ["sqsupseteq"] = "⊒",
        ["prec"] = "≺",
        ["preceq"] = "≼",
        ["succ"] = "≻",
        ["succeq"] = "≽",
        ["ll"] = "≪",
        ["gg"] = "≫",
        ["le"] = "≤",
        ["leq"] = "≤",
        ["leqslant"] = "≤",
        ["ge"] = "≥",
        ["geq"] = "≥",
        ["geqslant"] = "≥",
        ["ne"] = "≠",
        ["neq"] = "≠",
        ["equiv"] = "≡",
        ["approx"] = "≈",
        ["sim"] = "∼",
        ["simeq"] = "≃",
        ["cong"] = "≅",
        ["asymp"] = "≍",
        ["doteq"] = "≐",
        ["propto"] = "∝",
        ["parallel"] = "∥",
        ["perp"] = "⊥",
        ["mid"] = "∣",
        ["vdash"] = "⊢",
        ["dashv"] = "⊣",
        ["models"] = "⊨",
        ["Vdash"] = "⊩",
        ["Vvdash"] = "⊪",
        ["nvdash"] = "⊬",
        ["nvDash"] = "⊭",
        ["forall"] = "∀",
        ["exists"] = "∃",
        ["nexists"] = "∄",
        ["neg"] = "¬",
        ["land"] = "∧",
        ["wedge"] = "∧",
        ["lor"] = "∨",
        ["vee"] = "∨",
        ["to"] = "→",
        ["rightarrow"] = "→",
        ["longrightarrow"] = "→",
        ["leftarrow"] = "←",
        ["longleftarrow"] = "←",
        ["gets"] = "←",
        ["leftrightarrow"] = "↔",
        ["longleftrightarrow"] = "↔",
        ["hookleftarrow"] = "↩",
        ["hookrightarrow"] = "↪",
        ["twoheadleftarrow"] = "↞",
        ["twoheadrightarrow"] = "↠",
        ["leftharpoonup"] = "↼",
        ["leftharpoondown"] = "↽",
        ["rightharpoonup"] = "⇀",
        ["rightharpoondown"] = "⇁",
        ["rightleftharpoons"] = "⇌",
        ["leftrightharpoons"] = "⇋",
        ["nearrow"] = "↗",
        ["searrow"] = "↘",
        ["swarrow"] = "↙",
        ["nwarrow"] = "↖",
        ["rightsquigarrow"] = "⇝",
        ["leadsto"] = "⇝",
        ["Rightarrow"] = "⇒",
        ["Longrightarrow"] = "⇒",
        ["Leftarrow"] = "⇐",
        ["Longleftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔",
        ["Longleftrightarrow"] = "⇔",
        ["implies"] = "⇒",
        ["iff"] = "⇔",
        ["mapsto"] = "↦",
        ["longmapsto"] = "↦",
        ["uparrow"] = "↑",
        ["downarrow"] = "↓",
        ["partial"] = "∂",
        ["nabla"] = "∇",
        ["int"] = "∫",
        ["iint"] = "∬",
        ["iiint"] = "∭",
        ["oint"] = "∮",
        ["sum"] = "∑",
        ["prod"] = "∏",
        ["coprod"] = "∐",
        ["infty"] = "∞",
        ["emptyset"] = "∅",
        ["varnothing"] = "∅",
        ["angle"] = "∠",
        ["therefore"] = "∴",
        ["because"] = "∵",
        ["aleph"] = "ℵ",
        ["beth"] = "ℶ",
        ["gimel"] = "ℷ",
        ["daleth"] = "ℸ",
        ["top"] = "⊤",
        ["bot"] = "⊥",
        ["triangle"] = "△",
        ["square"] = "□",
        ["lozenge"] = "◊",
        ["checkmark"] = "✓",
        ["complement"] = "∁",
        ["wp"] = "℘",
        ["prime"] = "′",
        ["ldots"] = "…",
        ["dots"] = "…",
        ["cdots"] = "⋯",
        ["vdots"] = "⋮",
        ["ddots"] = "⋱",
        ["ell"] = "ℓ",
        ["hbar"] = "ℏ",
        ["Im"] = "ℑ",
        ["Re"] = "ℜ",
        ["langle"] = "⟨",
        ["rangle"] = "⟩",
        ["vert"] = "|",
        ["lvert"] = "|",
        ["rvert"] = "|",
        ["Vert"] = "‖",
        ["lVert"] = "‖",
        ["rVert"] = "‖",
        ["lbrace"] = "{",
        ["rbrace"] = "}",
        ["backslash"] = "\\",
        ["lfloor"] = "⌊",
        ["rfloor"] = "⌋",
        ["lceil"] = "⌈",
        ["rceil"] = "⌉",
        ["colon"] = ":",
    };

    private static readonly HashSet<string> _namedOperators =
    [
        "arccos", "arcsin", "arctan", "arg", "cos", "cosh", "cot", "coth", "csc", "deg", "det", "dim", "exp",
        "gcd", "hom", "inf", "ker", "lg", "lim", "liminf", "limsup", "ln", "log", "max", "min", "Pr", "sec",
        "sin", "sinh", "sup", "tan", "tanh",
    ];

    private static readonly HashSet<string> _limitOperators =
    [
        "argmax", "argmin", "inf", "injlim", "lim", "liminf", "limsup", "max", "min", "projlim", "sup",
    ];

    private static readonly HashSet<string> _displayLimitSymbols =
    [
        "bigcap", "bigcup", "bigodot", "bigoplus", "bigotimes", "bigsqcup", "biguplus", "bigvee", "bigwedge", "coprod",
        "int", "iint", "iiint", "oint", "prod", "sum",
    ];

    private static readonly HashSet<string> _relationCommands =
    [
        "Leftarrow", "Leftrightarrow", "Longleftarrow", "Longleftrightarrow", "Longrightarrow", "Rightarrow", "Vdash", "Vvdash",
        "approx", "asymp", "cong", "dashv", "doteq", "downarrow", "equiv", "ge", "geq", "geqslant", "gets", "gg",
        "hookleftarrow", "hookrightarrow", "iff", "implies", "in", "leadsto", "le", "leftarrow", "leftharpoondown",
        "leftharpoonup", "leftrightarrow", "leftrightharpoons", "leq", "leqslant", "ll", "longleftarrow", "longleftrightarrow",
        "longmapsto", "longrightarrow", "mapsto", "mid", "models", "ne", "nearrow", "neq", "ni", "notin", "nvdash", "nvDash",
        "nwarrow", "parallel", "perp", "prec", "preceq", "propto", "rightharpoondown", "rightharpoonup", "rightleftharpoons",
        "rightarrow", "rightsquigarrow", "searrow", "sim", "simeq", "sqsubset", "sqsubseteq", "sqsupset", "sqsupseteq",
        "subset", "subseteq", "succ", "succeq", "supset", "supseteq", "swarrow", "to", "triangleleft", "triangleright",
        "twoheadleftarrow", "twoheadrightarrow", "uparrow", "vdash",
    ];

    private static readonly IReadOnlyDictionary<string, string> _negatedSymbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["<"] = "≮",
        [">"] = "≯",
        ["="] = "≠",
        ["∈"] = "∉",
        ["∋"] = "∌",
        ["∣"] = "∤",
        ["∥"] = "∦",
        ["∼"] = "≁",
        ["≃"] = "≄",
        ["≅"] = "≇",
        ["≈"] = "≉",
        ["≡"] = "≢",
        ["≤"] = "≰",
        ["≥"] = "≱",
        ["≺"] = "⊀",
        ["≻"] = "⊁",
        ["⊂"] = "⊄",
        ["⊃"] = "⊅",
        ["⊆"] = "⊈",
        ["⊇"] = "⊉",
        ["⊢"] = "⊬",
        ["⊨"] = "⊭",
        ["↔"] = "↮",
        ["←"] = "↚",
        ["→"] = "↛",
        ["⇒"] = "⇏",
        ["⇐"] = "⇍",
        ["⇔"] = "⇎",
        ["≼"] = "⋠",
        ["≽"] = "⋡",
    };

    private static readonly IReadOnlyDictionary<string, string> _blackboard = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C"] = "ℂ",
        ["H"] = "ℍ",
        ["N"] = "ℕ",
        ["P"] = "ℙ",
        ["Q"] = "ℚ",
        ["R"] = "ℝ",
        ["Z"] = "ℤ",
    };

    private static readonly IReadOnlyDictionary<string, string> _superscripts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0"] = "⁰",
        ["1"] = "¹",
        ["2"] = "²",
        ["3"] = "³",
        ["4"] = "⁴",
        ["5"] = "⁵",
        ["6"] = "⁶",
        ["7"] = "⁷",
        ["8"] = "⁸",
        ["9"] = "⁹",
        ["+"] = "⁺",
        ["-"] = "⁻",
        ["="] = "⁼",
        ["("] = "⁽",
        [")"] = "⁾",
        ["a"] = "ᵃ",
        ["b"] = "ᵇ",
        ["c"] = "ᶜ",
        ["d"] = "ᵈ",
        ["e"] = "ᵉ",
        ["f"] = "ᶠ",
        ["g"] = "ᵍ",
        ["h"] = "ʰ",
        ["i"] = "ⁱ",
        ["j"] = "ʲ",
        ["k"] = "ᵏ",
        ["l"] = "ˡ",
        ["m"] = "ᵐ",
        ["n"] = "ⁿ",
        ["o"] = "ᵒ",
        ["p"] = "ᵖ",
        ["r"] = "ʳ",
        ["s"] = "ˢ",
        ["t"] = "ᵗ",
        ["u"] = "ᵘ",
        ["v"] = "ᵛ",
        ["w"] = "ʷ",
        ["x"] = "ˣ",
        ["y"] = "ʸ",
        ["z"] = "ᶻ",
    };

    private static readonly IReadOnlyDictionary<string, string> _subscripts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0"] = "₀",
        ["1"] = "₁",
        ["2"] = "₂",
        ["3"] = "₃",
        ["4"] = "₄",
        ["5"] = "₅",
        ["6"] = "₆",
        ["7"] = "₇",
        ["8"] = "₈",
        ["9"] = "₉",
        ["+"] = "₊",
        ["-"] = "₋",
        ["="] = "₌",
        ["("] = "₍",
        [")"] = "₎",
        ["a"] = "ₐ",
        ["e"] = "ₑ",
        ["h"] = "ₕ",
        ["i"] = "ᵢ",
        ["j"] = "ⱼ",
        ["k"] = "ₖ",
        ["l"] = "ₗ",
        ["m"] = "ₘ",
        ["n"] = "ₙ",
        ["o"] = "ₒ",
        ["p"] = "ₚ",
        ["r"] = "ᵣ",
        ["s"] = "ₛ",
        ["t"] = "ₜ",
        ["u"] = "ᵤ",
        ["v"] = "ᵥ",
        ["x"] = "ₓ",
    };

    private static readonly HashSet<string> _spacingCommands =
    [
        ",", ":", ";", " ", ">", "enspace", "enskip", "medspace", "quad", "qquad", "thickspace", "thinspace",
    ];

    private static readonly HashSet<string> _negativeSpacingCommands = ["!", "negmedspace", "negthickspace", "negthinspace"];
    private static readonly HashSet<string> _ignoredCommands = ["displaystyle", "limits", "nolimits", "scriptstyle", "scriptscriptstyle", "textstyle"];
    private static readonly HashSet<string> _sizeCommands =
    [
        "big", "Big", "bigg", "Bigg", "bigl", "Bigl", "biggl", "Biggl", "bigr", "Bigr", "biggr", "Biggr",
    ];
    private static readonly HashSet<string> _plainWrappers =
    [
        "emph", "mathcal", "mathbf", "mathfrak", "mathit", "mathrm", "mathnormal", "mathscr", "mathsf", "mathtt", "mathup", "mbox",
        "overbrace", "pmb", "smash", "substack", "text", "textbf", "textit", "textmd", "textnormal", "textrm", "textsc", "textsf",
        "textsl", "texttt", "textup", "underbrace", "bm", "boldsymbol",
    ];

    private static readonly IReadOnlyDictionary<string, string> _accents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["acute"] = "\u0301",
        ["bar"] = "\u0305",
        ["breve"] = "\u0306",
        ["check"] = "\u030c",
        ["ddot"] = "\u0308",
        ["dot"] = "\u0307",
        ["grave"] = "\u0300",
        ["hat"] = "\u0302",
        ["mathring"] = "\u030a",
        ["overleftarrow"] = "\u20d6",
        ["overleftrightarrow"] = "\u20e1",
        ["overline"] = "\u0305",
        ["overrightarrow"] = "\u20d7",
        ["tilde"] = "\u0303",
        ["underline"] = "\u0332",
        ["vec"] = "\u20d7",
        ["widehat"] = "\u0302",
        ["widetilde"] = "\u0303",
    };

    private const string _namedOperatorStart = "\U000F0004";
    private const string _namedOperatorEnd = "\U000F0005";
    private const string _layoutMarkerStart = "\U000F0000";
    private const string _layoutMarkerEnd = "\U000F0001";
    private const string _protectedSpace = "\U000F0002";
    private const string _negativeSpace = "\0";

    private static readonly Regex _scriptSpacing = new(@"\s*([=+-])\s*", RegexOptions.CultureInvariant);
    private static readonly Regex _simpleText = new(@"^[\p{L}\p{N}.]+$", RegexOptions.CultureInvariant);
    private static readonly Regex _simpleDenominator = new(@"^[\p{N}.]+$", RegexOptions.CultureInvariant);
    private static readonly Regex _letters = new(@"^[A-Za-z]+$", RegexOptions.CultureInvariant);
    private static readonly Regex _environmentRowBreak = new(@"\\\\(?:\[[^\]\n]*\])?", RegexOptions.CultureInvariant);
    private static readonly Regex _leadingEnvironmentArraySpec = new(@"^\s*\{[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex _trailingComma = new(@",\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex _conditionPrefix = new(@"^(?:if|when|for|otherwise)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex _operatorModifier = new(@"^\\(limits|nolimits)(?![A-Za-z])", RegexOptions.CultureInvariant);

    /// <summary>Renders a basic LaTeX math expression as terminal-friendly Unicode text.</summary>
    /// <remarks>Returns null when the expression contains unsupported or malformed syntax.</remarks>
    public static string? RenderLatex(string source, RenderLatexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var layoutNodes = new List<LayoutNode>();
        var rendered = new LatexParser(source, layoutNodes, options?.Display == true).Render();
        if (rendered is null)
        {
            return null;
        }

        if (layoutNodes.Count == 0)
        {
            return rendered.Replace(_protectedSpace, " ", StringComparison.Ordinal);
        }

        var lines = RenderLayout(rendered, layoutNodes).Lines;
        var nonEmptyLines = lines.Where(static line => line.Trim().Length > 0).ToArray();
        var indentation = nonEmptyLines.Length == 0
            ? 0
            : nonEmptyLines.Min(static line => line.Length - line.TrimStart().Length);
        return string.Join('\n', lines.Select(line => (line.Length > indentation ? line[indentation..] : string.Empty).TrimEnd()))
            .TrimEnd()
            .Replace(_protectedSpace, " ", StringComparison.Ordinal);
    }

    private static string? ReplaceCharacters(string value, IReadOnlyDictionary<string, string> replacements)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (!replacements.TryGetValue(rune.ToString(), out var replacement))
            {
                return null;
            }

            result.Append(replacement);
        }

        return result.ToString();
    }

    private static string FormatScript(string value, bool subscript)
    {
        value = value.Trim();
        var replacements = subscript ? _subscripts : _superscripts;
        var normalized = _scriptSpacing.Replace(value, "$1");
        var unicode = ReplaceCharacters(normalized, replacements);
        if (unicode is not null)
        {
            return unicode;
        }

        var prefix = subscript ? "_" : "^";
        if (value.EnumerateRunes().Count() == 1 || (subscript && _letters.IsMatch(value)))
        {
            return prefix + value;
        }

        return $"{prefix}({value})";
    }

    private static string FormatFraction(string numerator, string denominator)
    {
        numerator = numerator.Trim();
        denominator = denominator.Trim();
        var simpleNumerator = _simpleText.IsMatch(numerator);
        var simpleDenominator = _simpleDenominator.IsMatch(denominator) || denominator.EnumerateRunes().Count() == 1;
        return $"{(simpleNumerator ? numerator : $"({numerator})")}/{(simpleDenominator ? denominator : $"({denominator})")}";
    }

    private static string FormatRoot(string value, string symbol = "√")
    {
        value = value.Trim();
        return _simpleText.IsMatch(value) ? symbol + value : $"{symbol}({value})";
    }

    private static string NormalizeOutput(string value)
    {
        value = ReplaceNamedOperatorSpacing(value);
        var lines = value.Split('\n');
        var normalized = lines
            .Select(static line => Regex.Replace(line, @"[ \t]+", " ", RegexOptions.CultureInvariant).Trim())
            .Where((line, index) => line.Length > 0 || (index > 0 && index < lines.Length - 1));
        return string.Join('\n', normalized).Trim();
    }

    private static string ReplaceNamedOperatorSpacing(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (value.AsSpan(index).StartsWith(_namedOperatorStart, StringComparison.Ordinal))
            {
                if (result.Length > 0 && IsNamedOperatorLeftSpacingCharacter(result))
                {
                    result.Append(' ');
                }

                index += _namedOperatorStart.Length;
                continue;
            }

            if (value.AsSpan(index).StartsWith(_namedOperatorEnd, StringComparison.Ordinal))
            {
                result.Append(_namedOperatorEnd);
                index += _namedOperatorEnd.Length;
                if (index < value.Length && IsNamedOperatorRightSpacingCharacter(value, index))
                {
                    result.Append(' ');
                }

                continue;
            }

            result.Append(value[index++]);
        }

        return result.ToString()
            .Replace(_namedOperatorStart, string.Empty, StringComparison.Ordinal)
            .Replace(_namedOperatorEnd, string.Empty, StringComparison.Ordinal);
    }

    private static bool IsNamedOperatorLeftSpacingCharacter(StringBuilder result)
    {
        if (result.Length == 0)
        {
            return false;
        }

        var last = result[^1];
        if (char.IsLetterOrDigit(last) || last is ')' or ']' or '}' || result.ToString().EndsWith(_layoutMarkerEnd, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsNamedOperatorRightSpacingCharacter(string value, int index)
    {
        var character = value[index];
        return char.IsLetterOrDigit(character) || character == '√' || value.AsSpan(index).StartsWith(_layoutMarkerStart, StringComparison.Ordinal);
    }

    private static Layout RenderLayout(string source, IReadOnlyList<LayoutNode> nodes)
    {
        var renderedLines = new List<string>();
        var firstBaseline = 0;
        foreach (var sourceLine in source.Split('\n'))
        {
            var layouts = new List<Layout>();
            var position = 0;
            LayoutNode? previousNode = null;
            foreach (var marker in FindLayoutMarkers(sourceLine))
            {
                var index = marker.Index;
                var node = marker.NodeIndex >= 0 && marker.NodeIndex < nodes.Count ? nodes[marker.NodeIndex] : null;
                if (node is null)
                {
                    continue;
                }

                if (index > position)
                {
                    var sliced = sourceLine[position..index];
                    var trimmed = previousNode is not null ? sliced.TrimStart() : sliced;
                    trimmed = trimmed.TrimEnd();
                    var preserveLeadingSpace = previousNode is MatrixNode && HasWhitespacePrefix(sliced);
                    var preserveTrailingSpace = node is MatrixNode && HasWhitespaceSuffix(sliced);
                    var text = trimmed.Length > 0
                        ? $"{(preserveLeadingSpace ? " " : string.Empty)}{trimmed}{(preserveTrailingSpace ? " " : string.Empty)}"
                        : preserveLeadingSpace || preserveTrailingSpace ? " " : string.Empty;
                    layouts.Add(new Layout([text], TextMeasurement.VisibleWidth(text), 0));
                }

                if (node is FractionNode fraction)
                {
                    var numerator = RenderLayout(fraction.Numerator, nodes);
                    var denominator = RenderLayout(fraction.Denominator, nodes);
                    var contentWidth = Math.Max(Math.Max(numerator.Width, denominator.Width), 1);
                    var width = contentWidth + 2;
                    var lines = new List<string>(numerator.Lines.Count + denominator.Lines.Count + 1);
                    lines.AddRange(numerator.Lines.Select(line => PadLayoutLine(line, width, true)));
                    lines.Add($" {new string('─', contentWidth)} ");
                    lines.AddRange(denominator.Lines.Select(line => PadLayoutLine(line, width, true)));
                    layouts.Add(new Layout(lines, width, numerator.Lines.Count));
                }
                else if (node is OperatorNode op)
                {
                    var contentWidth = Math.Max(
                        Math.Max(TextMeasurement.VisibleWidth(op.Operator), op.Lower is null ? 0 : TextMeasurement.VisibleWidth(op.Lower)),
                        op.Upper is null ? 0 : TextMeasurement.VisibleWidth(op.Upper));
                    var lines = new List<string>();
                    if (op.Upper is not null)
                    {
                        lines.Add($"{PadLayoutLine(op.Upper, contentWidth, true)} ");
                    }

                    lines.Add($"{PadLayoutLine(op.Operator, contentWidth, true)} ");
                    if (op.Lower is not null)
                    {
                        lines.Add($"{PadLayoutLine(op.Lower, contentWidth, true)} ");
                    }

                    layouts.Add(new Layout(lines, contentWidth + 1, op.Upper is null ? 0 : 1));
                }
                else if (node is MatrixNode matrix)
                {
                    var width = matrix.Lines.Count == 0 ? 0 : matrix.Lines.Max(line => TextMeasurement.VisibleWidth(line));
                    layouts.Add(new Layout(matrix.Lines.Select(line => PadLayoutLine(line, width)).ToList(), width, matrix.Baseline));
                }

                position = marker.End;
                previousNode = node;
            }

            if (position < sourceLine.Length)
            {
                var sliced = sourceLine[position..];
                var trimmed = previousNode is not null ? sliced.TrimStart() : sliced;
                var text = previousNode is MatrixNode && HasWhitespacePrefix(sliced) ? $" {trimmed}" : trimmed;
                layouts.Add(new Layout([text], TextMeasurement.VisibleWidth(text), 0));
            }

            var lineLayout = JoinLayouts(layouts);
            if (renderedLines.Count == 0)
            {
                firstBaseline = lineLayout.Baseline;
            }

            renderedLines.AddRange(lineLayout.Lines);
        }

        return new Layout(
            renderedLines,
            renderedLines.Count == 0 ? 0 : renderedLines.Max(line => TextMeasurement.VisibleWidth(line)),
            firstBaseline);
    }

    private static bool HasWhitespacePrefix(string value) => value.Length > 0 && char.IsWhiteSpace(value[0]);

    private static bool HasWhitespaceSuffix(string value) => value.Length > 0 && char.IsWhiteSpace(value[^1]);

    private static IEnumerable<LayoutMarker> FindLayoutMarkers(string value)
    {
        var searchPosition = 0;
        while (searchPosition < value.Length)
        {
            var start = value.IndexOf(_layoutMarkerStart, searchPosition, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var numberStart = start + _layoutMarkerStart.Length;
            var numberEnd = numberStart;
            while (numberEnd < value.Length && char.IsAsciiDigit(value[numberEnd]))
            {
                numberEnd++;
            }

            if (numberEnd > numberStart && value.AsSpan(numberEnd).StartsWith(_layoutMarkerEnd, StringComparison.Ordinal) &&
                int.TryParse(value[numberStart..numberEnd], out var nodeIndex))
            {
                yield return new LayoutMarker(start, numberEnd + _layoutMarkerEnd.Length, nodeIndex);
                searchPosition = numberEnd + _layoutMarkerEnd.Length;
            }
            else
            {
                searchPosition = numberStart;
            }
        }
    }

    private static bool TryGetTrailingLayoutNodeIndex(string value, out int nodeIndex)
    {
        nodeIndex = -1;
        var end = value.LastIndexOf(_layoutMarkerEnd, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var start = value.LastIndexOf(_layoutMarkerStart, end, StringComparison.Ordinal);
        if (start < 0 || start + _layoutMarkerStart.Length >= end)
        {
            return false;
        }

        var number = value[(start + _layoutMarkerStart.Length)..end];
        return int.TryParse(number, out nodeIndex) && end + _layoutMarkerEnd.Length == value.Length;
    }

    private static string PadLayoutLine(string line, int width, bool centered = false)
    {
        var padding = Math.Max(0, width - TextMeasurement.VisibleWidth(line));
        var left = centered ? padding / 2 : 0;
        return new string(' ', left) + line + new string(' ', padding - left);
    }

    private static Layout JoinLayouts(IReadOnlyList<Layout> layouts)
    {
        if (layouts.Count == 0)
        {
            return new Layout([string.Empty], 0, 0);
        }

        var baseline = layouts.Max(layout => layout.Baseline);
        var below = layouts.Max(layout => layout.Lines.Count - layout.Baseline - 1);
        var lines = new List<string>();
        for (var row = 0; row <= baseline + below; row++)
        {
            var line = new StringBuilder();
            foreach (var layout in layouts)
            {
                var sourceRow = row - baseline + layout.Baseline;
                if (sourceRow >= 0 && sourceRow < layout.Lines.Count)
                {
                    line.Append(PadLayoutLine(layout.Lines[sourceRow], layout.Width));
                }
                else
                {
                    line.Append(' ', layout.Width);
                }
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return new Layout(lines, layouts.Sum(layout => layout.Width), baseline);
    }

    private sealed class LatexParser
    {
        private readonly string _source;
        private readonly List<LayoutNode> _layoutNodes;
        private readonly bool _display;
        private int _position;
        private bool _supported = true;
        private bool _stackFractions = true;

        public LatexParser(string source, List<LayoutNode> layoutNodes, bool display)
        {
            _source = source;
            _layoutNodes = layoutNodes;
            _display = display;
        }

        public string? Render()
        {
            var rendered = ParseSequence();
            if (!_supported || _position != _source.Length)
            {
                return null;
            }

            return NormalizeOutput(rendered);
        }

        private string ParseSequence(char? endCharacter = null)
        {
            var result = new StringBuilder();
            while (_position < _source.Length)
            {
                var character = _source[_position];
                if (endCharacter is not null && character == endCharacter.Value)
                {
                    _position++;
                    return result.ToString();
                }

                if (character == '}')
                {
                    _supported = false;
                    return result.ToString();
                }

                if (character == '{')
                {
                    _position++;
                    result.Append(ParseSequence('}'));
                    continue;
                }

                if (character == '\\')
                {
                    var command = ParseCommand();
                    if (command == _negativeSpace)
                    {
                        TrimEnd(result);
                        if (EndsWith(result, _namedOperatorEnd))
                        {
                            result.Length -= _namedOperatorEnd.Length;
                        }
                    }
                    else
                    {
                        result.Append(command);
                    }

                    continue;
                }

                if (character is '^' or '_')
                {
                    _position++;
                    TrimEnd(result);
                    var script = FormatScript(ParseRequiredArgument(false), character == '_');
                    if (EndsWith(result, _namedOperatorEnd))
                    {
                        result.Length -= _namedOperatorEnd.Length;
                        result.Append(script);
                        result.Append(_namedOperatorEnd);
                    }
                    else
                    {
                        result.Append(script);
                    }

                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    result.Append(ParseWhitespace());
                    continue;
                }

                if (character is '=' or '<' or '>')
                {
                    TrimEnd(result);
                    result.Append(' ').Append(character).Append(' ');
                    _position++;
                    continue;
                }

                if (character == '&')
                {
                    _position++;
                    continue;
                }

                if (character == '~')
                {
                    _position++;
                    result.Append(' ');
                    continue;
                }

                if (character == '.' && TryGetTrailingLayoutNodeIndex(result.ToString(), out var matrixIndex) &&
                    matrixIndex >= 0 && matrixIndex < _layoutNodes.Count && _layoutNodes[matrixIndex] is MatrixNode matrix)
                {
                    var lastLine = matrix.Lines.Count - 1;
                    if (lastLine >= 0)
                    {
                        matrix.Lines[lastLine] += character;
                    }

                    _position++;
                    continue;
                }

                result.Append(character);
                _position++;
            }

            if (endCharacter is not null)
            {
                _supported = false;
            }

            return result.ToString();
        }

        private string ParseWhitespace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }

            return " ";
        }

        private string ParseCommand()
        {
            _position++;
            if (_position >= _source.Length)
            {
                _supported = false;
                return string.Empty;
            }

            var first = _source[_position];
            if (first is '\n' or '\r')
            {
                _position++;
                if (first == '\r' && _position < _source.Length && _source[_position] == '\n')
                {
                    _position++;
                }

                return " ";
            }

            string command;
            if (IsAsciiLetter(first))
            {
                var start = _position;
                while (_position < _source.Length && IsAsciiLetter(_source[_position]))
                {
                    _position++;
                }

                command = _source[start.._position];
            }
            else
            {
                command = first.ToString();
                _position++;
            }

            if (command == "\\")
            {
                return "\n";
            }

            if (_spacingCommands.Contains(command))
            {
                return " ";
            }

            if (_negativeSpacingCommands.Contains(command))
            {
                return _negativeSpace;
            }

            if (_ignoredCommands.Contains(command))
            {
                return string.Empty;
            }

            if (command is "{" or "}" or "$" or "%" or "#" or "_" or "&")
            {
                return command;
            }

            if (command == "|")
            {
                return "‖";
            }

            if (command == "not")
            {
                var value = ParseRequiredArgument(false).Trim();
                if (_negatedSymbols.TryGetValue(value, out var negated))
                {
                    return $" {negated} ";
                }

                var characters = value.EnumerateRunes().Select(static rune => rune.ToString()).ToArray();
                if (characters.Length == 0)
                {
                    _supported = false;
                    return string.Empty;
                }

                return $" {characters[0]}\u0338{string.Concat(characters.Skip(1))} ";
            }

            if (_limitOperators.Contains(command))
            {
                return ParseOperator(command, false, true, true);
            }

            if (_symbols.TryGetValue(command, out var symbol))
            {
                if (_displayLimitSymbols.Contains(command))
                {
                    return ParseOperator(symbol, true, true);
                }

                return command is "cdot" or "times" || _relationCommands.Contains(command) ? $" {symbol} " : symbol;
            }

            if (_namedOperators.Contains(command))
            {
                return _namedOperatorStart + command + _namedOperatorEnd;
            }

            if (_sizeCommands.Contains(command))
            {
                return string.Empty;
            }

            if (command is "left" or "middle" or "right")
            {
                if (_position < _source.Length && _source[_position] == '.')
                {
                    _position++;
                }

                return string.Empty;
            }

            if (command is "frac" or "dfrac" or "tfrac")
            {
                var shouldStack = _display && _stackFractions && command != "tfrac";
                var numerator = ParseRequiredArgument(!shouldStack);
                var denominator = ParseRequiredArgument(!shouldStack);
                if (shouldStack)
                {
                    var index = _layoutNodes.Count;
                    _layoutNodes.Add(new FractionNode(NormalizeOutput(numerator), NormalizeOutput(denominator)));
                    return _layoutMarkerStart + index + _layoutMarkerEnd;
                }

                return FormatFraction(numerator, denominator);
            }

            if (command == "sqrt")
            {
                var degree = ParseOptionalArgument()?.Trim();
                var value = ParseRequiredArgument();
                if (degree is null or "2")
                {
                    return FormatRoot(value);
                }

                if (degree == "3")
                {
                    return FormatRoot(value, "∛");
                }

                if (degree == "4")
                {
                    return FormatRoot(value, "∜");
                }

                return FormatScript(degree, false) + FormatRoot(value);
            }

            if (command is "boxed" or "fbox")
            {
                return $"[{ParseRequiredArgument().Trim()}]";
            }

            if (command is "binom" or "dbinom" or "tbinom")
            {
                return $"({ParseRequiredArgument()} choose {ParseRequiredArgument()})";
            }

            if (_accents.TryGetValue(command, out var accent))
            {
                var value = ParseRequiredArgument();
                return value.EnumerateRunes().Count() == 1 ? value + accent : $"{command}({value})";
            }

            if (command == "mathbb")
            {
                var value = ParseRequiredArgument();
                return string.Concat(value.EnumerateRunes().Select(rune => _blackboard.TryGetValue(rune.ToString(), out var mapped) ? mapped : rune.ToString()));
            }

            if (command == "operatorname")
            {
                var starred = _position < _source.Length && _source[_position] == '*';
                if (starred)
                {
                    _position++;
                }

                var @operator = NormalizeOutput(ParseRequiredArgument()).Trim();
                return ParseOperator(@operator, false, starred, true);
            }

            if (command is "mod" or "bmod")
            {
                return " mod ";
            }

            if (command is "pmod" or "pod")
            {
                var value = ParseRequiredArgument().Trim();
                return command == "pmod" ? $" (mod {value})" : $" ({value})";
            }

            if (command is "overset" or "stackrel")
            {
                var upper = ParseRequiredArgument();
                var value = ParseRequiredArgument().Trim();
                return value + FormatScript(upper, false);
            }

            if (command == "underset")
            {
                var lower = ParseRequiredArgument();
                var value = ParseRequiredArgument().Trim();
                return value + FormatScript(lower, true);
            }

            if (_plainWrappers.Contains(command))
            {
                var value = ParseRequiredArgument();
                return command.StartsWith("text", StringComparison.Ordinal) || command == "mbox" ? value : value.Trim();
            }

            if (command == "begin")
            {
                return ParseEnvironment();
            }

            if (command == "end")
            {
                _supported = false;
                return string.Empty;
            }

            _supported = false;
            return "\\" + command;
        }

        private string ParseOperator(string @operator, bool inlineLowerAsScript, bool displayLimits, bool spaced = false)
        {
            var useDisplayLimits = displayLimits;
            var modifierPosition = _position;
            while (modifierPosition < _source.Length && _source[modifierPosition] is ' ' or '\t')
            {
                modifierPosition++;
            }

            var modifier = _operatorModifier.Match(_source[modifierPosition..]);
            if (modifier.Success)
            {
                useDisplayLimits = modifier.Groups[1].Value == "limits";
                _position = modifierPosition + modifier.Length;
            }

            string? lower = null;
            string? upper = null;
            while (true)
            {
                var scriptPosition = _position;
                while (scriptPosition < _source.Length && _source[scriptPosition] is ' ' or '\t')
                {
                    scriptPosition++;
                }

                if (scriptPosition >= _source.Length || _source[scriptPosition] is not ('_' or '^'))
                {
                    break;
                }

                var kind = _source[scriptPosition];
                _position = scriptPosition + 1;
                var value = NormalizeOutput(ParseRequiredArgument(false)).Replace(" ", string.Empty, StringComparison.Ordinal);
                if (kind == '_')
                {
                    if (lower is not null)
                    {
                        _supported = false;
                    }

                    lower = value;
                }
                else
                {
                    if (upper is not null)
                    {
                        _supported = false;
                    }

                    upper = value;
                }
            }

            if (_display && useDisplayLimits && (lower is not null || upper is not null))
            {
                var index = _layoutNodes.Count;
                _layoutNodes.Add(new OperatorNode(@operator, lower, upper));
                return _layoutMarkerStart + index + _layoutMarkerEnd;
            }

            var rendered = @operator;
            if (lower is not null)
            {
                rendered += inlineLowerAsScript ? FormatScript(lower, true) : $"[{lower}]";
            }

            if (upper is not null)
            {
                rendered += FormatScript(upper, false);
            }

            return spaced ? $" {rendered} " : rendered;
        }

        private string ParseRequiredArgument(bool stackFractions = true)
        {
            var previousStackFractions = _stackFractions;
            _stackFractions = previousStackFractions && stackFractions;
            var value = ParseRequiredArgumentValue();
            _stackFractions = previousStackFractions;
            return value;
        }

        private string ParseRequiredArgumentValue()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }

            if (_position >= _source.Length)
            {
                _supported = false;
                return string.Empty;
            }

            if (_source[_position] == '{')
            {
                _position++;
                return ParseSequence('}');
            }

            if (_source[_position] == '\\')
            {
                return ParseCommand();
            }

            var value = _source[_position].ToString();
            _position++;
            return value;
        }

        private string? ParseOptionalArgument()
        {
            while (_position < _source.Length && _source[_position] is ' ' or '\t')
            {
                _position++;
            }

            if (_position >= _source.Length || _source[_position] != '[')
            {
                return null;
            }

            var end = _source.IndexOf(']', _position + 1);
            if (end < 0)
            {
                _supported = false;
                return null;
            }

            var value = _source[(_position + 1)..end];
            _position = end + 1;
            return RenderNested(value);
        }

        private string? ReadRawGroup()
        {
            while (_position < _source.Length && _source[_position] is ' ' or '\t')
            {
                _position++;
            }

            if (_position >= _source.Length || _source[_position] != '{')
            {
                _supported = false;
                return null;
            }

            var start = ++_position;
            var depth = 1;
            while (_position < _source.Length)
            {
                var character = _source[_position];
                if (character == '\\')
                {
                    _position += 2;
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }

                if (character == '}')
                {
                    depth--;
                }

                if (depth == 0)
                {
                    var value = _source[start.._position];
                    _position++;
                    return value;
                }

                _position++;
            }

            _supported = false;
            return null;
        }

        private static string[] SplitEnvironmentRows(string body) => _environmentRowBreak.Split(body);

        private string ParseEnvironment()
        {
            var environment = ReadRawGroup();
            if (string.IsNullOrEmpty(environment))
            {
                return string.Empty;
            }

            var endMarker = $"\\end{{{environment}}}";
            var end = _source.IndexOf(endMarker, _position, StringComparison.Ordinal);
            if (end < 0)
            {
                _supported = false;
                return string.Empty;
            }

            var body = _source[_position..end];
            _position = end + endMarker.Length;

            if (environment is "equation" or "equation*" or "displaymath")
            {
                return RenderNested(body).Trim();
            }

            if (environment is "aligned" or "align" or "align*" or "alignedat" or "alignat" or "alignat*" or "gather" or "gathered" or "multline" or "multline*" or "split")
            {
                var alignedAt = environment is "alignedat" or "alignat" or "alignat*";
                var alignedBody = alignedAt ? _leadingEnvironmentArraySpec.Replace(body, string.Empty) : body;
                return string.Join(
                    '\n',
                    SplitEnvironmentRows(alignedBody)
                        .Select(row =>
                        {
                            var cells = row.Split('&');
                            var source = alignedAt
                                ? string.Join(' ', Enumerable.Range(0, (cells.Length + 1) / 2).Select(index => string.Concat(cells.Skip(index * 2).Take(2))))
                                : string.Concat(cells);
                            return RenderNested(source).Trim();
                        })
                        .Where(static row => row.Length > 0));
            }

            if (environment is "cases" or "cases*")
            {
                var rows = SplitEnvironmentRows(body)
                    .Select(row => row.Split('&').Select(cell => RenderNested(cell, false).Trim()).ToArray())
                    .Where(row => row.Any(static cell => cell.Length > 0))
                    .ToArray();
                return string.Join(
                    '\n',
                    rows.Select((row, index) =>
                    {
                        var value = _trailingComma.Replace(row.ElementAtOrDefault(0) ?? string.Empty, string.Empty);
                        var condition = row.ElementAtOrDefault(1) ?? string.Empty;
                        var delimiter = index == 0 ? "⎧" : index == rows.Length - 1 ? "⎩" : "⎨";
                        var conditionPrefix = _conditionPrefix.IsMatch(condition) ? " " : " if ";
                        return $"{delimiter} {value}{(condition.Length > 0 ? conditionPrefix + condition : string.Empty)}";
                    }));
            }

            if (environment is "array" or "matrix" or "smallmatrix" or "pmatrix" or "bmatrix" or "Bmatrix" or "vmatrix" or "Vmatrix")
            {
                var matrixBody = environment == "array" ? _leadingEnvironmentArraySpec.Replace(body, string.Empty) : body;
                return RenderMatrix(environment, matrixBody);
            }

            _supported = false;
            return body;
        }

        private string RenderMatrix(string environment, string body)
        {
            var matrix = SplitEnvironmentRows(body)
                .Select(row => row.Split('&').Select(cell => RenderNested(cell, false).Trim()).ToArray())
                .Where(row => row.Any(static cell => cell.Length > 0))
                .ToArray();
            var columnCount = matrix.Length == 0 ? 0 : matrix.Max(row => row.Length);
            var columnWidths = Enumerable.Range(0, columnCount)
                .Select(column => matrix.Length == 0 ? 0 : matrix.Max(row => TextMeasurement.VisibleWidth(row.ElementAtOrDefault(column) ?? string.Empty)))
                .ToArray();
            var rows = matrix.Select(row => string.Join(
                " │ ",
                Enumerable.Range(0, columnCount).Select(column =>
                {
                    var cell = row.ElementAtOrDefault(column) ?? string.Empty;
                    return cell + string.Concat(Enumerable.Repeat(_protectedSpace, Math.Max(0, columnWidths[column] - TextMeasurement.VisibleWidth(cell))));
                }))).ToArray();

            string[] lines;
            if (environment is "array" or "matrix" or "smallmatrix")
            {
                lines = rows;
            }
            else
            {
                var delimiters = environment switch
                {
                    "pmatrix" => ("⎛", "⎞", "⎜", "⎟", "⎝", "⎠"),
                    "bmatrix" => ("⎡", "⎤", "⎢", "⎥", "⎣", "⎦"),
                    "Bmatrix" => ("⎧", "⎫", "⎨", "⎬", "⎩", "⎭"),
                    "vmatrix" => ("│", "│", "│", "│", "│", "│"),
                    "Vmatrix" => ("║", "║", "║", "║", "║", "║"),
                    _ => default,
                };
                if (delimiters == default)
                {
                    _supported = false;
                    return string.Join('\n', rows);
                }

                lines = rows.Select((row, index) =>
                {
                    var left = index == 0 ? delimiters.Item1 : index == rows.Length - 1 ? delimiters.Item5 : delimiters.Item3;
                    var right = index == 0 ? delimiters.Item2 : index == rows.Length - 1 ? delimiters.Item6 : delimiters.Item4;
                    return $"{left} {row} {right}";
                }).ToArray();
            }

            if (lines.Length <= 1)
            {
                return lines.ElementAtOrDefault(0) ?? string.Empty;
            }

            var index = _layoutNodes.Count;
            _layoutNodes.Add(new MatrixNode(lines.ToList(), 0));
            return _layoutMarkerStart + index + _layoutMarkerEnd;
        }

        private string RenderNested(string source, bool stackFractions = true)
        {
            var rendered = new LatexParser(source, _layoutNodes, _display && stackFractions).Render();
            if (rendered is null)
            {
                _supported = false;
                return source;
            }

            return rendered;
        }
    }

    private abstract record LayoutNode;

    private sealed record FractionNode(string Numerator, string Denominator) : LayoutNode;

    private sealed record OperatorNode(string Operator, string? Lower, string? Upper) : LayoutNode;

    private sealed record MatrixNode(List<string> Lines, int Baseline) : LayoutNode;

    private sealed record Layout(IReadOnlyList<string> Lines, int Width, int Baseline);

    private readonly record struct LayoutMarker(int Index, int End, int NodeIndex);

    private static bool IsAsciiLetter(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void TrimEnd(StringBuilder value)
    {
        while (value.Length > 0 && char.IsWhiteSpace(value[^1]))
        {
            value.Length--;
        }
    }

    private static bool EndsWith(StringBuilder value, string suffix) => value.Length >= suffix.Length && value.ToString().EndsWith(suffix, StringComparison.Ordinal);
}
