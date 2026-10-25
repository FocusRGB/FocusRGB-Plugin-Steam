using System.Text;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class VdfObject
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<KeyValuePair<string, object>> Values => _values;

        public void Add(string key, object value) => _values[key] = value;

        public string? GetString(string key) =>
            _values.TryGetValue(key, out var value) ? value as string : null;

        public VdfObject? GetObject(string key) =>
            _values.TryGetValue(key, out var value) ? value as VdfObject : null;
    }

    internal static class VdfParser
    {
        private const int MaximumDocumentLength = 4 * 1024 * 1024;
        private const int MaximumDepth = 64;

        public static VdfObject Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length > MaximumDocumentLength)
                throw new FormatException("The VDF document is too large.");
            var tokenizer = new Tokenizer(text);
            return ParseObject(tokenizer, 0);
        }

        private static VdfObject ParseObject(Tokenizer tokenizer, int depth)
        {
            if (depth > MaximumDepth) throw new FormatException("The VDF document is nested too deeply.");
            var result = new VdfObject();
            while (tokenizer.Read() is { } key)
            {
                if (!key.Quoted && key.Value == "}")
                {
                    if (depth == 0)
                    {
                        throw new FormatException("Unexpected closing brace in VDF document.");
                    }

                    return result;
                }

                if (!key.Quoted && key.Value == "{")
                {
                    throw new FormatException("A VDF object requires a key.");
                }

                var value = tokenizer.Read()
                    ?? throw new FormatException($"VDF key '{key.Value}' has no value.");
                result.Add(key.Value,
                    !value.Quoted && value.Value == "{" ? ParseObject(tokenizer, depth + 1) : value.Value);
            }

            if (depth > 0)
            {
                throw new FormatException("VDF object is missing a closing brace.");
            }

            return result;
        }

        private sealed class Tokenizer(string text)
        {
            private int _offset;

            public Token? Read()
            {
                SkipTrivia();
                if (_offset >= text.Length)
                {
                    return null;
                }

                if (text[_offset] is '{' or '}')
                {
                    return new Token(text[_offset++].ToString(), Quoted: false);
                }

                if (text[_offset] != '"')
                {
                    var start = _offset;
                    while (_offset < text.Length && !char.IsWhiteSpace(text[_offset])
                        && text[_offset] is not '{' and not '}')
                    {
                        _offset++;
                    }

                    return new Token(text[start.._offset], Quoted: false);
                }

                _offset++;
                var value = new StringBuilder();
                while (_offset < text.Length)
                {
                    var character = text[_offset++];
                    if (character == '"')
                    {
                        return new Token(value.ToString(), Quoted: true);
                    }

                    if (character == '\\' && _offset < text.Length
                        && text[_offset] is '\\' or '"')
                    {
                        character = text[_offset++];
                    }

                    value.Append(character);
                }

                throw new FormatException("VDF string is missing a closing quote.");
            }

            private void SkipTrivia()
            {
                while (_offset < text.Length)
                {
                    if (char.IsWhiteSpace(text[_offset]))
                    {
                        _offset++;
                        continue;
                    }

                    if (_offset + 1 < text.Length && text[_offset] == '/' && text[_offset + 1] == '/')
                    {
                        _offset += 2;
                        while (_offset < text.Length && text[_offset] is not '\r' and not '\n')
                        {
                            _offset++;
                        }
                        continue;
                    }

                    return;
                }
            }
        }

        private sealed record Token(string Value, bool Quoted);
    }
}
