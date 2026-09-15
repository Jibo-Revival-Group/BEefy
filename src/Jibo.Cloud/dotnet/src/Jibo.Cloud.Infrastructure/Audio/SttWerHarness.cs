using System.Text;
using System.Text.Json;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Lightweight WER helper for comparing STT engines against reference transcripts.
/// </summary>
public static class SttWerHarness
{
    public sealed record CaseResult(
        string Name,
        string Reference,
        string Hypothesis,
        double Wer,
        int Substitutions,
        int Deletions,
        int Insertions);

    public sealed record Summary(
        int CaseCount,
        double AverageWer,
        IReadOnlyList<CaseResult> Cases);

    public static double ComputeWer(string reference, string hypothesis)
    {
        var refTokens = Tokenize(reference);
        var hypTokens = Tokenize(hypothesis);
        if (refTokens.Length == 0)
            return hypTokens.Length == 0 ? 0 : 1;

        var (substitutions, deletions, insertions) = Align(refTokens, hypTokens);
        return (substitutions + deletions + insertions) / (double)refTokens.Length;
    }

    public static Summary Evaluate(IEnumerable<(string Name, string Reference, string Hypothesis)> cases)
    {
        var results = cases
            .Select(item =>
            {
                var refTokens = Tokenize(item.Reference);
                var hypTokens = Tokenize(item.Hypothesis);
                var (substitutions, deletions, insertions) = Align(refTokens, hypTokens);
                var wer = refTokens.Length == 0
                    ? hypTokens.Length == 0 ? 0 : 1
                    : (substitutions + deletions + insertions) / (double)refTokens.Length;
                return new CaseResult(item.Name, item.Reference, item.Hypothesis, wer, substitutions, deletions,
                    insertions);
            })
            .ToArray();

        return new Summary(
            results.Length,
            results.Length == 0 ? 0 : results.Average(result => result.Wer),
            results);
    }

    public static string FormatMarkdown(Summary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Case | WER | S | D | I | Reference | Hypothesis |");
        builder.AppendLine("|---|---:|---:|---:|---:|---|---|");
        foreach (var item in summary.Cases)
        {
            builder.Append("| ")
                .Append(Escape(item.Name)).Append(" | ")
                .Append(item.Wer.ToString("0.000")).Append(" | ")
                .Append(item.Substitutions).Append(" | ")
                .Append(item.Deletions).Append(" | ")
                .Append(item.Insertions).Append(" | ")
                .Append(Escape(item.Reference)).Append(" | ")
                .Append(Escape(item.Hypothesis)).AppendLine(" |");
        }

        builder.AppendLine();
        builder.Append("Average WER: ").AppendLine(summary.AverageWer.ToString("0.000"));
        return builder.ToString();
    }

    public static Summary EvaluateFromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var cases = new List<(string, string, string)>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            cases.Add((
                element.TryGetProperty("name", out var name) ? name.GetString() ?? "case" : "case",
                element.TryGetProperty("reference", out var reference) ? reference.GetString() ?? "" : "",
                element.TryGetProperty("hypothesis", out var hypothesis) ? hypothesis.GetString() ?? "" : ""));
        }

        return Evaluate(cases);
    }

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static (int Substitutions, int Deletions, int Insertions) Align(string[] reference, string[] hypothesis)
    {
        var rows = reference.Length + 1;
        var cols = hypothesis.Length + 1;
        var distance = new int[rows, cols];
        var back = new byte[rows, cols]; // 0=sub/match, 1=del, 2=ins

        for (var i = 0; i < rows; i++)
        {
            distance[i, 0] = i;
            back[i, 0] = 1;
        }

        for (var j = 0; j < cols; j++)
        {
            distance[0, j] = j;
            back[0, j] = 2;
        }

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal) ? 0 : 1;
                var sub = distance[i - 1, j - 1] + cost;
                var del = distance[i - 1, j] + 1;
                var ins = distance[i, j - 1] + 1;
                if (sub <= del && sub <= ins)
                {
                    distance[i, j] = sub;
                    back[i, j] = 0;
                }
                else if (del <= ins)
                {
                    distance[i, j] = del;
                    back[i, j] = 1;
                }
                else
                {
                    distance[i, j] = ins;
                    back[i, j] = 2;
                }
            }
        }

        var substitutions = 0;
        var deletions = 0;
        var insertions = 0;
        var r = reference.Length;
        var h = hypothesis.Length;
        while (r > 0 || h > 0)
        {
            var op = back[r, h];
            if (r > 0 && h > 0 && op == 0)
            {
                if (!string.Equals(reference[r - 1], hypothesis[h - 1], StringComparison.Ordinal))
                    substitutions++;
                r--;
                h--;
            }
            else if (r > 0 && (h == 0 || op == 1))
            {
                deletions++;
                r--;
            }
            else
            {
                insertions++;
                h--;
            }
        }

        return (substitutions, deletions, insertions);
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
