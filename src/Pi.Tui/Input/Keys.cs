using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Validated representation of a Pi terminal key identifier.</summary>
public readonly record struct KeyId
{
    /// <summary>Creates a validated key identifier from its string form.</summary>
    public static KeyId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!KeyIdentifierParsing.TryParse(value, out _))
        {
            throw new ArgumentException($"Invalid key identifier: {value}", nameof(value));
        }

        return new KeyId(value);
    }

    /// <summary>Attempts to validate a key identifier without throwing.</summary>
    public static bool TryParse(string? value, out KeyId keyId)
    {
        if (value is not null && KeyIdentifierParsing.TryParse(value, out _))
        {
            keyId = new KeyId(value);
            return true;
        }

        keyId = default;
        return false;
    }

    /// <summary>The original string form of the key identifier.</summary>
    public string Value { get; }

    private KeyId(string value) => Value = value;

    /// <summary>Returns the key identifier's string form.</summary>
    public override string ToString() => Value;

    /// <summary>Converts a validated key identifier to its string form.</summary>
    public static implicit operator string(KeyId keyId) => keyId.Value;

    /// <summary>Validates and converts a string to a key identifier.</summary>
    public static explicit operator KeyId(string value) => Parse(value);
}

/// <summary>Runtime event types reported by the Kitty keyboard protocol.</summary>
public enum KeyEventType
{
    /// <summary>A key press event.</summary>
    Press,

    /// <summary>A key repeat event.</summary>
    Repeat,

    /// <summary>A key release event.</summary>
    Release,
}

/// <summary>Helpers for constructing Pi terminal key identifier strings.</summary>
public static class Key
{
    /// <summary>Escape key.</summary>
    public const string Escape = "escape";

    /// <summary>Escape alias.</summary>
    public const string Esc = "esc";

    /// <summary>Enter key.</summary>
    public const string Enter = "enter";

    /// <summary>Return key alias.</summary>
    public const string Return = "return";

    /// <summary>Tab key.</summary>
    public const string Tab = "tab";

    /// <summary>Space key.</summary>
    public const string Space = "space";

    /// <summary>Backspace key.</summary>
    public const string Backspace = "backspace";

    /// <summary>Delete key.</summary>
    public const string Delete = "delete";

    /// <summary>Insert key.</summary>
    public const string Insert = "insert";

    /// <summary>Clear key.</summary>
    public const string Clear = "clear";

    /// <summary>Home key.</summary>
    public const string Home = "home";

    /// <summary>End key.</summary>
    public const string End = "end";

    /// <summary>Page-up key.</summary>
    public const string PageUp = "pageUp";

    /// <summary>Page-down key.</summary>
    public const string PageDown = "pageDown";

    /// <summary>Up-arrow key.</summary>
    public const string Up = "up";

    /// <summary>Down-arrow key.</summary>
    public const string Down = "down";

    /// <summary>Left-arrow key.</summary>
    public const string Left = "left";

    /// <summary>Right-arrow key.</summary>
    public const string Right = "right";

    /// <summary>Function key F1.</summary>
    public const string F1 = "f1";

    /// <summary>Function key F2.</summary>
    public const string F2 = "f2";

    /// <summary>Function key F3.</summary>
    public const string F3 = "f3";

    /// <summary>Function key F4.</summary>
    public const string F4 = "f4";

    /// <summary>Function key F5.</summary>
    public const string F5 = "f5";

    /// <summary>Function key F6.</summary>
    public const string F6 = "f6";

    /// <summary>Function key F7.</summary>
    public const string F7 = "f7";

    /// <summary>Function key F8.</summary>
    public const string F8 = "f8";

    /// <summary>Function key F9.</summary>
    public const string F9 = "f9";

    /// <summary>Function key F10.</summary>
    public const string F10 = "f10";

    /// <summary>Function key F11.</summary>
    public const string F11 = "f11";

    /// <summary>Function key F12.</summary>
    public const string F12 = "f12";

    /// <summary>Backtick key.</summary>
    public const string Backtick = "`";

    /// <summary>Hyphen key.</summary>
    public const string Hyphen = "-";

    /// <summary>Equals key.</summary>
    public new const string Equals = "=";

    /// <summary>Left-bracket key.</summary>
    public const string LeftBracket = "[";

    /// <summary>Right-bracket key.</summary>
    public const string RightBracket = "]";

    /// <summary>Backslash key.</summary>
    public const string Backslash = "\\";

    /// <summary>Semicolon key.</summary>
    public const string Semicolon = ";";

    /// <summary>Quote key.</summary>
    public const string Quote = "'";

    /// <summary>Comma key.</summary>
    public const string Comma = ",";

    /// <summary>Period key.</summary>
    public const string Period = ".";

    /// <summary>Slash key.</summary>
    public const string Slash = "/";

    /// <summary>Exclamation key.</summary>
    public const string Exclamation = "!";

    /// <summary>At-sign key.</summary>
    public const string At = "@";

    /// <summary>Hash key.</summary>
    public const string Hash = "#";

    /// <summary>Dollar key.</summary>
    public const string Dollar = "$";

    /// <summary>Percent key.</summary>
    public const string Percent = "%";

    /// <summary>Caret key.</summary>
    public const string Caret = "^";

    /// <summary>Ampersand key.</summary>
    public const string Ampersand = "&";

    /// <summary>Asterisk key.</summary>
    public const string Asterisk = "*";

    /// <summary>Left-parenthesis key.</summary>
    public const string LeftParenthesis = "(";

    /// <summary>Right-parenthesis key.</summary>
    public const string RightParenthesis = ")";

    /// <summary>Underscore key.</summary>
    public const string Underscore = "_";

    /// <summary>Plus key.</summary>
    public const string Plus = "+";

    /// <summary>Pipe key.</summary>
    public const string Pipe = "|";

    /// <summary>Tilde key.</summary>
    public const string Tilde = "~";

    /// <summary>Left-brace key.</summary>
    public const string LeftBrace = "{";

    /// <summary>Right-brace key.</summary>
    public const string RightBrace = "}";

    /// <summary>Colon key.</summary>
    public const string Colon = ":";

    /// <summary>Less-than key.</summary>
    public const string LessThan = "<";

    /// <summary>Greater-than key.</summary>
    public const string GreaterThan = ">";

    /// <summary>Question-mark key.</summary>
    public const string Question = "?";

    /// <summary>Creates a key identifier with Control.</summary>
    public static string Ctrl(string key) => $"ctrl+{key}";

    /// <summary>Creates a key identifier with Shift.</summary>
    public static string Shift(string key) => $"shift+{key}";

    /// <summary>Creates a key identifier with Alt.</summary>
    public static string Alt(string key) => $"alt+{key}";

    /// <summary>Creates a key identifier with Super.</summary>
    public static string Super(string key) => $"super+{key}";

    /// <summary>Creates a key identifier with Control and Shift.</summary>
    public static string CtrlShift(string key) => $"ctrl+shift+{key}";

    /// <summary>Creates a key identifier with Shift and Control.</summary>
    public static string ShiftCtrl(string key) => $"shift+ctrl+{key}";

    /// <summary>Creates a key identifier with Control and Alt.</summary>
    public static string CtrlAlt(string key) => $"ctrl+alt+{key}";

    /// <summary>Creates a key identifier with Alt and Control.</summary>
    public static string AltCtrl(string key) => $"alt+ctrl+{key}";

    /// <summary>Creates a key identifier with Shift and Alt.</summary>
    public static string ShiftAlt(string key) => $"shift+alt+{key}";

    /// <summary>Creates a key identifier with Alt and Shift.</summary>
    public static string AltShift(string key) => $"alt+shift+{key}";

    /// <summary>Creates a key identifier with Control and Super.</summary>
    public static string CtrlSuper(string key) => $"ctrl+super+{key}";

    /// <summary>Creates a key identifier with Super and Control.</summary>
    public static string SuperCtrl(string key) => $"super+ctrl+{key}";

    /// <summary>Creates a key identifier with Shift and Super.</summary>
    public static string ShiftSuper(string key) => $"shift+super+{key}";

    /// <summary>Creates a key identifier with Super and Shift.</summary>
    public static string SuperShift(string key) => $"super+shift+{key}";

    /// <summary>Creates a key identifier with Alt and Super.</summary>
    public static string AltSuper(string key) => $"alt+super+{key}";

    /// <summary>Creates a key identifier with Super and Alt.</summary>
    public static string SuperAlt(string key) => $"super+alt+{key}";

    /// <summary>Creates a key identifier with Control, Shift, and Alt.</summary>
    public static string CtrlShiftAlt(string key) => $"ctrl+shift+alt+{key}";

    /// <summary>Creates a key identifier with Control, Shift, and Super.</summary>
    public static string CtrlShiftSuper(string key) => $"ctrl+shift+super+{key}";
}

/// <summary>Keyboard sequence parsing and matching for legacy and Kitty terminals.</summary>
public static class Keys
{
    private const int _shiftModifier = 1;
    private const int _altModifier = 2;
    private const int _ctrlModifier = 4;
    private const int _superModifier = 8;
    private const int _lockMask = 64 + 128;
    private const int _escapeCodepoint = 27;
    private const int _tabCodepoint = 9;
    private const int _enterCodepoint = 13;
    private const int _spaceCodepoint = 32;
    private const int _backspaceCodepoint = 127;
    private const int _keypadEnterCodepoint = 57414;

    private const int _upCodepoint = -1;
    private const int _downCodepoint = -2;
    private const int _rightCodepoint = -3;
    private const int _leftCodepoint = -4;
    private const int _deleteCodepoint = -10;
    private const int _insertCodepoint = -11;
    private const int _pageUpCodepoint = -12;
    private const int _pageDownCodepoint = -13;
    private const int _homeCodepoint = -14;
    private const int _endCodepoint = -15;

    private static readonly HashSet<char> _symbolKeys =
    [
        '`', '-', '=', '[', ']', '\\', ';', '\'', ',', '.', '/', '!', '@', '#', '$', '%', '^', '&', '*', '(', ')',
        '_', '+', '|', '~', '{', '}', ':', '<', '>', '?',
    ];

    private static readonly Dictionary<int, int> _kittyFunctionalKeyEquivalents = new()
    {
        [57399] = '0',
        [57400] = '1',
        [57401] = '2',
        [57402] = '3',
        [57403] = '4',
        [57404] = '5',
        [57405] = '6',
        [57406] = '7',
        [57407] = '8',
        [57408] = '9',
        [57409] = '.',
        [57410] = '/',
        [57411] = '*',
        [57412] = '-',
        [57413] = '+',
        [57415] = '=',
        [57416] = ',',
        [57417] = _leftCodepoint,
        [57418] = _rightCodepoint,
        [57419] = _upCodepoint,
        [57420] = _downCodepoint,
        [57421] = _pageUpCodepoint,
        [57422] = _pageDownCodepoint,
        [57423] = _homeCodepoint,
        [57424] = _endCodepoint,
        [57425] = _insertCodepoint,
        [57426] = _deleteCodepoint,
    };

    private static readonly Dictionary<string, string[]> _legacyKeySequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\x1b[A", "\x1bOA"],
        ["down"] = ["\x1b[B", "\x1bOB"],
        ["right"] = ["\x1b[C", "\x1bOC"],
        ["left"] = ["\x1b[D", "\x1bOD"],
        ["home"] = ["\x1b[H", "\x1bOH", "\x1b[1~", "\x1b[7~"],
        ["end"] = ["\x1b[F", "\x1bOF", "\x1b[4~", "\x1b[8~"],
        ["insert"] = ["\x1b[2~"],
        ["delete"] = ["\x1b[3~"],
        ["pageUp"] = ["\x1b[5~", "\x1b[[5~"],
        ["pageDown"] = ["\x1b[6~", "\x1b[[6~"],
        ["clear"] = ["\x1b[E", "\x1bOE"],
        ["f1"] = ["\x1bOP", "\x1b[11~", "\x1b[[A"],
        ["f2"] = ["\x1bOQ", "\x1b[12~", "\x1b[[B"],
        ["f3"] = ["\x1bOR", "\x1b[13~", "\x1b[[C"],
        ["f4"] = ["\x1bOS", "\x1b[14~", "\x1b[[D"],
        ["f5"] = ["\x1b[15~", "\x1b[[E"],
        ["f6"] = ["\x1b[17~"],
        ["f7"] = ["\x1b[18~"],
        ["f8"] = ["\x1b[19~"],
        ["f9"] = ["\x1b[20~"],
        ["f10"] = ["\x1b[21~"],
        ["f11"] = ["\x1b[23~"],
        ["f12"] = ["\x1b[24~"],
    };

    private static readonly Dictionary<string, string[]> _legacyShiftSequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\x1b[a"],
        ["down"] = ["\x1b[b"],
        ["right"] = ["\x1b[c"],
        ["left"] = ["\x1b[d"],
        ["clear"] = ["\x1b[e"],
        ["insert"] = ["\x1b[2$"],
        ["delete"] = ["\x1b[3$"],
        ["pageUp"] = ["\x1b[5$"],
        ["pageDown"] = ["\x1b[6$"],
        ["home"] = ["\x1b[7$"],
        ["end"] = ["\x1b[8$"],
    };

    private static readonly Dictionary<string, string[]> _legacyCtrlSequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\x1bOa"],
        ["down"] = ["\x1bOb"],
        ["right"] = ["\x1bOc"],
        ["left"] = ["\x1bOd"],
        ["clear"] = ["\x1bOe"],
        ["insert"] = ["\x1b[2^"],
        ["delete"] = ["\x1b[3^"],
        ["pageUp"] = ["\x1b[5^"],
        ["pageDown"] = ["\x1b[6^"],
        ["home"] = ["\x1b[7^"],
        ["end"] = ["\x1b[8^"],
    };

    private static readonly Dictionary<string, string> _legacySequenceKeyIds = new(StringComparer.Ordinal)
    {
        ["\x1bOA"] = "up",
        ["\x1bOB"] = "down",
        ["\x1bOC"] = "right",
        ["\x1bOD"] = "left",
        ["\x1bOH"] = "home",
        ["\x1bOF"] = "end",
        ["\x1b[E"] = "clear",
        ["\x1bOE"] = "clear",
        ["\x1bOe"] = "ctrl+clear",
        ["\x1b[e"] = "shift+clear",
        ["\x1b[2~"] = "insert",
        ["\x1b[2$"] = "shift+insert",
        ["\x1b[2^"] = "ctrl+insert",
        ["\x1b[3$"] = "shift+delete",
        ["\x1b[3^"] = "ctrl+delete",
        ["\x1b[[5~"] = "pageUp",
        ["\x1b[[6~"] = "pageDown",
        ["\x1b[a"] = "shift+up",
        ["\x1b[b"] = "shift+down",
        ["\x1b[c"] = "shift+right",
        ["\x1b[d"] = "shift+left",
        ["\x1bOa"] = "ctrl+up",
        ["\x1bOb"] = "ctrl+down",
        ["\x1bOc"] = "ctrl+right",
        ["\x1bOd"] = "ctrl+left",
        ["\x1b[5$"] = "shift+pageUp",
        ["\x1b[6$"] = "shift+pageDown",
        ["\x1b[7$"] = "shift+home",
        ["\x1b[8$"] = "shift+end",
        ["\x1b[5^"] = "ctrl+pageUp",
        ["\x1b[6^"] = "ctrl+pageDown",
        ["\x1b[7^"] = "ctrl+home",
        ["\x1b[8^"] = "ctrl+end",
        ["\x1bOP"] = "f1",
        ["\x1bOQ"] = "f2",
        ["\x1bOR"] = "f3",
        ["\x1bOS"] = "f4",
        ["\x1b[11~"] = "f1",
        ["\x1b[12~"] = "f2",
        ["\x1b[13~"] = "f3",
        ["\x1b[14~"] = "f4",
        ["\x1b[[A"] = "f1",
        ["\x1b[[B"] = "f2",
        ["\x1b[[C"] = "f3",
        ["\x1b[[D"] = "f4",
        ["\x1b[[E"] = "f5",
        ["\x1b[15~"] = "f5",
        ["\x1b[17~"] = "f6",
        ["\x1b[18~"] = "f7",
        ["\x1b[19~"] = "f8",
        ["\x1b[20~"] = "f9",
        ["\x1b[21~"] = "f10",
        ["\x1b[23~"] = "f11",
        ["\x1b[24~"] = "f12",
        ["\u001bb"] = "alt+left",
        ["\u001bf"] = "alt+right",
        ["\x1bp"] = "alt+up",
        ["\x1bn"] = "alt+down",
    };

    private static readonly Regex _kittyCsiURegex = new(
        @"^\x1b\[([0-9]+)(?::([0-9]*))?(?::([0-9]+))?(?:;([0-9]+))?(?::([0-9]+))?u$",
        RegexOptions.CultureInvariant);

    private static readonly Regex _arrowRegex = new(@"^\x1b\[1;([0-9]+)(?::([0-9]+))?([ABCD])$", RegexOptions.CultureInvariant);

    private static readonly Regex _functionalRegex = new(@"^\x1b\[([0-9]+)(?:;([0-9]+))?(?::([0-9]+))?~$", RegexOptions.CultureInvariant);

    private static readonly Regex _homeEndRegex = new(@"^\x1b\[1;([0-9]+)(?::([0-9]+))?([HF])$", RegexOptions.CultureInvariant);

    private static readonly Regex _modifyOtherKeysRegex = new(@"^\x1b\[27;([0-9]+);([0-9]+)~$", RegexOptions.CultureInvariant);

    private static int _kittyProtocolActive;

    /// <summary>Sets the process-wide Kitty keyboard protocol state.</summary>
    public static void SetKittyProtocolActive(bool active) => Volatile.Write(ref _kittyProtocolActive, active ? 1 : 0);

    /// <summary>Returns whether the process-wide Kitty keyboard protocol is active.</summary>
    public static bool IsKittyProtocolActive() => Volatile.Read(ref _kittyProtocolActive) != 0;

    /// <summary>Returns whether terminal data contains a Kitty key-release event marker.</summary>
    public static bool IsKeyRelease(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Contains("\x1b[200~", StringComparison.Ordinal))
        {
            return false;
        }

        return ContainsAny(data, ":3u", ":3~", ":3A", ":3B", ":3C", ":3D", ":3H", ":3F");
    }

    /// <summary>Returns whether terminal data contains a Kitty key-repeat event marker.</summary>
    public static bool IsKeyRepeat(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Contains("\x1b[200~", StringComparison.Ordinal))
        {
            return false;
        }

        return ContainsAny(data, ":2u", ":2~", ":2A", ":2B", ":2C", ":2D", ":2H", ":2F");
    }

    /// <summary>Matches raw terminal input against a string-form Pi key identifier.</summary>
    public static bool MatchesKey(string data, string keyId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(keyId);
        return KeyIdentifierParsing.TryParse(keyId, out var parsed) && MatchesParsedKey(data, parsed);
    }

    /// <summary>Matches raw terminal input against a validated Pi key identifier.</summary>
    public static bool MatchesKey(string data, KeyId keyId)
    {
        ArgumentNullException.ThrowIfNull(data);
        return KeyIdentifierParsing.TryParse(keyId.Value, out var parsed) && MatchesParsedKey(data, parsed);
    }

    /// <summary>Parses raw terminal input into a Pi key identifier when recognized.</summary>
    public static string? ParseKey(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var kitty = ParseKittySequence(data);
        if (kitty is not null)
        {
            return FormatParsedKey(kitty.Codepoint, kitty.Modifier, kitty.BaseLayoutKey);
        }

        var modifyOtherKeys = ParseModifyOtherKeysSequence(data);
        if (modifyOtherKeys is not null)
        {
            return FormatParsedKey(modifyOtherKeys.Codepoint, modifyOtherKeys.Modifier);
        }

        var kittyActive = IsKittyProtocolActive();
        if (kittyActive && (data == "\x1b\r" || data == "\n"))
        {
            return "shift+enter";
        }

        if (_legacySequenceKeyIds.TryGetValue(data, out var legacyKeyId))
        {
            return legacyKeyId;
        }

        if (data == "\x1b") return "escape";
        if (data == "\x1c") return "ctrl+\\";
        if (data == "\x1d") return "ctrl+]";
        if (data == "\x1f") return "ctrl+-";
        if (data == "\x1b\x1b") return "ctrl+alt+[";
        if (data == "\x1b\x1c") return "ctrl+alt+\\";
        if (data == "\x1b\x1d") return "ctrl+alt+]";
        if (data == "\x1b\x1f") return "ctrl+alt+-";
        if (data == "\t") return "tab";
        if (data == "\r" || (!kittyActive && data == "\n") || data == "\x1bOM") return "enter";
        if (data == "\x00") return "ctrl+space";
        if (data == " ") return "space";
        if (data == "\x7f") return "backspace";
        if (data == "\x08") return IsWindowsTerminalSession() ? "ctrl+backspace" : "backspace";
        if (data == "\x1b[Z") return "shift+tab";
        if (!kittyActive && data == "\x1b\r") return "alt+enter";
        if (!kittyActive && data == "\x1b ") return "alt+space";
        if (data == "\x1b\x7f" || data == "\x1b\b") return "alt+backspace";
        if (!kittyActive && data == "\u001bB") return "alt+left";
        if (!kittyActive && data == "\u001bF") return "alt+right";

        if (!kittyActive && data.Length == 2 && data[0] == '\x1b')
        {
            var code = data[1];
            if (code is >= '\u0001' and <= '\u001a')
            {
                return $"ctrl+alt+{(char)(code + 96)}";
            }

            var key = code.ToString();
            if ((code is >= 'a' and <= 'z') || (code is >= '0' and <= '9') || _symbolKeys.Contains(code))
            {
                return $"alt+{key}";
            }
        }

        if (data == "\x1b[A") return "up";
        if (data == "\x1b[B") return "down";
        if (data == "\x1b[C") return "right";
        if (data == "\x1b[D") return "left";
        if (data == "\x1b[H" || data == "\x1bOH") return "home";
        if (data == "\x1b[F" || data == "\x1bOF") return "end";
        if (data == "\x1b[3~") return "delete";
        if (data == "\x1b[5~") return "pageUp";
        if (data == "\x1b[6~") return "pageDown";

        if (data.Length == 1)
        {
            var code = data[0];
            if (code is >= '\u0001' and <= '\u001a')
            {
                return $"ctrl+{(char)(code + 96)}";
            }

            if (code is >= '\x20' and <= '\x7e')
            {
                return data;
            }
        }

        return null;
    }

    /// <summary>Decodes an unmodified or Shift-modified Kitty printable sequence.</summary>
    public static string? DecodeKittyPrintable(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var match = _kittyCsiURegex.Match(data);
        if (!match.Success || !TryGetInt(match, 1, out var codepoint)) return null;

        int? shiftedKey = TryGetOptionalInt(match, 2);
        var modValue = TryGetOptionalInt(match, 4) ?? 1;
        var modifier = modValue - 1;
        const int allowedModifiers = _shiftModifier | _lockMask;
        if ((modifier & ~allowedModifiers) != 0 || (modifier & (_altModifier | _ctrlModifier)) != 0) return null;

        var effectiveCodepoint = codepoint;
        if ((modifier & _shiftModifier) != 0 && shiftedKey.HasValue)
        {
            effectiveCodepoint = shiftedKey.Value;
        }

        effectiveCodepoint = NormalizeKittyFunctionalCodepoint(effectiveCodepoint);
        if (effectiveCodepoint < 32 || effectiveCodepoint > 0x10ffff) return null;

        try
        {
            return char.ConvertFromUtf32(effectiveCodepoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Decodes any supported printable modified-key sequence.</summary>
    public static string? DecodePrintableKey(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return DecodeKittyPrintable(data) ?? DecodeModifyOtherKeysPrintable(data);
    }

    private static bool MatchesParsedKey(string data, ParsedKeyIdentifier parsed)
    {
        var key = parsed.Key;
        var modifier = 0;
        if (parsed.Shift) modifier |= _shiftModifier;
        if (parsed.Alt) modifier |= _altModifier;
        if (parsed.Ctrl) modifier |= _ctrlModifier;
        if (parsed.Super) modifier |= _superModifier;

        switch (key)
        {
            case "escape":
            case "esc":
                return modifier == 0 &&
                       (data == "\x1b" || MatchesKittySequence(data, _escapeCodepoint, 0) ||
                        MatchesModifyOtherKeys(data, _escapeCodepoint, 0));

            case "space":
                if (!IsKittyProtocolActive())
                {
                    if (modifier == _ctrlModifier && data == "\x00") return true;
                    if (modifier == _altModifier && data == "\x1b ") return true;
                }

                if (modifier == 0)
                {
                    return data == " " || MatchesKittySequence(data, _spaceCodepoint, 0) ||
                           MatchesModifyOtherKeys(data, _spaceCodepoint, 0);
                }

                return MatchesKittySequence(data, _spaceCodepoint, modifier) ||
                       MatchesModifyOtherKeys(data, _spaceCodepoint, modifier);

            case "tab":
                if (modifier == _shiftModifier)
                {
                    return data == "\x1b[Z" || MatchesKittySequence(data, _tabCodepoint, _shiftModifier) ||
                           MatchesModifyOtherKeys(data, _tabCodepoint, _shiftModifier);
                }

                if (modifier == 0) return data == "\t" || MatchesKittySequence(data, _tabCodepoint, 0);
                return MatchesKittySequence(data, _tabCodepoint, modifier) ||
                       MatchesModifyOtherKeys(data, _tabCodepoint, modifier);

            case "enter":
            case "return":
                if (modifier == _shiftModifier)
                {
                    if (MatchesKittySequence(data, _enterCodepoint, _shiftModifier) ||
                        MatchesKittySequence(data, _keypadEnterCodepoint, _shiftModifier) ||
                        MatchesModifyOtherKeys(data, _enterCodepoint, _shiftModifier)) return true;

                    return IsKittyProtocolActive() && (data == "\x1b\r" || data == "\n");
                }

                if (modifier == _altModifier)
                {
                    if (MatchesKittySequence(data, _enterCodepoint, _altModifier) ||
                        MatchesKittySequence(data, _keypadEnterCodepoint, _altModifier) ||
                        MatchesModifyOtherKeys(data, _enterCodepoint, _altModifier)) return true;

                    return !IsKittyProtocolActive() && data == "\x1b\r";
                }

                if (modifier == 0)
                {
                    return data == "\r" || (!IsKittyProtocolActive() && data == "\n") || data == "\x1bOM" ||
                           MatchesKittySequence(data, _enterCodepoint, 0) ||
                           MatchesKittySequence(data, _keypadEnterCodepoint, 0);
                }

                return MatchesKittySequence(data, _enterCodepoint, modifier) ||
                       MatchesKittySequence(data, _keypadEnterCodepoint, modifier) ||
                       MatchesModifyOtherKeys(data, _enterCodepoint, modifier);

            case "backspace":
                if (modifier == _altModifier)
                {
                    return data == "\x1b\x7f" || data == "\x1b\b" ||
                           MatchesKittySequence(data, _backspaceCodepoint, _altModifier) ||
                           MatchesModifyOtherKeys(data, _backspaceCodepoint, _altModifier);
                }

                if (modifier == _ctrlModifier)
                {
                    return MatchesRawBackspace(data, _ctrlModifier) ||
                           MatchesKittySequence(data, _backspaceCodepoint, _ctrlModifier) ||
                           MatchesModifyOtherKeys(data, _backspaceCodepoint, _ctrlModifier);
                }

                if (modifier == 0)
                {
                    return MatchesRawBackspace(data, 0) || MatchesKittySequence(data, _backspaceCodepoint, 0) ||
                           MatchesModifyOtherKeys(data, _backspaceCodepoint, 0);
                }

                return MatchesKittySequence(data, _backspaceCodepoint, modifier) ||
                       MatchesModifyOtherKeys(data, _backspaceCodepoint, modifier);

            case "insert":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["insert"]) ||
                           MatchesKittySequence(data, _insertCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "insert", modifier)) return true;
                return MatchesKittySequence(data, _insertCodepoint, modifier);

            case "delete":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["delete"]) ||
                           MatchesKittySequence(data, _deleteCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "delete", modifier)) return true;
                return MatchesKittySequence(data, _deleteCodepoint, modifier);

            case "clear":
                return modifier == 0
                    ? MatchesLegacySequence(data, _legacyKeySequences["clear"])
                    : MatchesLegacyModifierSequence(data, "clear", modifier);

            case "home":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["home"]) ||
                           MatchesKittySequence(data, _homeCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "home", modifier)) return true;
                return MatchesKittySequence(data, _homeCodepoint, modifier);

            case "end":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["end"]) ||
                           MatchesKittySequence(data, _endCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "end", modifier)) return true;
                return MatchesKittySequence(data, _endCodepoint, modifier);

            case "pageup":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["pageUp"]) ||
                           MatchesKittySequence(data, _pageUpCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "pageUp", modifier)) return true;
                return MatchesKittySequence(data, _pageUpCodepoint, modifier);

            case "pagedown":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["pageDown"]) ||
                           MatchesKittySequence(data, _pageDownCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "pageDown", modifier)) return true;
                return MatchesKittySequence(data, _pageDownCodepoint, modifier);

            case "up":
                if (modifier == _altModifier)
                {
                    return data == "\x1bp" || MatchesKittySequence(data, _upCodepoint, _altModifier);
                }

                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["up"]) ||
                           MatchesKittySequence(data, _upCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "up", modifier)) return true;
                return MatchesKittySequence(data, _upCodepoint, modifier);

            case "down":
                if (modifier == _altModifier)
                {
                    return data == "\x1bn" || MatchesKittySequence(data, _downCodepoint, _altModifier);
                }

                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["down"]) ||
                           MatchesKittySequence(data, _downCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "down", modifier)) return true;
                return MatchesKittySequence(data, _downCodepoint, modifier);

            case "left":
                if (modifier == _altModifier)
                {
                    return data == "\x1b[1;3D" || (!IsKittyProtocolActive() && data == "\u001bB") ||
                           data == "\u001bb" || MatchesKittySequence(data, _leftCodepoint, _altModifier);
                }

                if (modifier == _ctrlModifier)
                {
                    return data == "\x1b[1;5D" || MatchesLegacyModifierSequence(data, "left", _ctrlModifier) ||
                           MatchesKittySequence(data, _leftCodepoint, _ctrlModifier);
                }

                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["left"]) ||
                           MatchesKittySequence(data, _leftCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "left", modifier)) return true;
                return MatchesKittySequence(data, _leftCodepoint, modifier);

            case "right":
                if (modifier == _altModifier)
                {
                    return data == "\x1b[1;3C" || (!IsKittyProtocolActive() && data == "\u001bF") ||
                           data == "\u001bf" || MatchesKittySequence(data, _rightCodepoint, _altModifier);
                }

                if (modifier == _ctrlModifier)
                {
                    return data == "\x1b[1;5C" || MatchesLegacyModifierSequence(data, "right", _ctrlModifier) ||
                           MatchesKittySequence(data, _rightCodepoint, _ctrlModifier);
                }

                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, _legacyKeySequences["right"]) ||
                           MatchesKittySequence(data, _rightCodepoint, 0);
                }

                if (MatchesLegacyModifierSequence(data, "right", modifier)) return true;
                return MatchesKittySequence(data, _rightCodepoint, modifier);

            case "f1":
            case "f2":
            case "f3":
            case "f4":
            case "f5":
            case "f6":
            case "f7":
            case "f8":
            case "f9":
            case "f10":
            case "f11":
            case "f12":
                return modifier == 0 && MatchesLegacySequence(data, _legacyKeySequences[key]);
        }

        if (key.Length == 1 && ((key[0] is >= 'a' and <= 'z') || IsDigitKey(key) || _symbolKeys.Contains(key[0])))
        {
            var codepoint = key[0];
            var rawCtrl = RawCtrlChar(key);
            var isLetter = key[0] is >= 'a' and <= 'z';
            var isDigit = IsDigitKey(key);
            var kittyActive = IsKittyProtocolActive();

            if (modifier == _ctrlModifier + _altModifier && !kittyActive && rawCtrl is not null && data == $"\x1b{rawCtrl}")
            {
                return true;
            }

            if (modifier == _altModifier && !kittyActive && (isLetter || isDigit || _symbolKeys.Contains(key[0])) &&
                data == $"\x1b{key}")
            {
                return true;
            }

            if (modifier == _ctrlModifier)
            {
                if (rawCtrl is not null && data == rawCtrl) return true;
                return MatchesKittySequence(data, codepoint, _ctrlModifier) ||
                       MatchesPrintableModifyOtherKeys(data, codepoint, _ctrlModifier);
            }

            if (modifier == _shiftModifier + _ctrlModifier)
            {
                return MatchesKittySequence(data, codepoint, _shiftModifier + _ctrlModifier) ||
                       MatchesPrintableModifyOtherKeys(data, codepoint, _shiftModifier + _ctrlModifier);
            }

            if (modifier == _shiftModifier)
            {
                if (isLetter && data == char.ToUpperInvariant(key[0]).ToString()) return true;
                return MatchesKittySequence(data, codepoint, _shiftModifier) ||
                       MatchesPrintableModifyOtherKeys(data, codepoint, _shiftModifier);
            }

            if (modifier != 0)
            {
                return MatchesKittySequence(data, codepoint, modifier) ||
                       MatchesPrintableModifyOtherKeys(data, codepoint, modifier);
            }

            return data == key || MatchesKittySequence(data, codepoint, 0);
        }

        return false;
    }

    private static ParsedKittySequence? ParseKittySequence(string data)
    {
        var csiUMatch = _kittyCsiURegex.Match(data);
        if (csiUMatch.Success && TryGetInt(csiUMatch, 1, out var codepoint))
        {
            var shiftedKey = TryGetOptionalInt(csiUMatch, 2);
            var baseLayoutKey = TryGetOptionalInt(csiUMatch, 3);
            var modValue = TryGetOptionalInt(csiUMatch, 4) ?? 1;
            var eventType = ParseEventType(TryGetOptionalString(csiUMatch, 5));
            return new ParsedKittySequence(codepoint, shiftedKey, baseLayoutKey, modValue - 1, eventType);
        }

        var arrowMatch = _arrowRegex.Match(data);
        if (arrowMatch.Success && TryGetInt(arrowMatch, 1, out var arrowModifier))
        {
            var arrowCodepoint = arrowMatch.Groups[3].Value switch
            {
                "A" => _upCodepoint,
                "B" => _downCodepoint,
                "C" => _rightCodepoint,
                "D" => _leftCodepoint,
                _ => throw new InvalidOperationException("Unexpected arrow sequence."),
            };
            return new ParsedKittySequence(
                arrowCodepoint,
                null,
                null,
                arrowModifier - 1,
                ParseEventType(TryGetOptionalString(arrowMatch, 2)));
        }

        var functionalMatch = _functionalRegex.Match(data);
        if (functionalMatch.Success && TryGetInt(functionalMatch, 1, out var keyNumber))
        {
            var functionalCodepoint = keyNumber switch
            {
                2 => _insertCodepoint,
                3 => _deleteCodepoint,
                5 => _pageUpCodepoint,
                6 => _pageDownCodepoint,
                7 => _homeCodepoint,
                8 => _endCodepoint,
                _ => (int?)null,
            };
            if (functionalCodepoint.HasValue)
            {
                return new ParsedKittySequence(
                    functionalCodepoint.Value,
                    null,
                    null,
                    (TryGetOptionalInt(functionalMatch, 2) ?? 1) - 1,
                    ParseEventType(TryGetOptionalString(functionalMatch, 3)));
            }
        }

        var homeEndMatch = _homeEndRegex.Match(data);
        if (homeEndMatch.Success && TryGetInt(homeEndMatch, 1, out var homeEndModifier))
        {
            var homeEndCodepoint = homeEndMatch.Groups[3].Value == "H" ? _homeCodepoint : _endCodepoint;
            return new ParsedKittySequence(
                homeEndCodepoint,
                null,
                null,
                homeEndModifier - 1,
                ParseEventType(TryGetOptionalString(homeEndMatch, 2)));
        }

        return null;
    }

    private static bool MatchesKittySequence(string data, int expectedCodepoint, int expectedModifier)
    {
        var parsed = ParseKittySequence(data);
        if (parsed is null) return false;

        var actualModifier = parsed.Modifier & ~_lockMask;
        var expectedModifierWithoutLocks = expectedModifier & ~_lockMask;
        if (actualModifier != expectedModifierWithoutLocks) return false;

        var normalizedCodepoint = NormalizeShiftedLetterIdentityCodepoint(
            NormalizeKittyFunctionalCodepoint(parsed.Codepoint),
            parsed.Modifier);
        var normalizedExpectedCodepoint = NormalizeShiftedLetterIdentityCodepoint(
            NormalizeKittyFunctionalCodepoint(expectedCodepoint),
            expectedModifier);
        if (normalizedCodepoint == normalizedExpectedCodepoint) return true;

        if (parsed.BaseLayoutKey.HasValue && parsed.BaseLayoutKey.Value == expectedCodepoint)
        {
            var codepoint = normalizedCodepoint;
            var isLatinLetter = codepoint is >= 'a' and <= 'z';
            var isKnownSymbol = codepoint is >= 0 and <= char.MaxValue && _symbolKeys.Contains((char)codepoint);
            if (!isLatinLetter && !isKnownSymbol) return true;
        }

        return false;
    }

    private static ParsedModifyOtherKeysSequence? ParseModifyOtherKeysSequence(string data)
    {
        var match = _modifyOtherKeysRegex.Match(data);
        if (!match.Success || !TryGetInt(match, 1, out var modifierValue) || !TryGetInt(match, 2, out var codepoint))
        {
            return null;
        }

        return new ParsedModifyOtherKeysSequence(codepoint, modifierValue - 1);
    }

    private static bool MatchesModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        var parsed = ParseModifyOtherKeysSequence(data);
        return parsed is not null && parsed.Codepoint == expectedKeycode && parsed.Modifier == expectedModifier;
    }

    private static bool IsWindowsTerminalSession()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) &&
               string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) &&
               string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")) &&
               string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));
    }

    private static bool MatchesRawBackspace(string data, int expectedModifier)
    {
        if (data == "\x7f") return expectedModifier == 0;
        if (data != "\x08") return false;
        return IsWindowsTerminalSession() ? expectedModifier == _ctrlModifier : expectedModifier == 0;
    }

    private static string? RawCtrlChar(string key)
    {
        var character = char.ToLowerInvariant(key[0]);
        var code = character;
        if ((code is >= 'a' and <= 'z') || character is '[' or '\\' or ']' or '_')
        {
            return ((char)(code & 0x1f)).ToString();
        }

        return character == '-' ? ((char)31).ToString() : null;
    }

    private static bool IsDigitKey(string key) => key.Length == 1 && key[0] is >= '0' and <= '9';

    internal static bool IsSymbolKey(char key) => _symbolKeys.Contains(key);

    private static bool MatchesPrintableModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        if (expectedModifier == 0) return false;
        var parsed = ParseModifyOtherKeysSequence(data);
        if (parsed is null || parsed.Modifier != expectedModifier) return false;
        return NormalizeShiftedLetterIdentityCodepoint(parsed.Codepoint, parsed.Modifier) ==
               NormalizeShiftedLetterIdentityCodepoint(expectedKeycode, expectedModifier);
    }

    private static string? FormatParsedKey(int codepoint, int modifier, int? baseLayoutKey = null)
    {
        var normalizedCodepoint = NormalizeKittyFunctionalCodepoint(codepoint);
        var identityCodepoint = NormalizeShiftedLetterIdentityCodepoint(normalizedCodepoint, modifier);
        var isLatinLetter = identityCodepoint is >= 'a' and <= 'z';
        var isDigit = identityCodepoint is >= '0' and <= '9';
        var isKnownSymbol = identityCodepoint is >= 0 and <= char.MaxValue && _symbolKeys.Contains((char)identityCodepoint);
        var effectiveCodepoint = isLatinLetter || isDigit || isKnownSymbol ? identityCodepoint : baseLayoutKey ?? identityCodepoint;

        string? keyName = effectiveCodepoint switch
        {
            _escapeCodepoint => "escape",
            _tabCodepoint => "tab",
            _enterCodepoint or _keypadEnterCodepoint => "enter",
            _spaceCodepoint => "space",
            _backspaceCodepoint => "backspace",
            _deleteCodepoint => "delete",
            _insertCodepoint => "insert",
            _homeCodepoint => "home",
            _endCodepoint => "end",
            _pageUpCodepoint => "pageUp",
            _pageDownCodepoint => "pageDown",
            _upCodepoint => "up",
            _downCodepoint => "down",
            _leftCodepoint => "left",
            _rightCodepoint => "right",
            >= '0' and <= '9' => ((char)effectiveCodepoint).ToString(),
            >= 'a' and <= 'z' => ((char)effectiveCodepoint).ToString(),
            _ when effectiveCodepoint is >= 0 and <= char.MaxValue && _symbolKeys.Contains((char)effectiveCodepoint) =>
                ((char)effectiveCodepoint).ToString(),
            _ => null,
        };

        return keyName is null ? null : FormatKeyNameWithModifiers(keyName, modifier);
    }

    private static string? FormatKeyNameWithModifiers(string keyName, int modifier)
    {
        var effectiveModifier = modifier & ~_lockMask;
        const int supportedModifierMask = _shiftModifier | _ctrlModifier | _altModifier | _superModifier;
        if ((effectiveModifier & ~supportedModifierMask) != 0) return null;

        var modifiers = new List<string>(4);
        if ((effectiveModifier & _shiftModifier) != 0) modifiers.Add("shift");
        if ((effectiveModifier & _ctrlModifier) != 0) modifiers.Add("ctrl");
        if ((effectiveModifier & _altModifier) != 0) modifiers.Add("alt");
        if ((effectiveModifier & _superModifier) != 0) modifiers.Add("super");
        return modifiers.Count == 0 ? keyName : $"{string.Join('+', modifiers)}+{keyName}";
    }

    private static KeyEventType ParseEventType(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return KeyEventType.Press;
        return int.TryParse(eventType, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) switch
        {
            true when value == 2 => KeyEventType.Repeat,
            true when value == 3 => KeyEventType.Release,
            _ => KeyEventType.Press,
        };
    }

    private static int NormalizeKittyFunctionalCodepoint(int codepoint) =>
        _kittyFunctionalKeyEquivalents.TryGetValue(codepoint, out var equivalent) ? equivalent : codepoint;

    private static int NormalizeShiftedLetterIdentityCodepoint(int codepoint, int modifier)
    {
        var effectiveModifier = modifier & ~_lockMask;
        return (effectiveModifier & _shiftModifier) != 0 && codepoint is >= 'A' and <= 'Z' ? codepoint + 32 : codepoint;
    }

    private static bool MatchesLegacySequence(string data, IReadOnlyList<string> sequences) => sequences.Contains(data, StringComparer.Ordinal);

    private static bool MatchesLegacyModifierSequence(string data, string key, int modifier)
    {
        if (modifier == _shiftModifier && _legacyShiftSequences.TryGetValue(key, out var shift))
        {
            return MatchesLegacySequence(data, shift);
        }

        if (modifier == _ctrlModifier && _legacyCtrlSequences.TryGetValue(key, out var ctrl))
        {
            return MatchesLegacySequence(data, ctrl);
        }

        return false;
    }

    private static string? DecodeModifyOtherKeysPrintable(string data)
    {
        var parsed = ParseModifyOtherKeysSequence(data);
        if (parsed is null) return null;
        var modifier = parsed.Modifier & ~_lockMask;
        if ((modifier & ~_shiftModifier) != 0 || parsed.Codepoint < 32 || parsed.Codepoint > 0x10ffff) return null;

        try
        {
            return char.ConvertFromUtf32(parsed.Codepoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static bool TryGetInt(Match match, int group, out int value) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int? TryGetOptionalInt(Match match, int group)
    {
        var value = match.Groups[group].Value;
        return string.IsNullOrEmpty(value) || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? null
            : result;
    }

    private static string? TryGetOptionalString(Match match, int group) =>
        match.Groups[group].Success ? match.Groups[group].Value : null;

    private sealed record ParsedKittySequence(
        int Codepoint,
        int? ShiftedKey,
        int? BaseLayoutKey,
        int Modifier,
        KeyEventType EventType);

    private sealed record ParsedModifyOtherKeysSequence(int Codepoint, int Modifier);
}

internal readonly record struct ParsedKeyIdentifier(string Key, bool Ctrl, bool Shift, bool Alt, bool Super);

internal static class KeyIdentifierParsing
{
    private static readonly HashSet<string> _modifierNames = new(StringComparer.Ordinal)
    {
        "ctrl",
        "shift",
        "alt",
        "super",
    };

    public static bool TryParse(string value, out ParsedKeyIdentifier parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(value)) return false;

        var normalized = value.ToLowerInvariant();
        string key;
        string modifiers;
        if (normalized == "+")
        {
            key = "+";
            modifiers = string.Empty;
        }
        else if (normalized.EndsWith("++", StringComparison.Ordinal))
        {
            key = "+";
            modifiers = normalized[..^2];
            if (modifiers.Length == 0) return false;
        }
        else
        {
            var delimiter = normalized.LastIndexOf('+');
            if (delimiter < 0)
            {
                key = normalized;
                modifiers = string.Empty;
            }
            else
            {
                if (delimiter == 0) return false;
                key = normalized[(delimiter + 1)..];
                modifiers = normalized[..delimiter];
            }
        }

        if (!IsBaseKey(key)) return false;

        var ctrl = false;
        var shift = false;
        var alt = false;
        var super = false;
        if (modifiers.Length > 0)
        {
            foreach (var modifier in modifiers.Split('+', StringSplitOptions.None))
            {
                if (!_modifierNames.Contains(modifier)) return false;
                switch (modifier)
                {
                    case "ctrl" when !ctrl:
                        ctrl = true;
                        break;
                    case "shift" when !shift:
                        shift = true;
                        break;
                    case "alt" when !alt:
                        alt = true;
                        break;
                    case "super" when !super:
                        super = true;
                        break;
                    default:
                        return false;
                }
            }
        }

        parsed = new ParsedKeyIdentifier(key, ctrl, shift, alt, super);
        return true;
    }

    private static bool IsBaseKey(string key)
    {
        if (key.Length == 1 && ((key[0] is >= 'a' and <= 'z') || (key[0] is >= '0' and <= '9') ||
                                Keys.IsSymbolKey(key[0])))
        {
            return true;
        }

        return key is "escape" or "esc" or "enter" or "return" or "tab" or "space" or "backspace" or "delete" or
            "insert" or "clear" or "home" or "end" or "pageup" or "pagedown" or "up" or "down" or "left" or
            "right" or "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7" or "f8" or "f9" or "f10" or "f11" or
            "f12";
    }
}
