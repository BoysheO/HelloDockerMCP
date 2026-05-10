using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// var builder = Host.CreateApplicationBuilder(args);

// // MCP stdio 模式要求普通日志不要写到 stdout。
// // stdout 要留给 MCP 协议通信。
// builder.Logging.AddConsole(options =>
// {
//     options.LogToStandardErrorThreshold = LogLevel.Trace;
// });

// builder.Services.AddSingleton<DockerGuard>();
// builder.Services.AddSingleton<DockerService>();

// builder.Services
//     .AddMcpServer()
//     .WithStdioServerTransport()
//     .WithToolsFromAssembly();

// await builder.Build().RunAsync();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DockerGuard>();
builder.Services.AddSingleton<DockerService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();