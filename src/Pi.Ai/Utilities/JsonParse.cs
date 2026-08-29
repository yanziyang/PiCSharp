using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Repair and best-effort parsing helpers for streamed provider JSON.</summary>
public static class JsonParseUtilities
{
    private static readonly HashSet<char> _validJsonEscapes = ['"', '\\', '/', 'b', 'f', 'n', 'r', 't', 'u'];

    /// <summary>
    /// Repairs malformed JSON string literals by escaping raw control characters and doubling
    /// backslashes before invalid escape characters.
    /// </summary>
    public static string RepairJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var repaired = new StringBuilder(json.Length);
        var inString = false;

        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (!inString)
            {
                repaired.Append(character);
                if (character == '"')
                {
                    inString = true;
                }

                continue;
            }

            if (character == '"')
            {
                repaired.Append(character);
                inString = false;
                continue;
            }

            if (character == '\\')
            {
                if (index + 1 >= json.Length)
                {
                    repaired.Append("\\\\");
                    continue;
                }

                var nextCharacter = json[index + 1];
                if (nextCharacter == 'u')
                {
                    var unicodeDigits = json.AsSpan(index + 2, Math.Min(4, json.Length - index - 2));
                    if (unicodeDigits.Length == 4 && unicodeDigits.ToString().All(Uri.IsHexDigit))
                    {
                        repaired.Append("\\u");
                        repaired.Append(unicodeDigits);
                        index += 5;
                        continue;
                    }
                }

                if (_validJsonEscapes.Contains(nextCharacter))
                {
                    repaired.Append('\\');
                    repaired.Append(nextCharacter);
                    index++;
                    continue;
                }

                repaired.Append("\\\\");
                continue;
            }

            if (IsControlCharacter(character))
            {
                repaired.Append(EscapeControlCharacter(character));
            }
            else
            {
                repaired.Append(character);
            }
        }

        return repaired.ToString();
    }

    /// <summary>Parses complete JSON, retrying once after the pinned Pi repair pass.</summary>
    public static JsonNode? ParseJsonWithRepair(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException original)
        {
            var repairedJson = RepairJson(json);
            if (repairedJson != json)
            {
                return JsonNode.Parse(repairedJson);
            }

            throw new JsonException(original.Message, original);
        }
    }

    /// <summary>
    /// Parses complete or incomplete streamed JSON. An incomplete or unparseable value returns
    /// the best-effort object/array prefix, or an empty object when no prefix is usable.
    /// </summary>
    public static JsonNode ParseStreamingJson(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson))
        {
            return new JsonObject();
        }

        try
        {
            return ParseJsonWithRepair(partialJson) ?? new JsonObject();
        }
        catch
        {
            try
            {
                return new PartialJsonParser(partialJson).Parse();
            }
            catch
            {
                try
                {
                    return new PartialJsonParser(RepairJson(partialJson)).Parse();
                }
                catch
                {
                    return new JsonObject();
                }
            }
        }
    }

    private static bool IsControlCharacter(char character) => character <= 0x1F;

    private static string EscapeControlCharacter(char character) => character switch
    {
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => $"\\u{(int)character:x4}",
    };

    private sealed class PartialJsonParser
    {
        private readonly string _json;
        private int _index;

        public PartialJsonParser(string json)
        {
            _json = json;
        }

        public JsonNode Parse()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                return new JsonObject();
            }

            var value = ParseValue();
            return value.HasValue && value.Node is not null ? value.Node : new JsonObject();
        }

        private ValueResult ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                return default;
            }

            return Current switch
            {
                '{' => new ValueResult(true, ParseObject()),
                '[' => new ValueResult(true, ParseArray()),
                '"' => new ValueResult(true, JsonValue.Create(ParseString())),
                't' => ParseLiteral("true", true),
                'f' => ParseLiteral("false", false),
                'n' => ParseLiteral("null", null),
                '-' or >= '0' and <= '9' => ParseNumber(),
                _ => default,
            };
        }

        private JsonObject ParseObject()
        {
            _index++;
            var result = new JsonObject();
            SkipWhitespace();
            if (AtEnd || Current == '}')
            {
                if (!AtEnd)
                {
                    _index++;
                }

                return result;
            }

            while (!AtEnd)
            {
                SkipWhitespace();
                if (AtEnd || Current != '"')
                {
                    return result;
                }

                var name = ParseString();
                SkipWhitespace();
                if (AtEnd || Current != ':')
                {
                    return result;
                }

                _index++;
                var value = ParseValue();
                if (!value.HasValue)
                {
                    result[name] = null;
                    return result;
                }

                result[name] = value.Node;
                SkipWhitespace();
                if (AtEnd)
                {
                    return result;
                }

                if (Current == '}')
                {
                    _index++;
                    return result;
                }

                if (Current != ',')
                {
                    return result;
                }

                _index++;
            }

            return result;
        }

        private JsonArray ParseArray()
        {
            _index++;
            var result = new JsonArray();
            SkipWhitespace();
            if (AtEnd || Current == ']')
            {
                if (!AtEnd)
                {
                    _index++;
                }

                return result;
            }

            while (!AtEnd)
            {
                var value = ParseValue();
                if (value.HasValue)
                {
                    result.Add(value.Node);
                }

                SkipWhitespace();
                if (AtEnd)
                {
                    return result;
                }

                if (Current == ']')
                {
                    _index++;
                    return result;
                }

                if (Current != ',')
                {
                    return result;
                }

                _index++;
                SkipWhitespace();
                if (AtEnd)
                {
                    return result;
                }
            }

            return result;
        }

        private string ParseString()
        {
            if (!AtEnd && Current == '"')
            {
                _index++;
            }

            var result = new StringBuilder();
            while (!AtEnd)
            {
                var character = _json[_index++];
                if (character == '"')
                {
                    break;
                }

                if (character != '\\')
                {
                    result.Append(character);
                    continue;
                }

                if (AtEnd)
                {
                    result.Append('\\');
                    break;
                }

                var escaped = _json[_index++];
                result.Append(escaped switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ParseUnicodeEscape(),
                    _ => escaped,
                });
            }

            return result.ToString();
        }

        private char ParseUnicodeEscape()
        {
            if (_json.Length - _index < 4)
            {
                return 'u';
            }

            var digits = _json.AsSpan(_index, 4);
            if (!digits.ToString().All(Uri.IsHexDigit))
            {
                return 'u';
            }

            _index += 4;
            return (char)int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private ValueResult ParseLiteral(string literal, bool? value)
        {
            var remaining = _json.AsSpan(_index);
            var length = Math.Min(literal.Length, remaining.Length);
            if (!remaining[..length].SequenceEqual(literal.AsSpan(0, length)))
            {
                return default;
            }

            if (length < literal.Length)
            {
                _index = _json.Length;
                return default;
            }

            _index += literal.Length;
            return new ValueResult(true, value is null ? null : JsonValue.Create(value.Value));
        }

        private ValueResult ParseNumber()
        {
            var start = _index;
            while (!AtEnd && (char.IsDigit(Current) || Current is '-' or '+' or '.' or 'e' or 'E'))
            {
                _index++;
            }

            var raw = _json[start.._index];
            while (raw.Length > 0 && raw[^1] is '.' or 'e' or 'E' or '+' or '-')
            {
                raw = raw[..^1];
            }

            if (raw.Length == 0)
            {
                return default;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                return new ValueResult(true, JsonValue.Create(intValue));
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return new ValueResult(true, JsonValue.Create(integer));
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                double.IsFinite(number))
            {
                return new ValueResult(true, JsonValue.Create(number));
            }

            return default;
        }

        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                _index++;
            }
        }

        private bool AtEnd => _index >= _json.Length;

        private char Current => _json[_index];

        private readonly record struct ValueResult(bool HasValue, JsonNode? Node);
    }
}
