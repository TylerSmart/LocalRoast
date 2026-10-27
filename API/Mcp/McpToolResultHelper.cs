using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.API.Authentication;
using App.Core.Exceptions;
using ModelContextProtocol.Protocol;

namespace App.API.Mcp;

public static class McpToolResultHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<CallToolResult> ExecuteAsync(
        ClaimsPrincipal? user,
        Func<Task<object?>> action
    )
    {
        string? userId =
            user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;

        IDisposable? scope = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            scope = McpUserContextAccessor.BeginScope(userId);
        }

        try
        {
            var payload = await action();
            var json = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonOptions);
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = json }],
            };
        }
        catch (DomainValidationException ex)
        {
            return Error(ex.Message);
        }
        catch (NotFoundException ex)
        {
            return Error(ex.Message);
        }
        catch (ConflictException ex)
        {
            return Error(ex.Message);
        }
        catch (ForbiddenException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            return Error($"An error occurred executing the tool: {ex.Message}");
        }
        finally
        {
            scope?.Dispose();
        }
    }

    public static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
