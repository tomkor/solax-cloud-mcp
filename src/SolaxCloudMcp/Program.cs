using SolaxCloudMcp;

// Entry point: TRANSPORT selects the MCP (stdio) server or the HTTP server.
// `SolaxCloudMcp consumption --since YYYY-MM-DD` prints consumption profiles instead.

// Fail fast when the OAuth2 credentials are missing, in every mode
Config.ClientId();
Config.ClientSecret();

// All app logs go to stderr: in stdio mode stdout carries the MCP protocol
AppLog.Factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(options => options.SingleLine = true)
    .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));

if (args is ["consumption", ..]) return await Consumption.RunAsync(args[1..]);

// `SolaxCloudMcp healthcheck` is for the Docker HEALTHCHECK: the runtime image has no curl
if (args is ["healthcheck"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        return (await http.GetAsync($"http://127.0.0.1:{Config.Int("HTTP_PORT", "8000")}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

switch (Config.Env("TRANSPORT", "stdio")!.ToLowerInvariant())
{
    case "http":
        HttpServer.Run();
        return 0;
    case "stdio":
        await McpTools.RunAsync();
        return 0;
    case var transport:
        throw new ConfigException($"Invalid TRANSPORT mode: {transport}. Must be 'stdio' (default) or 'http'");
}
