using App.Core.Interfaces;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class ShoppingRealtimeNotificationTests
{
    private sealed class FixedUserContext(string userId) : IUserContext
    {
        public string UserId => userId;
    }

    private sealed class RecordingRealtimeNotifier : IShoppingRealtimeNotifier
    {
        public List<(
            ShoppingListRealtimeEventDto ListEvent,
            ShoppingListsCatalogChangedDto CatalogEvent
        )> Calls { get; } = [];

        public Task NotifyListChangedAsync(
            ShoppingListRealtimeEventDto listEvent,
            ShoppingListsCatalogChangedDto catalogEvent,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add((listEvent, catalogEvent));
            return Task.CompletedTask;
        }
    }

    private AppDbContext _dbContext = null!;
    private RecordingRealtimeNotifier _notifier = null!;
    private ShoppingListService _service = null!;
    private User _user = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _notifier = new RecordingRealtimeNotifier();

        _user = new User
        {
            Id = Guid.NewGuid(),
            Email = "shopper@example.com",
            NormalizedEmail = "SHOPPER@EXAMPLE.COM",
            Username = "Shopper Sam",
            NormalizedUsername = "SHOPPER SAM",
            PasswordHash = "hash",
        };
        _dbContext.Users.Add(_user);
        _dbContext.SaveChanges();

        var userContext = new FixedUserContext(_user.Id.ToString());
        _service = new ShoppingListService(_dbContext, userContext, TimeProvider.System, _notifier);
    }

    [TestMethod]
    public async Task RealtimeNotifier_TracksListCreatedAndUpdated()
    {
        var created = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Costco", false)
        );
        Assert.AreEqual(1, _notifier.Calls.Count);
        Assert.AreEqual(
            ShoppingRealtimeEventType.ListCreated,
            _notifier.Calls[0].ListEvent.EventType
        );
        Assert.AreEqual(created.Id, _notifier.Calls[0].ListEvent.ShoppingListId);

        var updated = await _service.UpdateShoppingListAsync(
            created.Id,
            new UpdateShoppingListRequest("Costco Wholesale", true)
        );
        Assert.IsNotNull(updated);
        Assert.AreEqual(2, _notifier.Calls.Count);
        Assert.AreEqual(
            ShoppingRealtimeEventType.ListUpdated,
            _notifier.Calls[1].ListEvent.EventType
        );
        Assert.AreEqual("Costco Wholesale", _notifier.Calls[1].ListEvent.ShoppingList?.Name);
    }

    [TestMethod]
    public async Task RealtimeNotifier_TracksItemAddedCheckedAndPromoted()
    {
        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Weekly Groceries", true)
        );

        var item = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest("Avocados", 3, null, MeasurementUnit.Whole)
        );
        Assert.IsNotNull(item);
        Assert.AreEqual(2, _notifier.Calls.Count); // 1 list create, 1 item add
        Assert.AreEqual(
            ShoppingRealtimeEventType.ItemAdded,
            _notifier.Calls[1].ListEvent.EventType
        );
        Assert.AreEqual("Avocados", _notifier.Calls[1].ListEvent.ChangedItem?.DisplayName);

        var checkedItem = await _service.SetItemCheckedAsync(
            list.Id,
            item.Id,
            new SetShoppingListItemCheckedRequest(true)
        );
        Assert.IsNotNull(checkedItem);
        Assert.AreEqual(3, _notifier.Calls.Count);
        Assert.AreEqual(
            ShoppingRealtimeEventType.ItemCheckedChanged,
            _notifier.Calls[2].ListEvent.EventType
        );
        Assert.AreEqual(_user.Id, _notifier.Calls[2].ListEvent.ActorUserId);
        Assert.AreEqual(_user.Username, _notifier.Calls[2].ListEvent.ActorDisplayName);
    }

    [TestMethod]
    public async Task RealtimeNotifier_TracksItemDeletedAndClearedAndListDeleted()
    {
        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Hardware", false)
        );
        var item = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest("Nails", 100, null, MeasurementUnit.Whole)
        );
        Assert.IsNotNull(item);

        var deletedItem = await _service.DeleteItemAsync(list.Id, item.Id);
        Assert.IsTrue(deletedItem);
        Assert.AreEqual(3, _notifier.Calls.Count);
        Assert.AreEqual(
            ShoppingRealtimeEventType.ItemDeleted,
            _notifier.Calls[2].ListEvent.EventType
        );
        Assert.AreEqual(item.Id, _notifier.Calls[2].ListEvent.DeletedItemId);

        // Add checked item to clear
        var item2 = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest("Screws", 50, null, MeasurementUnit.Whole)
        );
        Assert.IsNotNull(item2);
        await _service.SetItemCheckedAsync(
            list.Id,
            item2.Id,
            new SetShoppingListItemCheckedRequest(true)
        );

        var cleared = await _service.ClearCheckedItemsAsync(list.Id);
        Assert.IsNotNull(cleared);
        Assert.AreEqual(1, cleared.RemovedCount);
        var lastCall = _notifier.Calls[^1];
        Assert.AreEqual(
            ShoppingRealtimeEventType.CheckedItemsCleared,
            lastCall.ListEvent.EventType
        );

        var deletedList = await _service.DeleteShoppingListAsync(list.Id);
        Assert.IsTrue(deletedList);
        Assert.AreEqual(
            ShoppingRealtimeEventType.ListDeleted,
            _notifier.Calls[^1].ListEvent.EventType
        );
    }
}
