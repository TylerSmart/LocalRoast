using App.Core.Authentication;
using App.Core.Exceptions;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class PersonalAccessTokenServiceTests
{
    private AppDbContext _dbContext = null!;
    private PersonalAccessTokenService _service = null!;
    private Guid _userId;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _service = new PersonalAccessTokenService(_dbContext);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "TestChef",
            NormalizedUsername = "TESTCHEF",
            Email = "chef@example.com",
            NormalizedEmail = "CHEF@EXAMPLE.COM",
            PasswordHash = "hash123",
        };
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();
        _userId = user.Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task CreateTokenAsync_ValidRequest_CreatesTokenAndReturnsPlaintext()
    {
        var request = new CreatePersonalAccessTokenRequest("Claude Desktop");

        var created = await _service.CreateTokenAsync(_userId, request);

        Assert.IsNotNull(created);
        Assert.IsNotNull(created.PlaintextToken);
        Assert.IsTrue(created.PlaintextToken.StartsWith("lr_pat_", StringComparison.Ordinal));
        Assert.AreEqual("Claude Desktop", created.Token.Name);
        Assert.IsTrue(created.Token.IsActive);
        Assert.IsNull(created.Token.RevokedAt);
        Assert.IsNull(created.Token.LastUsedAt);

        var dbToken = await _dbContext.PersonalAccessTokens.FirstOrDefaultAsync(t =>
            t.Id == created.Token.Id
        );
        Assert.IsNotNull(dbToken);
        Assert.AreEqual(created.Token.TokenPrefix, dbToken.TokenPrefix);
        Assert.AreEqual(
            PersonalAccessTokenGenerator.ComputeSha256Hex(created.PlaintextToken),
            dbToken.TokenHash
        );
    }

    [TestMethod]
    public async Task CreateTokenAsync_InvalidName_ThrowsDomainValidationException()
    {
        var request = new CreatePersonalAccessTokenRequest("a"); // < 2 chars
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.CreateTokenAsync(_userId, request)
        );
    }

    [TestMethod]
    public async Task CreateTokenAsync_PastExpiration_ThrowsDomainValidationException()
    {
        var request = new CreatePersonalAccessTokenRequest(
            "Expired Token",
            DateTimeOffset.UtcNow.AddMinutes(-5)
        );

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.CreateTokenAsync(_userId, request)
        );
    }

    [TestMethod]
    public async Task RevokeTokenAsync_UserOwnsToken_SetsRevokedAtAndIsActiveFalse()
    {
        var created = await _service.CreateTokenAsync(
            _userId,
            new CreatePersonalAccessTokenRequest("To Revoke")
        );

        var revoked = await _service.RevokeTokenAsync(_userId, created.Token.Id);

        Assert.IsNotNull(revoked);
        Assert.IsFalse(revoked.IsActive);
        Assert.IsNotNull(revoked.RevokedAt);

        var validated = await _service.ValidateAndTouchTokenAsync(created.PlaintextToken);
        Assert.IsNull(validated);
    }

    [TestMethod]
    public async Task RevokeTokenAsync_OtherUserToken_ThrowsForbiddenException()
    {
        var created = await _service.CreateTokenAsync(
            _userId,
            new CreatePersonalAccessTokenRequest("User1 Token")
        );
        var otherUserId = Guid.NewGuid();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.RevokeTokenAsync(otherUserId, created.Token.Id)
        );
    }

    [TestMethod]
    public async Task DeleteTokenAsync_UserOwnsToken_DeletesFromDb()
    {
        var created = await _service.CreateTokenAsync(
            _userId,
            new CreatePersonalAccessTokenRequest("To Delete")
        );

        var deleted = await _service.DeleteTokenAsync(_userId, created.Token.Id);
        Assert.IsTrue(deleted);

        var dbToken = await _dbContext.PersonalAccessTokens.FirstOrDefaultAsync(t =>
            t.Id == created.Token.Id
        );
        Assert.IsNull(dbToken);
    }

    [TestMethod]
    public async Task ValidateAndTouchTokenAsync_ValidToken_UpdatesLastUsedAtAndReturnsUser()
    {
        var created = await _service.CreateTokenAsync(
            _userId,
            new CreatePersonalAccessTokenRequest("Valid PAT")
        );

        var validated = await _service.ValidateAndTouchTokenAsync(created.PlaintextToken);

        Assert.IsNotNull(validated);
        Assert.AreEqual(_userId, validated.UserId);
        Assert.AreEqual("TestChef", validated.Username);
        Assert.AreEqual("chef@example.com", validated.Email);

        var dbToken = await _dbContext.PersonalAccessTokens.FirstOrDefaultAsync(t =>
            t.Id == created.Token.Id
        );
        Assert.IsNotNull(dbToken?.LastUsedAt);
    }

    [TestMethod]
    public async Task ValidateAndTouchTokenAsync_ExpiredToken_ReturnsNull()
    {
        var created = await _service.CreateTokenAsync(
            _userId,
            new CreatePersonalAccessTokenRequest(
                "Expiring PAT",
                DateTimeOffset.UtcNow.AddSeconds(1)
            )
        );

        // Manually expire in DB for deterministic test
        var dbToken = await _dbContext.PersonalAccessTokens.FirstAsync(t =>
            t.Id == created.Token.Id
        );
        dbToken.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _dbContext.SaveChangesAsync();

        var validated = await _service.ValidateAndTouchTokenAsync(created.PlaintextToken);
        Assert.IsNull(validated);
    }
}
