using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Account;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Maintenance;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data;
using App.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class AccountIntegrationTests
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

    [TestMethod]
    public async Task GetAccount_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/account");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetAccount_ForRegisteredUser_ReturnsAccountMetadata()
    {
        var auth = await RegisterAndAuthorizeAsync("alice@example.com", "alice", "Password123!");

        var response = await _client.GetAsync("/api/account");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var account = await response.Content.ReadFromJsonAsync<AccountDto>();
        Assert.IsNotNull(account);
        Assert.AreEqual(auth.Id, account.Id);
        Assert.AreEqual("alice@example.com", account.Email);
        Assert.AreEqual("alice", account.Username);
        Assert.IsTrue(account.HasPassword);
        Assert.IsNull(account.ExternalProvider);
    }

    [TestMethod]
    public async Task UpdateProfile_UsernameChangeOnly_DoesNotRequirePassword()
    {
        await RegisterAndAuthorizeAsync("bob@example.com", "bob", "Password123!");

        var response = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(Username: "bobby", Email: "bob@example.com")
        );

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var updatedAuth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(updatedAuth);
        Assert.AreEqual("bobby", updatedAuth.Username);
        Assert.AreEqual("bob@example.com", updatedAuth.Email);
        Assert.IsFalse(string.IsNullOrWhiteSpace(updatedAuth.Token));

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            updatedAuth.Token
        );
        var me = await _client.GetFromJsonAsync<CurrentUserDto>("/api/auth/me");
        Assert.IsNotNull(me);
        Assert.AreEqual("bobby", me.Username);
    }

    [TestMethod]
    public async Task UpdateProfile_DuplicateUsername_ReturnsConflict()
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("taken@example.com", "takenname", "Password123!")
        );
        await RegisterAndAuthorizeAsync("second@example.com", "second", "Password123!");

        var response = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(Username: "TakenName", Email: "second@example.com")
        );

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateProfile_EmailChangeWithoutOrWrongPassword_ReturnsBadRequest()
    {
        await RegisterAndAuthorizeAsync("carol@example.com", "carol", "Password123!");

        var missingPw = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(Username: "carol", Email: "carol.new@example.com")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, missingPw.StatusCode);

        var wrongPw = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(
                Username: "carol",
                Email: "carol.new@example.com",
                CurrentPassword: "WrongPassword!"
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongPw.StatusCode);

        var validChange = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(
                Username: "carol",
                Email: "carol.new@example.com",
                CurrentPassword: "Password123!"
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, validChange.StatusCode);

        var updatedAuth = await validChange.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(updatedAuth);
        Assert.AreEqual("carol.new@example.com", updatedAuth.Email);
    }

    [TestMethod]
    public async Task ChangePassword_UpdatesCredentialsAndAllowsLoginWithNewPassword()
    {
        await RegisterAndAuthorizeAsync("dave@example.com", "dave", "OldPassword123!");

        var badAttempt = await _client.PutAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest(
                CurrentPassword: "WrongPassword!",
                NewPassword: "NewPassword456!"
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badAttempt.StatusCode);

        var goodAttempt = await _client.PutAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest(
                CurrentPassword: "OldPassword123!",
                NewPassword: "NewPassword456!"
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, goodAttempt.StatusCode);

        using var anonClient = _factory.CreateClient();
        var oldLogin = await anonClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("dave@example.com", "OldPassword123!")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anonClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("dave@example.com", "NewPassword456!")
        );
        Assert.AreEqual(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [TestMethod]
    public async Task ExternalOnlyUser_CanChangeUsernameAndSetInitialPassword_CannotChangeEmailWithoutPassword()
    {
        Guid externalUserId;
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jwt = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = "sso@example.com",
                NormalizedEmail = "SSO@EXAMPLE.COM",
                Username = "ssouser",
                NormalizedUsername = "SSOUSER",
                PasswordHash = string.Empty,
                ExternalProvider = "oidc",
                ExternalSubjectId = "sub-12345",
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            externalUserId = user.Id;
            token = jwt.GenerateToken(new UserSummary(user.Id, user.Email, user.Username));
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );

        var account = await _client.GetFromJsonAsync<AccountDto>("/api/account");
        Assert.IsNotNull(account);
        Assert.AreEqual(externalUserId, account.Id);
        Assert.IsFalse(account.HasPassword);
        Assert.AreEqual("oidc", account.ExternalProvider);

        // Email change is blocked when no password exists
        var emailBlocked = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(Username: "ssouser", Email: "different@example.com")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, emailBlocked.StatusCode);
        var problem = await emailBlocked.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.IsNotNull(problem);

        // Username change succeeds without password
        var usernameChanged = await _client.PutAsJsonAsync(
            "/api/account/profile",
            new UpdateProfileRequest(Username: "sso_renamed", Email: "sso@example.com")
        );
        Assert.AreEqual(HttpStatusCode.OK, usernameChanged.StatusCode);

        // Setting initial password succeeds without CurrentPassword
        var setPassword = await _client.PutAsJsonAsync(
            "/api/account/password",
            new ChangePasswordRequest(CurrentPassword: null, NewPassword: "InitialPassword123!")
        );
        Assert.AreEqual(HttpStatusCode.OK, setPassword.StatusCode);

        var refreshedAccount = await _client.GetFromJsonAsync<AccountDto>("/api/account");
        Assert.IsNotNull(refreshedAccount);
        Assert.IsTrue(refreshedAccount.HasPassword);
    }

    [TestMethod]
    public async Task UpdateThemeSettings_PersistsAndReturnsAcrossMeAccountAndLogin()
    {
        await RegisterAndAuthorizeAsync("theme@example.com", "themeuser", "Password123!");

        var putResponse = await _client.PutAsJsonAsync(
            "/api/account/theme",
            new
            {
                settings = new
                {
                    mode = "dark",
                    primaryPalette = "azure",
                    secondaryPalette = "rose",
                    tertiaryPalette = "spring-green",
                    fontFamily = "caveat",
                },
            }
        );
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);

        var me = await _client.GetFromJsonAsync<CurrentUserDto>("/api/auth/me");
        Assert.IsNotNull(me);
        Assert.IsNotNull(me.ThemeSettings);
        StringAssert.Contains(me.ThemeSettings, "\"mode\":\"dark\"");
        StringAssert.Contains(me.ThemeSettings, "\"primaryPalette\":\"azure\"");

        var account = await _client.GetFromJsonAsync<AccountDto>("/api/account");
        Assert.IsNotNull(account);
        Assert.AreEqual(me.ThemeSettings, account.ThemeSettings);

        using var anonClient = _factory.CreateClient();
        var loginResp = await anonClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("theme@example.com", "Password123!")
        );
        Assert.AreEqual(HttpStatusCode.OK, loginResp.StatusCode);
        var loginAuth = await loginResp.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(loginAuth);
        Assert.AreEqual(me.ThemeSettings, loginAuth.ThemeSettings);
    }

    [TestMethod]
    public async Task UpdateThemeSettings_NonObjectOrOversized_ReturnsBadRequest()
    {
        await RegisterAndAuthorizeAsync("theme2@example.com", "themeuser2", "Password123!");

        var nonObjectResp = await _client.PutAsJsonAsync(
            "/api/account/theme",
            new { settings = "not-an-object" }
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, nonObjectResp.StatusCode);

        var oversizedResp = await _client.PutAsJsonAsync(
            "/api/account/theme",
            new { settings = new { padding = new string('x', 4100) } }
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, oversizedResp.StatusCode);
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

    [TestMethod]
    public async Task ResetAppData_WipesCulinaryData_WhilePreservingUserAccountAndAuth()
    {
        var auth = await RegisterAndAuthorizeAsync("nuke@example.com", "nukeuser", "Password123!");

        // 1. Seed food item
        var foodRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Cheddar Cheese", IsPantryStaple: true)
        );
        Assert.AreEqual(HttpStatusCode.Created, foodRes.StatusCode);
        var food = await foodRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(food);

        // 2. Seed recipe
        var recipeReq = new UpsertRecipeRequest(
            Title: "Grilled Cheese",
            Description: "Crispy sandwich",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Slice,
                    CustomUnitName: null,
                    FoodItemId: food.Id,
                    LinkedRecipeId: null,
                    DisplayName: "slices cheddar cheese",
                    PreparationNote: null
                ),
            ],
            Steps: [new RecipeStepInput(null, "Grill sandwich.", [0])]
        );
        var recipeRes = await _client.PostAsJsonAsync("/api/recipes", recipeReq);
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);

        // 3. Seed shopping list
        var listRes = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Weekly Groceries", IsPinned: false)
        );
        Assert.AreEqual(HttpStatusCode.Created, listRes.StatusCode);

        // 4. Call nuclear reset endpoint
        var resetRes = await _client.PostAsync("/api/account/reset-data", null);
        Assert.AreEqual(HttpStatusCode.OK, resetRes.StatusCode);

        var result = await resetRes.Content.ReadFromJsonAsync<ResetAppDataResultDto>();
        Assert.IsNotNull(result);
        Assert.IsTrue(result.DeletedRecipesCount >= 1);
        Assert.IsTrue(result.DeletedFoodItemsCount >= 1);
        Assert.IsTrue(result.DeletedShoppingListsCount >= 1);

        // 5. Verify culinary collections are empty
        var getRecipes = await _client.GetFromJsonAsync<List<RecipeSummaryDto>>("/api/recipes");
        Assert.IsNotNull(getRecipes);
        Assert.AreEqual(0, getRecipes.Count);

        var getFoods = await _client.GetFromJsonAsync<List<FoodItemDto>>("/api/food-items");
        Assert.IsNotNull(getFoods);
        Assert.AreEqual(0, getFoods.Count);

        var getLists = await _client.GetFromJsonAsync<List<ShoppingListSummaryDto>>(
            "/api/shopping-lists"
        );
        Assert.IsNotNull(getLists);
        Assert.AreEqual(0, getLists.Count);

        // 6. User account is still valid and fully functional
        var account = await _client.GetFromJsonAsync<AccountDto>("/api/account");
        Assert.IsNotNull(account);
        Assert.AreEqual("nuke@example.com", account.Email);
        Assert.AreEqual("nukeuser", account.Username);
    }

    [TestMethod]
    public async Task ResetAppData_Unauthorized_Returns401()
    {
        using var anonClient = _factory.CreateClient();
        var res = await anonClient.PostAsync("/api/account/reset-data", null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
