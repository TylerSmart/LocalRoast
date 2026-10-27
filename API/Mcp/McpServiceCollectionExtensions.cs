using ModelContextProtocol.Protocol;

namespace App.API.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddAppMcpServer(this IServiceCollection services)
    {
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "LocalRoast Household MCP Server",
                    Version = "1.0.0",
                };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithTools<CookbookMcpTools>()
            .WithTools<IngestionMcpTools>();

        return services;
    }
}
