using System.Diagnostics;
using System.Text.Json;

namespace RoslynMcp;

[DebuggerDisplay("{ToString(),nq}")]
record WorkflowStep(string Tool, Dictionary<string, string> Args)
{
    public override string ToString() =>
        $"WorkflowStep({Tool}({string.Join(", ", (Args ?? new()).Select(kv => $"{kv.Key}={kv.Value}"))}))";
}

[DebuggerDisplay("{ToString(),nq}")]
record Workflow(string Name, string[] Params, WorkflowStep[] Steps, bool HasExecute, bool Confirmed)
{
    public override string ToString() =>
        $"Workflow({Name}, params=[{string.Join(", ", Params ?? Array.Empty<string>())}], steps=[{string.Join(" -> ", (Steps ?? Array.Empty<WorkflowStep>()).Select(s => s.Tool))}], confirmed={Confirmed})";
}

static class WorkflowStore
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "workflows");
    static readonly Dictionary<string, Workflow> Active = new(StringComparer.Ordinal);
    static readonly Dictionary<string, Workflow> Pending = new(StringComparer.Ordinal);

    static WorkflowStore() { Directory.CreateDirectory(Dir); Load(); }

    static void Load()
    {
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            Workflow? w;
            try { w = JsonSerializer.Deserialize<Workflow>(File.ReadAllText(f)); }
            catch (JsonException) { continue; }
            if (w is null) continue;
            if (w.HasExecute && !w.Confirmed) { Pending[w.Name] = w; continue; }
            Active[w.Name] = w;
        }
    }

    public static bool Exists(string name) =>
        Active.ContainsKey(name) || Pending.Values.Any(w => w.Name == name) || Pending.ContainsKey(name);

    public static string Stage(string name, string[] pars, WorkflowStep[] steps)
    {
        if (Exists(name)) throw new InvalidOperationException($"workflow '{name}' already exists");
        var hasExec = steps.Any(s => string.Equals(s.Tool, "Execute", StringComparison.OrdinalIgnoreCase));
        var w = new Workflow(name, pars, steps, hasExec, Confirmed: !hasExec);
        if (!hasExec) { Active[name] = w; Persist(w); return "active"; }
        var token = Guid.NewGuid().ToString("N")[..12];
        Pending[token] = w with { Name = name };
        return token;
    }

    public static Workflow Confirm(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !Pending.Remove(token, out var w))
            throw new KeyNotFoundException("no pending workflow for token");
        var confirmed = w with { Confirmed = true };
        Active[w.Name] = confirmed;
        Persist(confirmed);
        return confirmed;
    }

    public static Workflow Get(string name) =>
        !string.IsNullOrWhiteSpace(name) && Active.TryGetValue(name, out var w)
            ? w
            : throw new KeyNotFoundException($"no active workflow '{name}'");

    public static IEnumerable<Workflow> ListActive() => Active.Values;

    internal static void ClearInMemoryForTesting()
    {
        Active.Clear();
        Pending.Clear();
    }

    static void Persist(Workflow w) =>
        File.WriteAllText(Path.Combine(Dir, $"{w.Name}.json"), JsonSerializer.Serialize(w));
}
