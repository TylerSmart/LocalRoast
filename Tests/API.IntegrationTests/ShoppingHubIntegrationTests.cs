using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class ShoppingHubIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<AuthResponse> RegisterAndAuthorizeAsync(
        string email,
        string username,
        string password
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, username, password)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    private HubConnection CreateHubConnection(string? token)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(_client.BaseAddress!, "/api/hubs/shopping"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    }
                }
            )
            .Build();
    }

    [TestMethod]
    public async Task ShoppingHub_WhenUnauthenticated_FailsToConnect()
    {
        await using var hubConnection = CreateHubConnection(null);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await hubConnection.StartAsync()
        );
    }

    [TestMethod]
    public async Task ShoppingHub_WhenUsingPat_FailsToConnect()
    {
        await RegisterAndAuthorizeAsync("patuser@example.com", "patuser", "Password123!");
        var patRes = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("Test PAT")
        );
        var createdPat = await patRes.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(createdPat);

        await using var hubConnection = CreateHubConnection(createdPat.PlaintextToken);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await hubConnection.StartAsync()
        );
    }

    [TestMethod]
    public async Task ShoppingHub_WhenAuthenticated_ReceivesCatalogAndListEvents()
    {
        var auth = await RegisterAndAuthorizeAsync(
            "liveuser@example.com",
            "liveuser",
            "Password123!"
        );

        // 1. Create a list via REST
        var createListRes = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Trader Joe's", true)
        );
        var createdList = await createListRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(createdList);

        // 2. Connect client A
        await using var connectionA = CreateHubConnection(auth.Token);
        var catalogEvents = new List<ShoppingListsCatalogChangedDto>();
        var listEvents = new List<ShoppingListRealtimeEventDto>();

        connectionA.On<ShoppingListsCatalogChangedDto>(
            "ShoppingListsCatalogChanged",
            e => catalogEvents.Add(e)
        );
        connectionA.On<ShoppingListRealtimeEventDto>("ShoppingListUpdated", e => listEvents.Add(e));

        await connectionA.StartAsync();
        await connectionA.InvokeAsync("JoinShoppingList", createdList.Id);

        // 3. Add an item via REST to the list
        var addItemRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{createdList.Id}/items",
            new CreateShoppingListItemRequest("Bananas", 6, null, MeasurementUnit.Whole)
        );
        Assert.AreEqual(HttpStatusCode.Created, addItemRes.StatusCode);

        // Allow SignalR message loop to deliver event
        for (var i = 0; i < 20 && listEvents.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.IsGreaterThanOrEqualTo(1, listEvents.Count);
        Assert.AreEqual(ShoppingRealtimeEventType.ItemAdded, listEvents[0].EventType);
        Assert.AreEqual("Bananas", listEvents[0].ChangedItem?.DisplayName);
        Assert.IsGreaterThanOrEqualTo(1, catalogEvents.Count);
    }

    [TestMethod]
    public async Task ShoppingHub_GroupIsolation_OnlyJoinedClientsReceiveListUpdated()
    {
        var auth = await RegisterAndAuthorizeAsync(
            "groupuser@example.com",
            "groupuser",
            "Password123!"
        );

        var list1Res = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("List 1", false)
        );
        var list1 = await list1Res.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list1);

        var list2Res = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("List 2", false)
        );
        var list2 = await list2Res.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list2);

        // Connect Client 1 watching List 1
        await using var conn1 = CreateHubConnection(auth.Token);
        var list1Events = new List<ShoppingListRealtimeEventDto>();
        conn1.On<ShoppingListRealtimeEventDto>("ShoppingListUpdated", e => list1Events.Add(e));
        await conn1.StartAsync();
        await conn1.InvokeAsync("JoinShoppingList", list1.Id);

        // Connect Client 2 watching List 2
        await using var conn2 = CreateHubConnection(auth.Token);
        var list2Events = new List<ShoppingListRealtimeEventDto>();
        conn2.On<ShoppingListRealtimeEventDto>("ShoppingListUpdated", e => list2Events.Add(e));
        await conn2.StartAsync();
        await conn2.InvokeAsync("JoinShoppingList", list2.Id);

        // Add item to List 1 only
        await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list1.Id}/items",
            new CreateShoppingListItemRequest("Apples", 4, null, MeasurementUnit.Whole)
        );

        for (var i = 0; i < 20 && list1Events.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.AreEqual(1, list1Events.Count);
        Assert.AreEqual("Apples", list1Events[0].ChangedItem?.DisplayName);
        Assert.AreEqual(0, list2Events.Count, "Client 2 must not receive events for List 1!");
    }
}
