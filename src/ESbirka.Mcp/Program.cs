using System.Net;
using ESbirka.Client;
using ESbirka.Mcp;
using ESbirka.Mcp.Transports;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

var httpMode = args is ["http"];
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddESbirkaClient(options => builder.Configuration.GetSection("ESbirka").Bind(options));
var transport = new TransportOptions();
builder.Configuration.GetSection("Transport").Bind(transport);
builder.Services.AddSingleton(transport);
builder.Services.AddSingleton(provider =>
{
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };
    if (transport.Proxy is not null) handler.Proxy = new WebProxy(transport.Proxy);
    return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
});
builder.Services.AddSingleton(provider => new HttpContentFetcher(provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<ESbirkaClientOptions>().MaximumResponseBytes)
{
    UserAgent = transport.UserAgent
});
builder.Services.AddSingleton<IESbirkaContentFetcher>(provider => provider.GetRequiredService<HttpContentFetcher>());
builder.Services.AddSingleton<ESbirkaClient>();
builder.Services.AddSingleton<IESbirkaClient>(provider => provider.GetRequiredService<ESbirkaClient>());
builder.Services.AddSingleton<IESbirkaCacheAdministration>(provider => provider.GetRequiredService<ESbirkaClient>());
if (httpMode)
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<LawTools>();
else if (args.Length == 0)
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LawTools>();

await using var host = builder.Build();
if (httpMode)
{
    var httpUrl = builder.Configuration["Mcp:HttpUrl"] ?? "http://127.0.0.1:3001";
    var httpPath = builder.Configuration["Mcp:HttpPath"] ?? "/mcp";
    if (!httpPath.StartsWith('/'))
        throw new InvalidOperationException("Mcp:HttpPath must start with '/'.");
    host.Urls.Add(httpUrl);
    host.MapMcp(httpPath);
}
else if (args.Length > 0)
{
    try { return await CacheCommands.RunAsync(args, host.Services.GetRequiredService<IESbirkaCacheAdministration>()); }
    catch (ESbirkaException exception)
    {
        Console.Error.WriteLine(exception.Code + ": " + exception.Message);
        return 1;
    }
}
await host.RunAsync();
return 0;