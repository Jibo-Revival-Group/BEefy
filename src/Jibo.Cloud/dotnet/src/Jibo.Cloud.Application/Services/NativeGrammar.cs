using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public sealed record NativeParseResult(string Intent, string? Skill, string? Domain,
    IReadOnlyList<string> Rules, string? Priority, IReadOnlyDictionary<string, object?> Entities);

/// <summary>Managed reader/matcher for the pinned rule DSL. Grammar text is data, never executable code.</summary>
public sealed class NativeGrammar
{
    public static NativeGrammar Instance { get; } = new();
    private readonly Dictionary<string, Grammar> _grammars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Grammar> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string[][]>> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _equivalents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> _shared = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> RuleNames => _grammars.Keys;

    private sealed record Token(string Kind, string Value);
    private sealed class Node(string type, string value = "")
    {
        public string Type = type, Value = value;
        public List<Node> Children = [];
        public List<Tag> Tags = [];
        public double Cost;
    }
    private sealed record Tag(string Key, string Value, bool Append = false, bool Action = false);
    private sealed record Grammar(string Name, Dictionary<string, Node> Nodes, bool Equivalent);
    private sealed record State(int Position, Dictionary<string, object?> Values,
        Dictionary<string, Dictionary<string, object?>> Subs, double Cost = 0, int Specificity = 0, double Heuristic = 0, bool ExplicitHeuristic = false)
    {
        public State Copy() => this with { Values = CopyValues(Values), Subs = Subs.ToDictionary(p => p.Key, p => CopyValues(p.Value), StringComparer.Ordinal) };
        private static Dictionary<string, object?> CopyValues(Dictionary<string, object?> source) => source.ToDictionary(
            p => p.Key, p => p.Value is Dictionary<string, object?> nested ? CopyValues(nested) : p.Value, StringComparer.Ordinal);
    }

    private NativeGrammar()
    {
        const string root = "packages/nlu/resources/";
        var files = NativeConversationResources.Instance.Files;
        foreach (var (path, source) in files.Where(p => p.Key.StartsWith(root + "grammar/shared/", StringComparison.Ordinal)
            || p.Key.StartsWith(root + "grammar/globals/", StringComparison.Ordinal)))
            foreach (var entry in ReadSource(path, source)) _shared[entry.Key] = entry.Value;
        foreach (var (path, source) in files.Where(p => p.Key.StartsWith(root + "grammar/", StringComparison.Ordinal) && p.Key.EndsWith(".rule", StringComparison.Ordinal)))
        {
            var name = path[(root + "grammar/").Length..].Replace("skills/", "")[..^5];
            _grammars[name] = new(name, ReadSource(path, source), source.Contains("use_equivalent_words = true", StringComparison.Ordinal));
        }
        foreach (var (path, source) in files.Where(p => p.Key.StartsWith(root + "rules/@be/", StringComparison.Ordinal) && p.Key.EndsWith("launch.rule", StringComparison.Ordinal)))
        {
            var name = "fallback/" + path[(root + "rules/@be/").Length..][..^5];
            // The canonical grammar wins; older launch stubs are fallback-only.
            _grammars.TryAdd(name, new(name, ReadSource(path, source), false));
        }
        foreach (var (path, source) in files.Where(p => p.Key.StartsWith(root + "rules-src/", StringComparison.Ordinal) && p.Key.EndsWith(".rule", StringComparison.Ordinal)))
        {
            var name = path[(root + "rules-src/").Length..][..^5];
            _grammars.TryAdd(name, new(name, ReadSource(path, source), source.Contains("use_equivalent_words = true", StringComparison.Ordinal)));
        }
        foreach (var (path, source) in files.Where(p => (p.Key.StartsWith(root + "factory/", StringComparison.Ordinal)
            || p.Key.StartsWith(root + "factory-sources/", StringComparison.Ordinal)) && p.Key.EndsWith(".grm", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            _factories[name] = new(name, ReadSource(path, source), true);
        }
        foreach (var (path, source) in files.Where(p => p.Key.StartsWith(root + "factory-words/", StringComparison.Ordinal) && p.Key.EndsWith(".txt", StringComparison.Ordinal)))
        {
            _words[Path.GetFileNameWithoutExtension(path)] = source.Split('\n').Select(Tokenize).Where(w => w.Length > 0)
                .GroupBy(w => w[0]).ToDictionary(g => g.Key, g => g.OrderByDescending(w => w.Length).ToArray());
        }
        foreach (var line in files[root + "data/eq_words.txt"].Split('\n'))
        {
            var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) foreach (var word in words) _equivalents[word] = words[0];
        }
    }

    public NativeParseResult? Parse(string text, IReadOnlyList<string>? rules = null, IReadOnlyList<string>? loopNames = null)
    {
        var words = Tokenize(text);
        if (words.Length is 0 or > 80) return null;
        var selected = rules is { Count: > 0 } && !rules.Contains("launch")
            ? _grammars.Values.Where(g => rules.Contains(g.Name)).ToArray()
            : _grammars.Values.Where(g => g.Name.EndsWith("/launch", StringComparison.Ordinal)
                || g.Name.StartsWith("globals/", StringComparison.Ordinal) && g.Nodes.ContainsKey("TopRule")).ToArray();
        State? best = null; Grammar? bestGrammar = null; double score = double.NegativeInfinity;
        foreach (var grammar in selected)
        {
            if (!grammar.Nodes.TryGetValue("TopRule", out var top)) continue;
            var budget = new Budget(words, grammar, loopNames ?? []);
            var states = Match(top, new(0, new(StringComparer.Ordinal), new(StringComparer.Ordinal)), budget, 0);
            foreach (var state in states.Where(s => s.Position == words.Length))
            {
                var intent = StringValue(state.Values, "intent");
                var skill = StringValue(state.Values, "skill");
                if (string.IsNullOrEmpty(intent) && string.IsNullOrEmpty(skill)) continue;
                var priority = StringValue(state.Values, "priority");
                if (string.Equals(priority, "SKIP", StringComparison.OrdinalIgnoreCase)) continue;
                var candidateScore = state.Specificity - state.Cost - (grammar.Name.StartsWith("fallback/", StringComparison.Ordinal) ? 100 : 0);
                if (candidateScore <= score) continue;
                best = state; bestGrammar = grammar; score = candidateScore;
            }
        }
        if (best is null || bestGrammar is null) return null;
        var entities = best.Values.Where(p => !p.Key.StartsWith('_') && p.Key != "intent" && p.Key != "priority" && p.Value is not Dictionary<string, object?>)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var resultIntent = StringValue(best.Values, "intent") ?? "";
        var resultSkill = StringValue(best.Values, "skill");
        if (resultSkill is not null) entities["skill"] = resultSkill;
        if (resultIntent == "isJiboDescriptor" && entities.TryGetValue("GeneralDescriptor", out var descriptor))
        {
            if (Equals(descriptor, "Depressed")) { entities.Remove("GeneralDescriptor"); entities["Emotion"] = "Sad"; }
            if (Equals(descriptor, "GoodOrEvil")) { entities.Remove("GeneralDescriptor"); entities["JiboDescriptor"] = "GoodOrEvil"; }
        }
        return new(resultIntent, resultSkill, StringValue(entities, "domain"), rules is { Count: > 0 } ? rules : ["launch"],
            StringValue(best.Values, "priority"), entities);
    }

    private sealed class Budget(string[] words, Grammar grammar, IReadOnlyList<string> names)
    {
        public string[] Words = words;
        public Grammar Grammar = grammar;
        public IReadOnlyList<string> Names = names;
        public int Steps;
        public Dictionary<(Node, int, double, bool), List<State>> Cache = [];
        public HashSet<(Node, int, double, bool)> Active = [];
    }

    private List<State> Match(Node node, State input, Budget b, int depth)
    {
        if (depth > 100 || ++b.Steps > 250000) return [];
        var start = input.Position;
        List<State> output = [];
        switch (node.Type)
        {
            case "lit":
                var literal = Tokenize(node.Value);
                if (literal.Length > 0 && start + literal.Length <= b.Words.Length && literal.Select((w, i) => Equal(w, b.Words[start + i], b.Grammar.Equivalent)).All(v => v))
                    output.Add(input.Copy() with { Position = start + literal.Length, Specificity = input.Specificity + literal.Length, Cost = input.Cost + literal.Sum(w => Encoding.UTF8.GetByteCount(w) + 1) * input.Heuristic });
                break;
            case "class":
                if (start < b.Words.Length && ClassMatches(node.Value, b.Words[start]))
                    output.Add(input.Copy() with { Position = start + 1, Specificity = input.Specificity + 1, Cost = input.Cost + (Encoding.UTF8.GetByteCount(b.Words[start]) + 1) * input.Heuristic });
                break;
            case "heuristic": output.Add(input.Copy() with { Heuristic = double.Parse(node.Value, CultureInfo.InvariantCulture), ExplicitHeuristic = true }); break;
            case "star":
                var cap = int.TryParse(node.Value, out var max) ? max : b.Words.Length;
                for (var end = start; end <= Math.Min(b.Words.Length, start + cap); end++)
                    output.Add(input.Copy() with { Position = end, Cost = input.Cost + b.Words[start..end].Sum(w => Encoding.UTF8.GetByteCount(w)), Heuristic = 0, ExplicitHeuristic = true });
                break;
            case "seq":
                output = [input.Copy()];
                foreach (var child in node.Children)
                {
                    output = Prune(output.SelectMany(s => Match(child, s, b, depth + 1)));
                    if (output.Count == 0) break;
                }
                break;
            case "alt": output = Prune(node.Children.SelectMany(child => Match(child, input.Copy(), b, depth + 1))); break;
            case "opt": output = [input.Copy(), .. Match(node.Children[0], input, b, depth + 1)]; break;
            case "plus": case "kleene":
                var frontier = new List<State> { input.Copy() };
                if (node.Type == "kleene") output.Add(input.Copy());
                for (var count = 0; count < b.Words.Length && frontier.Count > 0; count++)
                {
                    frontier = Prune(frontier.SelectMany(s => Match(node.Children[0], s, b, depth + 1).Where(r => r.Position > s.Position)));
                    output.AddRange(frontier);
                }
                output = Prune(output);
                break;
            case "ref": output = MatchReference(node.Value, input, b, depth + 1); break;
        }
        foreach (var state in output)
        {
            state.Values["_parsed"] = string.Join(' ', b.Words[start..state.Position]);
            foreach (var tag in node.Tags)
            {
                if (tag.Action) { NativeSemanticAction.Apply(tag.Value, state.Values, state.Subs); continue; }
                object? value;
                if (tag.Value.StartsWith('\'')) value = tag.Value[1..];
                else if (tag.Value is "_parsed" or "this._parsed" or "this.parsed") value = state.Values["_parsed"];
                else if (tag.Value.Contains('.'))
                {
                    var pieces = tag.Value.Split('.', 2);
                    value = pieces[0] == "this" ? state.Values.GetValueOrDefault(pieces[1])
                        : state.Subs.GetValueOrDefault(pieces[0])?.GetValueOrDefault(pieces[1]);
                }
                else value = state.Values.GetValueOrDefault(tag.Value) ?? tag.Value;
                if (value is not null) state.Values[tag.Key] = tag.Append ? (state.Values.GetValueOrDefault(tag.Key)?.ToString() ?? "") + value : value;
            }
        }
        return output.Select(s => s with { Cost = s.Cost + node.Cost }).ToList();
    }

    private List<State> MatchReference(string name, State input, Budget b, int depth)
    {
        if (name == "w") return input.Position < b.Words.Length ? [input.Copy() with { Position = input.Position + 1, Cost = input.Cost + (input.ExplicitHeuristic ? (Encoding.UTF8.GetByteCount(b.Words[input.Position]) + 1) * input.Heuristic : Encoding.UTF8.GetByteCount(b.Words[input.Position])) }] : [];
        if ((name is "LOOPMEMBER" or "loopmember" or "factory:loop_member") && !b.Grammar.Nodes.ContainsKey(name))
            return b.Names.SelectMany(n => Match(new Node("lit", n), input, b, depth)).ToList();
        var factory = name.StartsWith("factory:", StringComparison.Ordinal);
        var reference = factory ? name[8..] : name.StartsWith("handle:", StringComparison.Ordinal) ? name[7..] : name;
        var grammar = factory && _factories.TryGetValue(reference, out var fg) ? fg : b.Grammar;
        Node? target = null;
        if (factory) grammar.Nodes.TryGetValue("TopRule", out target);
        else if (!grammar.Nodes.TryGetValue(reference, out target)) _shared.TryGetValue(reference, out target);
        var local = new State(input.Position, new(StringComparer.Ordinal), new(StringComparer.Ordinal), Heuristic: input.Heuristic, ExplicitHeuristic: input.ExplicitHeuristic);
        List<State> matches;
        if (factory && reference == "first_name" && b.Names.Any(n => Tokenize(n).SequenceEqual(b.Words.Skip(input.Position).Take(Tokenize(n).Length))))
            matches = b.Names.SelectMany(n => Match(new Node("lit", n), local, b, depth)).ToList();
        else if (factory && _words.TryGetValue(reference, out var vocabulary))
        {
            matches = [];
            if (input.Position < b.Words.Length && vocabulary.TryGetValue(b.Words[input.Position], out var alternatives))
                foreach (var words in alternatives)
                    matches.AddRange(Match(new Node("lit", string.Join(' ', words)), local, b, depth));
        }
        else if (target is not null)
        {
            var key = (target, input.Position, input.Heuristic, input.ExplicitHeuristic);
            if (!b.Cache.TryGetValue(key, out matches!))
            {
                if (!b.Active.Add(key)) return [];
                var previous = b.Grammar;
                b.Grammar = grammar;
                matches = Match(target, local, b, depth);
                b.Grammar = previous;
                b.Active.Remove(key);
                b.Cache[key] = matches;
            }
        }
        else return []; // An unknown factory must never become a permissive wildcard.
        return matches.Select(match =>
        {
            var state = input.Copy() with { Position = match.Position, Cost = input.Cost + match.Cost, Specificity = input.Specificity + match.Specificity, Heuristic = match.Heuristic, ExplicitHeuristic = match.ExplicitHeuristic };
            state.Subs[reference] = new(match.Values, StringComparer.Ordinal);
            foreach (var pair in match.Values.Where(p => !p.Key.StartsWith('_'))) state.Values[pair.Key] = pair.Value;
            return state;
        }).ToList();
    }

    private static List<State> Prune(IEnumerable<State> states) => states
        .GroupBy(s => (s.Position, s.Heuristic, s.ExplicitHeuristic, Signature(s.Values), SignatureSubs(s.Subs)))
        .Select(g => g.OrderBy(s => s.Cost - s.Specificity).First()).Take(512).ToList();
    private static string Signature(Dictionary<string, object?> values) => string.Join('\u001f', values.Where(p => p.Key != "_parsed").OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
    private static string SignatureSubs(Dictionary<string, Dictionary<string, object?>> values) => string.Join('\u001e', values.OrderBy(p => p.Key).Select(p => p.Key + Signature(p.Value)));
    private bool Equal(string a, string b, bool equivalent) => a == b || equivalent && _equivalents.TryGetValue(a, out var canonical) && _equivalents.GetValueOrDefault(b) == canonical;
    internal static string? StringValue(IReadOnlyDictionary<string, object?> values, string key) => values.GetValueOrDefault(key)?.ToString();
    internal static string[] Tokenize(string text) => Regex.Matches(text.ToLowerInvariant().Replace('’', '\''), @"[\p{L}\p{N}_]+(?:[':.&/-][\p{L}\p{N}_]+)*")
        .Select(m => m.Value).ToArray();

    private static bool ClassMatches(string body, string word)
    {
        // Bracket expressions concatenate characters; ? is a prefix optional operator.
        var index = 0;
        string Expression(char stop = '\0')
        {
            var result = new StringBuilder();
            while (index < body.Length && body[index] != stop)
            {
                var c = body[index++];
                if (char.IsWhiteSpace(c)) continue;
                if (c == '|') { result.Append('|'); continue; }
                var optional = c == '?';
                if (optional && index < body.Length) c = body[index++];
                string atom;
                if (c == '(') { atom = "(?:" + Expression(')') + ")"; index++; }
                else if (c == '\\' && index < body.Length) atom = Regex.Escape(body[index++].ToString());
                else atom = Regex.Escape(c.ToString());
                result.Append(optional ? "(?:" + atom + ")?" : atom);
            }
            return result.ToString();
        }
        return Regex.IsMatch(word, "^(?:" + Expression() + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    private static Dictionary<string, Node> ReadSource(string path, string source)
    {
        try { return Read(source); }
        catch (FormatException error) { throw new FormatException($"Invalid native grammar {path}: {error.Message}", error); }
    }

    private static Dictionary<string, Node> Read(string source)
    {
        var tokens = Lex(source); var position = 0;
        Token Peek() => tokens[position];
        Token Eat(string kind)
        {
            var token = tokens[position++];
            if (token.Kind != kind) throw new FormatException($"Grammar expected {kind}, found {token.Kind} ({token.Value}).");
            return token;
        }
        Node Expression()
        {
            List<Node> items = [];
            while (Peek().Kind is "id" or "string" or "(" or "ref" or "star" or "+" or "class" or "?" or "*" or "weight" or ",")
            {
                var item = Item();
                if (Peek().Kind == "|")
                {
                    var alt = new Node("alt"); alt.Children.Add(item);
                    while (Peek().Kind == "|") { Eat("|"); alt.Children.Add(Item()); }
                    item = alt;
                }
                items.Add(item);
            }
            if (items.Count == 0) throw new FormatException("Empty grammar sequence.");
            if (items.Count == 1) return items[0];
            var seq = new Node("seq") { Children = items };
            if (items[^1].Type is not ("plus" or "kleene")) { seq.Tags = items[^1].Tags; items[^1].Tags = []; }
            return seq;
        }
        Node Item()
        {
            if (Peek().Kind == "weight") return new Node("heuristic", Eat("weight").Value);
            var prefixes = new List<string>();
            while (Peek().Kind is "?" or "+" or "*") prefixes.Add(tokens[position++].Kind);
            var token = tokens[position++];
            Node atom = token.Kind switch
            {
                "(" => Expression(), "ref" => new("ref", token.Value), "star" => new("star", token.Value),
                "id" or "string" => new("lit", token.Value), "," => new("lit", ","), "class" => token.Value.IndexOfAny(['$', '*', '+', '{']) >= 0
                    ? Read("TopRule = (" + token.Value + ");")["TopRule"] : new("class", token.Value),
                _ => throw new FormatException($"Unexpected grammar atom {token.Kind} ({token.Value}).")
            };
            if (token.Kind == "(") Eat(")");
            while (Peek().Kind is "{" or "action" or "cost")
            {
                if (Peek().Kind == "cost") { atom.Cost += double.Parse(Eat("cost").Value, CultureInfo.InvariantCulture); continue; }
                if (Peek().Kind == "action") { atom.Tags.Add(new("", Eat("action").Value, Action: true)); continue; }
                Eat("{");
                while (Peek().Kind != "}")
                {
                    var key = Eat("id").Value;
                    var op = tokens[position++].Kind;
                    if (op is not ("=" or "+=")) throw new FormatException("Invalid tag assignment.");
                    var value = tokens[position++];
                    atom.Tags.Add(new(key, value.Kind == "string" ? "'" + value.Value : value.Value, op == "+="));
                    if (Peek().Kind == ",") Eat(",");
                }
                Eat("}");
            }
            foreach (var prefix in prefixes.AsEnumerable().Reverse())
            {
                var wrapper = new Node(prefix == "?" ? "opt" : prefix == "+" ? "plus" : "kleene") { Children = [atom] };
                if (prefix != "?") { wrapper.Tags = atom.Tags; atom.Tags = []; wrapper.Cost = atom.Cost; atom.Cost = 0; }
                atom = wrapper;
            }
            return atom;
        }
        var rules = new Dictionary<string, Node>(StringComparer.Ordinal);
        while (Peek().Kind != "eof")
        {
            if (Peek().Kind == "directive") { position++; continue; }
            var name = Eat("id").Value;
            Node node;
            if (Peek().Kind == "class") node = Item();
            else { Eat("="); node = Expression(); }
            Eat(";"); rules[name] = node;
        }
        return rules;
    }

    private static List<Token> Lex(string source)
    {
        source = source.Replace('\u00a0', ' ').Replace('’', '\'').Replace('‘', '\'');
        var tokens = new List<Token>(); var i = 0;
        string Until(char end)
        {
            var value = new StringBuilder();
            while (i < source.Length && source[i] != end)
            {
                if (source[i] == '\\' && i + 1 < source.Length) i++;
                value.Append(source[i++]);
            }
            if (i < source.Length) i++;
            return value.ToString();
        }
        while (i < source.Length)
        {
            var c = source[i++];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '#' || c == '/' && i < source.Length && source[i] == '/') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (c == '!') { tokens.Add(new("directive", Until(';'))); continue; }
            if (c == '\'') { tokens.Add(new("string", Until('\''))); continue; }
            if (c == '[')
            {
                var start = i; var nesting = 1;
                while (i < source.Length && nesting > 0) { if (source[i] == '[') nesting++; if (source[i] == ']') nesting--; i++; }
                tokens.Add(new("class", source[start..(i - 1)])); continue;
            }
            if (c == '{' && i < source.Length && source[i] == '%')
            {
                var start = ++i; var end = source.IndexOf("%}", i, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("Unclosed grammar semantic action.");
                tokens.Add(new("action", source[start..end])); i = end + 2; continue;
            }
            if (c == '$')
            {
                if (i < source.Length && source[i] == '*') { i++; tokens.Add(new("star", "")); continue; }
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || "_:-".Contains(source[i]))) i++;
                var name = source[start..i];
                tokens.Add(name.Length > 1 && name[0] == 'w' && int.TryParse(name[1..], out var n) ? new("star", n.ToString()) : new("ref", name)); continue;
            }
            if (c is '~' or '<')
            {
                var start = i; while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
                tokens.Add(new(c == '~' ? "cost" : "weight", source[start..i])); if (c == '<' && i < source.Length && source[i] == '>') i++; continue;
            }
            if (c == '+' && i < source.Length && source[i] == '=') { i++; tokens.Add(new("+=", "")); continue; }
            if (c == '@' && i < source.Length && source[i] == '=') { i++; tokens.Add(new("=", "")); continue; }
            if ("(){}|?=;,+*".Contains(c)) { tokens.Add(new(c.ToString(), "")); continue; }
            var word = new StringBuilder(); word.Append(c);
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && !"(){}|?=;,+*[]<>~".Contains(source[i]))
            {
                if (source[i] == '\\' && i + 1 < source.Length) i++;
                word.Append(source[i++]);
            }
            tokens.Add(new("id", word.ToString().Replace("\\", "")));
        }
        tokens.Add(new("eof", "")); return tokens;
    }
}
