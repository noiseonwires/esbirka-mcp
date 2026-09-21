using System.Net;
using ESbirka.Client;
using ESbirka.Mcp;
using ESbirka.Mcp.Transports;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

var httpMode = args is ["http"];
if (httpMode)
{
    var webBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    ConfigureLogging(webBuilder.Logging);
    ConfigureServices(webBuilder.Services, webBuilder.Configuration);
    webBuilder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<LawTools>();
    await using var app = webBuilder.Build();
    var httpUrl = webBuilder.Configuration["Mcp:HttpUrl"] ?? "http://127.0.0.1:3001";
    var httpPath = webBuilder.Configuration["Mcp:HttpPath"] ?? "/mcp";
    if (!httpPath.StartsWith('/'))
        throw new InvalidOperationException("Mcp:HttpPath must start with '/'.");
    app.Urls.Add(httpUrl);
    app.MapMcp(httpPath);
    await app.RunAsync();
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
ConfigureLogging(builder.Logging);
ConfigureServices(builder.Services, builder.Configuration);
if (args.Length == 0)
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LawTools>();

using var host = builder.Build();
if (args.Length > 0)
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

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddESbirkaClient(options => configuration.GetSection("ESbirka").Bind(options));
    var transport = new TransportOptions();
    configuration.GetSection("Transport").Bind(transport);
    services.AddSingleton(transport);
    services.AddSingleton(_ =>
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
    services.AddSingleton(provider => new HttpContentFetcher(provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<ESbirkaClientOptions>().MaximumResponseBytes)
    {
        UserAgent = transport.UserAgent
    });
    services.AddSingleton<IESbirkaContentFetcher>(provider => provider.GetRequiredService<HttpContentFetcher>());
    services.AddSingleton<ESbirkaClient>();
    services.AddSingleton<IESbirkaClient>(provider => provider.GetRequiredService<ESbirkaClient>());
    services.AddSingleton<IESbirkaCacheAdministration>(provider => provider.GetRequiredService<ESbirkaClient>());
}