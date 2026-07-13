using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var b = Host.CreateApplicationBuilder(args);
b.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
b.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
_ = RoslynMcp.WorkflowStore.ListActive();
await b.Build().RunAsync();
