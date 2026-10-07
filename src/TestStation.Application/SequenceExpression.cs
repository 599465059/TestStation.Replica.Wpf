using System.Globalization;

namespace TestStation.Application;

public static class SequenceExpression
{
    public static bool Evaluate(string expression, IReadOnlyDictionary<string, object?> values)
    {
        if (expression.Length > 2048) throw new InvalidDataException("IF 表达式过长");
        var parser = new Parser(expression, values);
        var result = parser.ParseOr();
        parser.ExpectEnd();
        return Boolean(result());
    }

    private static bool Boolean(object? value) => value switch
    {
        bool boolean => boolean,
        null => false,
        string text => text.Length > 0,
        _ when double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) => number != 0,
        _ => throw new InvalidDataException($"IF 条件不是布尔值: {value}")
    };

    private static double Number(object? value) =>
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"IF 数值无效: {value}");

    private sealed class Parser
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, object?> _values;
        private int _position;
        private Token _current = new("end", "");
        private int _depth;

        public Parser(string source, IReadOnlyDictionary<string, object?> values)
        {
            _source = source;
            _values = values;
            Next();
        }

        public Func<object?> ParseOr()
        {
            var left = ParseAnd();
            while (Take("||") || Take("or"))
            {
                var right = ParseAnd();
                var prior = left;
                left = () => Boolean(prior()) || Boolean(right());
            }
            return left;
        }

        private Func<object?> ParseAnd()
        {
            var left = ParseComparison();
            while (Take("&&") || Take("and"))
            {
                var right = ParseComparison();
                var prior = left;
                left = () => Boolean(prior()) && Boolean(right());
            }
            return left;
        }

        private Func<object?> ParseComparison()
        {
            var left = ParseAdd();
            while (_current.Text is "==" or "!=" or ">" or ">=" or "<" or "<=")
            {
                var operation = _current.Text;
                Next();
                var right = ParseAdd();
                var prior = left;
                left = () =>
                {
                    var comparison = Compare(prior(), right());
                    return operation switch
                    {
                        "==" => comparison == 0,
                        "!=" => comparison != 0,
                        ">" => comparison > 0,
                        ">=" => comparison >= 0,
                        "<" => comparison < 0,
                        _ => comparison <= 0
                    };
                };
            }
            return left;
        }

        private Func<object?> ParseAdd()
        {
            var left = ParseMultiply();
            while (_current.Text is "+" or "-")
            {
                var operation = _current.Text;
                Next();
                var right = ParseMultiply();
                var prior = left;
                left = () => operation == "+" ? Number(prior()) + Number(right()) : Number(prior()) - Number(right());
            }
            return left;
        }

        private Func<object?> ParseMultiply()
        {
            var left = ParseUnary();
            while (_current.Text is "*" or "/" or "%")
            {
                var operation = _current.Text;
                Next();
                var right = ParseUnary();
                var prior = left;
                left = () =>
                {
                    var divisor = Number(right());
                    if (operation is "/" or "%" && divisor == 0) throw new InvalidDataException("IF 除数不能为 0");
                    return operation switch
                    {
                        "*" => Number(prior()) * divisor,
                        "/" => Number(prior()) / divisor,
                        _ => Number(prior()) % divisor
                    };
                };
            }
            return left;
        }

        private Func<object?> ParseUnary()
        {
            if (Take("!") || Take("not")) { var operand = ParseUnary(); return () => !Boolean(operand()); }
            if (Take("-")) { var operand = ParseUnary(); return () => -Number(operand()); }
            if (Take("+")) { var operand = ParseUnary(); return () => Number(operand()); }
            if (Take("("))
            {
                if (++_depth > 64) throw new InvalidDataException("IF 表达式嵌套过深");
                var result = ParseOr();
                if (!Take(")")) throw Invalid();
                _depth--;
                return result;
            }
            var token = _current;
            if (token.Kind is not ("number" or "string" or "identifier" or "reference")) throw Invalid();
            Next();
            if (token.Kind == "number") { var number = double.Parse(token.Text, CultureInfo.InvariantCulture); return () => number; }
            if (token.Kind == "string") return () => token.Text;
            if (token.Text.Equals("true", StringComparison.OrdinalIgnoreCase)) return () => true;
            if (token.Text.Equals("false", StringComparison.OrdinalIgnoreCase)) return () => false;
            return () => _values.TryGetValue(token.Text, out var value)
                ? value : throw new InvalidDataException($"IF 变量未定义: {token.Text}");
        }

        public void ExpectEnd()
        {
            if (_current.Kind != "end") throw Invalid();
        }

        private bool Take(string text)
        {
            if (!_current.Text.Equals(text, StringComparison.OrdinalIgnoreCase)) return false;
            Next();
            return true;
        }

        private void Next()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++;
            if (_position == _source.Length) { _current = new("end", ""); return; }
            var start = _position;
            var c = _source[_position++];
            if (c == '$' && _position < _source.Length && _source[_position] == '{')
            {
                _position++;
                var nameStart = _position;
                while (_position < _source.Length && _source[_position] != '}') _position++;
                if (_position == _source.Length || _position == nameStart) throw Invalid();
                _current = new("reference", _source[nameStart.._position++]);
                return;
            }
            if (c is '\'' or '"')
            {
                var content = new System.Text.StringBuilder();
                while (_position < _source.Length && _source[_position] != c)
                {
                    if (_source[_position] == '\\' && _position + 1 < _source.Length) _position++;
                    content.Append(_source[_position++]);
                }
                if (_position == _source.Length) throw Invalid();
                _position++;
                _current = new("string", content.ToString());
                return;
            }
            if (char.IsDigit(c) || c == '.' && _position < _source.Length && char.IsDigit(_source[_position]))
            {
                while (_position < _source.Length && (char.IsDigit(_source[_position]) || _source[_position] == '.')) _position++;
                _current = new("number", _source[start.._position]);
                return;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_')) _position++;
                _current = new("identifier", _source[start.._position]);
                return;
            }
            if (_position < _source.Length && _source[start..(_position + 1)] is "==" or "!=" or ">=" or "<=" or "&&" or "||") _position++;
            _current = new("operator", _source[start.._position]);
            if (_current.Text is not ("==" or "!=" or ">=" or "<=" or "&&" or "||" or ">" or "<" or "+" or "-" or "*" or "/" or "%" or "!" or "(" or ")")) throw Invalid();
        }

        private static int Compare(object? left, object? right)
        {
            if (left is bool a && right is bool b) return a.CompareTo(b);
            if (double.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return x.CompareTo(y);
            return string.CompareOrdinal(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture));
        }

        private static InvalidDataException Invalid() => new("IF 表达式语法无效");
        private sealed record Token(string Kind, string Text);
    }
}
