using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcp.Tests;

if (args.Length > 0 && string.Equals(args[0], "--test", StringComparison.OrdinalIgnoreCase))
{
    var filter = args.Length > 1 ? args[1] : null;
    var report = await ToolTestSuite.RunAllAsync(filter);
    Console.WriteLine(report.ToString());
    foreach (var m in report.MethodCoverage)
        Console.WriteLine($"  {m}");
    foreach (var fail in report.Results.Where(r => !r.Passed))
        Console.Error.WriteLine($"  {fail}");
    Environment.ExitCode = report.AllPassed ? 0 : 1;
    return;
}

var b = Host.CreateApplicationBuilder(args);
b.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
b.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
_ = RoslynMcp.WorkflowStore.ListActive();
await b.Build().RunAsync();
