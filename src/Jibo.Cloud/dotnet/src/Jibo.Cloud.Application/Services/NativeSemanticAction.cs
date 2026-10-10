using System.Globalization;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>Bounded interpreter for data assignments in the reference DSL, without eval or a JS engine.</summary>
internal static class NativeSemanticAction
{
    public static void Apply(string source, Dictionary<string, object?> values,
        Dictionary<string, Dictionary<string, object?>> subs)
    {
        new Interpreter(source, values, subs).Run();
    }

    private sealed class Interpreter
    {
        private readonly List<string> _tokens;
        private readonly Dictionary<string, object?> _scope;
        private int _position;
        public Interpreter(string source, Dictionary<string, object?> values, Dictionary<string, Dictionary<string, object?>> subs)
        {
            _tokens = Regex.Matches(source, @"'(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*""|(?:\d+(?:\.\d+)?)|[A-Za-z_$][\w$]*|===|!==|==|!=|<=|>=|\+=|-=|&&|\|\||[^\s]")
                .Select(m => m.Value).ToList();
            _tokens.Add("<eof>");
            _scope = values;
            foreach (var pair in subs) _scope[pair.Key] = pair.Value;
        }
        private string Current => _tokens[_position];
        private bool Take(string token) { if (Current != token) return false; _position++; return true; }
        private void Expect(string token) { if (!Take(token)) throw new FormatException($"Semantic action expected {token}, found {Current}."); }
        public void Run() { while (Current != "<eof>") Statement(true); }
        private void Statement(bool execute)
        {
            if (Take(";")) return;
            if (Take("{")) { while (Current is not ("}" or "<eof>")) Statement(execute); Expect("}"); return; }
            if (Take("if"))
            {
                Expect("("); var condition = Truth(Expression()); Expect(")");
                Statement(execute && condition);
                if (Take("else")) Statement(execute && !condition);
                return;
            }
            if (Take("delete"))
            {
                var path = ReadPath();
                if (execute) Set(path, null, delete: true);
                Take(";"); return;
            }
            var assignment = ReadPath();
            var op = Current;
            if (op is not ("=" or "+=" or "-=")) throw new FormatException($"Unsupported semantic statement: {string.Join('.', assignment)} {op}");
            _position++;
            var value = Expression();
            if (execute)
            {
                if (op == "+=") value = Add(Get(assignment), value);
                if (op == "-=") value = Number(Get(assignment)) - Number(value);
                Set(assignment, value);
            }
            Take(";");
        }
        private List<string> ReadPath()
        {
            var path = new List<string> { Current }; _position++;
            while (Take(".")) { path.Add(Current); _position++; }
            if (path[0] == "this") path.RemoveAt(0);
            return path;
        }
        private object? Get(IReadOnlyList<string> path)
        {
            object? result = _scope;
            foreach (var key in path) result = Member(result, key);
            return result;
        }
        private void Set(IReadOnlyList<string> path, object? value, bool delete = false)
        {
            var dictionary = _scope;
            foreach (var key in path.SkipLast(1))
            {
                if (!dictionary.TryGetValue(key, out var item) || item is not Dictionary<string, object?> nested) return;
                dictionary = nested;
            }
            if (delete) dictionary.Remove(path[^1]); else dictionary[path[^1]] = value;
        }
        private object? Expression(int minimum = 0)
        {
            object? left;
            if (Take("!")) left = !Truth(Expression(8));
            else if (Take("-")) left = -Number(Expression(8));
            else if (Take("+")) left = Number(Expression(8));
            else if (Take("(")) { left = Expression(); Expect(")"); }
            else
            {
                var token = Current; _position++;
                if (token.StartsWith('\'') || token.StartsWith('"')) left = Regex.Unescape(token[1..^1]);
                else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) left = number;
                else if (token == "true") left = true;
                else if (token == "false") left = false;
                else if (token is "null" or "undefined") left = null;
                else if (token == "this") left = _scope;
                else if (Current == "(") left = Call(token, null, Arguments());
                else left = _scope.GetValueOrDefault(token);
            }
            while (Take("."))
            {
                var name = Current; _position++;
                left = Current == "(" ? Call(name, left, Arguments()) : Member(left, name);
            }
            while (Precedence(Current) >= minimum && Precedence(Current) > 0)
            {
                var op = Current; var precedence = Precedence(op); _position++;
                var right = Expression(precedence + 1);
                left = op switch
                {
                    "+" => Add(left, right), "-" => Number(left) - Number(right), "*" => Number(left) * Number(right),
                    "/" => Number(left) / Number(right), "%" => Number(left) % Number(right),
                    "==" or "===" => Equal(left, right), "!=" or "!==" => !Equal(left, right),
                    ">" => Number(left) > Number(right), "<" => Number(left) < Number(right),
                    ">=" => Number(left) >= Number(right), "<=" => Number(left) <= Number(right),
                    "&&" or "&" => Truth(left) && Truth(right), "||" or "|" => Truth(left) ? left : right,
                    _ => throw new FormatException("Unsupported semantic operator.")
                };
            }
            if (minimum == 0 && Take("?")) { var yes = Expression(); Expect(":"); var no = Expression(); return Truth(left) ? yes : no; }
            return left;
        }
        private List<object?> Arguments()
        {
            Expect("("); List<object?> arguments = [];
            if (Take(")")) return arguments;
            do { arguments.Add(Expression()); } while (Take(","));
            Expect(")"); return arguments;
        }
        private static object? Call(string name, object? receiver, List<object?> args) => name switch
        {
            "String" or "toString" => Text(receiver ?? args.FirstOrDefault()),
            "Number" or "parseInt" or "parseFloat" => Number(args.FirstOrDefault()),
            "concat" => Text(receiver) + string.Concat(args.Select(Text)),
            "toLowerCase" => Text(receiver).ToLowerInvariant(), "toUpperCase" => Text(receiver).ToUpperInvariant(),
            "trim" => Text(receiver).Trim(),
            "substring" or "substr" or "slice" => Text(receiver)[Math.Clamp((int)Number(args.FirstOrDefault()), 0, Text(receiver).Length)..],
            _ => throw new FormatException($"Unsupported semantic function {name}.")
        };
        private static object? Member(object? value, string name) => value is Dictionary<string, object?> dictionary
            ? dictionary.GetValueOrDefault(name) : name == "length" ? Text(value).Length : null;
        private static int Precedence(string op) => op switch
        { "||" or "|" => 1, "&&" or "&" => 2, "==" or "!=" or "===" or "!==" => 3, "<" or ">" or "<=" or ">=" => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, _ => 0 };
        private static bool Truth(object? value) => value switch { null => false, bool b => b, string s => s.Length > 0, double n => n != 0 && !double.IsNaN(n), _ => true };
        private static bool Equal(object? a, object? b) => Text(a) == Text(b);
        private static object Add(object? a, object? b) => a is string || b is string ? Text(a) + Text(b) : Number(a) + Number(b);
        private static double Number(object? value) => double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        private static string Text(object? value) => value is double number ? number.ToString(CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    }
}
