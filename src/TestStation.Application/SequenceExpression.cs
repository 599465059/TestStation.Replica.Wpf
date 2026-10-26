using System.Globalization;

namespace TestStation.Application;

public static class SequenceExpression
{
    public static void ValidateSyntax(string expression)
    {
        if (expression.Length > 4096) throw new InvalidDataException("表达式过长");
        var parser = new Parser(expression, new Dictionary<string, object?>());
        parser.ParseOr();
        parser.ExpectEnd();
    }

    public static bool Evaluate(string expression, IReadOnlyDictionary<string, object?> values)
        => Boolean(EvaluateValue(expression, values));

    public static object? EvaluateValue(string expression, IReadOnlyDictionary<string, object?> values)
    {
        if (expression.Length > 4096) throw new InvalidDataException("表达式过长");
        var parser = new Parser(expression, values);
        var result = parser.ParseOr();
        parser.ExpectEnd();
        var value = result();
        if (value is double number && !double.IsFinite(number)) throw new InvalidDataException("公式结果不是有限数字");
        return value;
    }

    public static IReadOnlyCollection<string> GetReferences(string expression)
    {
        if (expression.Length > 4096) throw new InvalidDataException("表达式过长");
        var parser = new Parser(expression, new Dictionary<string, object?>());
        parser.ParseOr(); parser.ExpectEnd(); return parser.References;
    }

    public static void ValidateReferences(string expression, IEnumerable<string> knownNames)
    {
        var known = knownNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in GetReferences(expression))
            if (!known.Contains(name)) throw new InvalidDataException($"变量或通道未声明: {name}");
    }

    private static bool Boolean(object? value) => value switch
    {
        bool boolean => boolean,
        null => false,
        string text => text.Length > 0,
        _ when double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) => number != 0,
        _ => throw new InvalidDataException($"条件不是布尔值: {value}")
    };

    private static double Number(object? value) =>
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"数值无效: {value}");

    private sealed class Parser
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, object?> _values;
        private int _position;
        private Token _current = new("end", "");
        private int _depth;
        public HashSet<string> References { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                    var leftValue = prior(); var rightValue = right();
                    if (SequenceValueOperations.AsArray(leftValue) is not null || SequenceValueOperations.AsArray(rightValue) is not null)
                    {
                        if (operation is not ("==" or "!=")) throw new InvalidDataException("数组表达式仅支持 == 和 !=；范围比较请使用读取判定规则");
                        var equal = SequenceValueOperations.Compare(leftValue, rightValue, "==", "EXACT");
                        return operation == "==" ? equal : !equal;
                    }
                    var comparison = Compare(leftValue, rightValue);
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
                    if (operation is "/" or "%" && divisor == 0) throw new InvalidDataException("除数不能为 0");
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
            if (++_depth > 64) throw new InvalidDataException("表达式嵌套过深");
            try
            {
                if (Take("!") || Take("not")) { var operand = ParseUnary(); return () => !Boolean(operand()); }
                if (Take("-")) { var operand = ParseUnary(); return () => -Number(operand()); }
                if (Take("+")) { var operand = ParseUnary(); return () => Number(operand()); }
                var result = ParsePrimary();
                while (Take("["))
                {
                    var index = ParseOr(); if (!Take("]")) throw Invalid();
                    var prior = result;
                    result = () =>
                    {
                        var array = SequenceValueOperations.AsArray(prior()) ?? throw new InvalidDataException("索引目标必须是数组");
                        var offset = Number(index());
                        if (offset != Math.Truncate(offset) || offset < 0 || offset >= array.Length) throw new InvalidDataException("数组下标超出范围（下标从 0 开始）");
                        return array[(int)offset];
                    };
                }
                return result;
            }
            finally { _depth--; }
        }

        private Func<object?> ParsePrimary()
        {
            if (Take("(")) { var result = ParseOr(); if (!Take(")")) throw Invalid(); return result; }
            if (Take("["))
            {
                var items = new List<Func<object?>>();
                if (!Take("]"))
                {
                    do { items.Add(ParseOr()); } while (Take(","));
                    if (!Take("]")) throw Invalid();
                }
                return () => items.Select(item => item()).ToArray();
            }
            var token = _current;
            if (token.Kind is not ("number" or "string" or "identifier" or "reference")) throw Invalid();
            Next();
            if (token.Kind == "number")
            {
                if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw Invalid();
                return () => number;
            }
            if (token.Kind == "string") return () => token.Text;
            if (token.Kind == "reference")
            {
                var nested = new Parser(token.Text.Trim(), _values);
                var result = nested.ParseOr(); nested.ExpectEnd(); References.UnionWith(nested.References); return result;
            }
            if (Take("("))
            {
                var args = new List<Func<object?>>();
                if (!Take(")"))
                {
                    do { args.Add(ParseOr()); } while (Take(","));
                    if (!Take(")")) throw Invalid();
                }
                var name = token.Text.ToLowerInvariant();
                var valid = name switch
                {
                    "abs" or "sqrt" or "sin" or "cos" or "tan" or "log" or "log10" or "exp" or "floor" or "ceil" or "sum" or "avg" or "length" => args.Count == 1,
                    "pow" => args.Count == 2, "round" => args.Count is 1 or 2,
                    "clamp" => args.Count == 3, "min" or "max" => args.Count > 0, _ => false
                };
                if (!valid) throw new InvalidDataException($"函数不存在或参数数量不正确: {token.Text}");
                return () => Call(name, args.Select(arg => arg()).ToArray());
            }
            if (token.Text.Equals("true", StringComparison.OrdinalIgnoreCase)) return () => true;
            if (token.Text.Equals("false", StringComparison.OrdinalIgnoreCase)) return () => false;
            if (token.Text.Equals("Math.PI", StringComparison.OrdinalIgnoreCase)) return () => Math.PI;
            if (token.Text.Equals("Math.E", StringComparison.OrdinalIgnoreCase)) return () => Math.E;
            References.Add(token.Text);
            return () => _values.TryGetValue(token.Text, out var value)
                ? SequenceValueOperations.Unwrap(value) : throw new InvalidDataException($"变量未定义: {token.Text}");
        }

        private static object Call(string name, object?[] args)
        {
            if (name == "length") return args[0] is string text ? text.Length :
                SequenceValueOperations.AsArray(args[0])?.Length ?? throw new InvalidDataException("length 需要文本或数组");
            if (name is "sum" or "avg" or "min" or "max")
            {
                var values = args.SelectMany(arg => SequenceValueOperations.AsArray(arg) ?? [arg]).Select(Number).ToArray();
                if (values.Length == 0) throw new InvalidDataException("统计函数不接受空数组");
                return name switch { "sum" => values.Sum(), "avg" => values.Average(), "min" => values.Min(), _ => values.Max() };
            }
            var x = Number(args[0]);
            return name switch
            {
                "abs" => Math.Abs(x), "sqrt" => Math.Sqrt(x), "sin" => Math.Sin(x), "cos" => Math.Cos(x), "tan" => Math.Tan(x),
                "log" => Math.Log(x), "log10" => Math.Log10(x), "exp" => Math.Exp(x), "floor" => Math.Floor(x), "ceil" => Math.Ceiling(x),
                "pow" => Math.Pow(x, Number(args[1])), "round" => Math.Round(x, args.Length == 1 ? 0 : checked((int)Number(args[1]))),
                "clamp" => Math.Clamp(x, Number(args[1]), Number(args[2])), _ => throw Invalid()
            };
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
                if (_position < _source.Length && _source[_position] is 'e' or 'E')
                {
                    _position++;
                    if (_position < _source.Length && _source[_position] is '+' or '-') _position++;
                    while (_position < _source.Length && char.IsDigit(_source[_position])) _position++;
                }
                _current = new("number", _source[start.._position]);
                return;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] is '_' or '.')) _position++;
                _current = new("identifier", _source[start.._position]);
                return;
            }
            if (_position < _source.Length && _source[start..(_position + 1)] is "==" or "!=" or ">=" or "<=" or "&&" or "||") _position++;
            _current = new("operator", _source[start.._position]);
            if (_current.Text is not ("==" or "!=" or ">=" or "<=" or "&&" or "||" or ">" or "<" or "+" or "-" or "*" or "/" or "%" or "!" or "(" or ")" or "[" or "]" or ",")) throw Invalid();
        }

        private static int Compare(object? left, object? right)
        {
            if (left is bool a && right is bool b) return a.CompareTo(b);
            if (double.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return x.CompareTo(y);
            return string.CompareOrdinal(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture));
        }

        private static InvalidDataException Invalid() => new("表达式语法无效");
        private sealed record Token(string Kind, string Text);
    }
}
