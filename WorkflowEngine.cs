using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RoslynMcp;

static class WorkflowEngine
{
    private static readonly Lazy<Dictionary<string, MethodInfo>> ToolMethods = new(DiscoverToolMethods);

    private static Dictionary<string, MethodInfo> DiscoverToolMethods()
    {
        var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in typeof(WorkflowEngine).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    map[method.Name] = method;
            }
        }
        return map;
    }

    public static object? RunStep(WorkflowStep step, Dictionary<string, string> bindings)
    {
        if (!ToolMethods.Value.TryGetValue(step.Tool, out var m))
            throw new MissingMethodException($"no tool method '{step.Tool}'");

        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var paramName = ps[i].Name!;
            if (!step.Args.TryGetValue(paramName, out var raw))
            {
                if (ps[i].HasDefaultValue)
                {
                    args[i] = ps[i].DefaultValue;
                    continue;
                }
                throw new ArgumentException($"step '{step.Tool}' missing required arg '{paramName}'");
            }

            var sub = Regex.Replace(raw, @"\{\{(\$?\w+)\}\}",
                mm => bindings.TryGetValue(mm.Groups[1].Value, out var v) ? v
                    : throw new ArgumentException($"unbound param '{mm.Groups[1].Value}'"));

            var targetType = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
            if (targetType == typeof(string[]))
            {
                args[i] = JsonSerializer.Deserialize<string[]>(sub) ?? Array.Empty<string>();
            }
            else
            {
                args[i] = Convert.ChangeType(sub, targetType);
            }
        }

        var result = m.Invoke(null, args);
        return result is Task t ? UnwrapTask(t) : result;
    }

    static object? UnwrapTask(Task t)
    {
        t.GetAwaiter().GetResult();
        var resultProp = t.GetType().GetProperty("Result");
        return resultProp?.GetValue(t);
    }

    public static string Run(Workflow w, Dictionary<string, string> paramValues)
    {
        foreach (var p in w.Params)
            if (!paramValues.ContainsKey(p))
                throw new ArgumentException($"workflow '{w.Name}' missing param '{p}'");

        var bindings = new Dictionary<string, string>(paramValues, StringComparer.Ordinal);
        var log = new List<object>();

        for (int idx = 0; idx < w.Steps.Length; idx++)
        {
            var step = w.Steps[idx];
            try
            {
                var r = RunStep(step, bindings);
                var rStr = r?.ToString() ?? "";
                bindings["$prev"] = rStr;
                bindings[$"$step_{idx}"] = rStr;

                // If step returned JSON with a 'text' or 'code' property, also expose {{$prev_text}} for easy chaining!
                TryExtractTextProperty(rStr, bindings);

                log.Add(new { index = idx, step = step.Tool, ok = true, result = r });
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
                log.Add(new { index = idx, step = step.Tool, ok = false, error = inner.Message });
                break;
            }
        }

        return JsonSerializer.Serialize(new { workflow = w.Name, log }, new JsonSerializerOptions { WriteIndented = false });
    }

    private static void TryExtractTextProperty(string jsonOrText, Dictionary<string, string> bindings)
    {
        if (!jsonOrText.StartsWith('{')) return;
        try
        {
            using var doc = JsonDocument.Parse(jsonOrText);
            if (doc.RootElement.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                bindings["$prev_text"] = tProp.GetString() ?? "";
            else if (doc.RootElement.TryGetProperty("code", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                bindings["$prev_text"] = cProp.GetString() ?? "";
            else if (doc.RootElement.TryGetProperty("primaryUpdatedSource", out var uProp) && uProp.ValueKind == JsonValueKind.String)
                bindings["$prev_text"] = uProp.GetString() ?? "";
        }
        catch
        {
            // Not JSON or no text property
        }
    }
}
