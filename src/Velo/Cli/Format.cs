using System.Globalization;
using System.Text;

namespace Velo;

public static partial class Cli
{
    private static string Status(Issue i) =>
        i.Status == IssueStatus.Review && i.Runs.Count > 0 ? "review/" + Engine.Name(i.Runs[^1].Status) : Engine.Name(i.Status);

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Ago(DateTimeOffset t)
    {
        TimeSpan d = DateTimeOffset.UtcNow - t;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago" : d.TotalHours < 48 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalDays}d ago";
    }

    private static string DirName(string? dir)
    {
        if (dir is null) return "-";
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (name.Length == 0) name = dir;
        return name.Length > 18 ? name[..17] + "…" : name;
    }

    /// <summary>Needs-attention first, like the web list: review, running, queued, done.</summary>
    private static string FormatList(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0) return "no issues";
        static int Rank(Issue i) => i.Status switch { IssueStatus.Review => 0, IssueStatus.Running => 1, IssueStatus.Todo => 2, _ => 3 };
        var sorted = issues.OrderBy(Rank)
            .ThenBy(i => Rank(i) is 1 or 2 ? i.Number : 0)
            .ThenByDescending(i => Rank(i) is 1 or 2 ? default : i.UpdatedAt).ToList();
        string[] status = [.. sorted.Select(Status)], when = [.. sorted.Select(i => Ago(i.UpdatedAt))], dir = [.. sorted.Select(i => DirName(i.Directory))];
        int sw = Math.Max(status.Max(s => s.Length), 6) + 2, ww = Math.Max(when.Max(s => s.Length), 7) + 2, dw = Math.Max(dir.Max(s => s.Length), 3) + 2;
        var sb = new StringBuilder($"{"KEY",-8}{"STATUS".PadRight(sw)}{"RUNS",-6}{"UPDATED".PadRight(ww)}{"DIR".PadRight(dw)}TITLE\n");
        for (int n = 0; n < sorted.Count; n++)
            sb.Append($"{sorted[n].Key,-8}{status[n].PadRight(sw)}{sorted[n].Runs.Count,-6}{when[n].PadRight(ww)}{dir[n].PadRight(dw)}{sorted[n].Title}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line on how approving would go, e.g. "clean into main (main has 2 newer commits)".</summary>
    private static string FormatMerge(MergeCheck m) => m.Conflicts switch
    {
        { Count: > 0 } files => $"conflicts with {m.Target} in {string.Join(", ", files)}",
        null => $"unknown (git too old to tell) - {m.Ahead} commit(s) ahead of {m.Target}",
        _ => $"clean into {m.Target}" + (m.Behind > 0 ? $" ({m.Target} has {m.Behind} newer commit{(m.Behind == 1 ? "" : "s")})" : ""),
    };

    private static string FormatIssue(Issue i, VeloPaths paths, string? merge = null)
    {
        var sb = new StringBuilder($"{i.Key}  {i.Title}\n");
        sb.Append($"status:    {Status(i)}\n");
        if (merge is not null) sb.Append($"merge:     {merge}\n");
        sb.Append($"directory: {i.Directory ?? "(none)"}\n");
        sb.Append($"workspace: {paths.Workspace(i.Number)}\n");
        if (i.Model is not null || i.Effort is not null) sb.Append($"model:     {i.Model ?? "(default)"}, effort {i.Effort ?? "(default)"}\n");
        if (i.Message is not null) sb.Append($"follow-up: {i.Message}\n");
        sb.Append($"created:   {Time(i.CreatedAt)}   updated: {Time(i.UpdatedAt)}\n");
        if (i.Description.Length > 0) sb.Append('\n').Append(i.Description).Append('\n');
        if (i.Runs.Count > 0) sb.Append("\nruns:\n");
        foreach (Run r in i.Runs)
        {
            string end = r.EndedAt is { } e ? " - " + Time(e) : "";
            sb.Append($"  #{r.Number} {Engine.Name(r.Status)}  {Time(r.StartedAt)}{end}  {r.Tokens} tokens\n");
            if (r.Message is not null) sb.Append($"     follow-up: {r.Message.ReplaceLineEndings("\n     ")}\n");
            if (r.Summary is not null) sb.Append($"     {r.Summary.Trim().ReplaceLineEndings("\n     ")}\n");
        }
        if (i.TakeOver is { } takeOver)
            sb.Append($"\ntake over: cd \"{paths.Workspace(i.Number)}\" && {takeOver}\n");
        return sb.ToString().TrimEnd();
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }
}
