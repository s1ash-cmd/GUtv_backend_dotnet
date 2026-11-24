using System.Data.Common;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using GUtv_backend_dotnet.Services.Telegram;
using HotChocolate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;

namespace GUtv_backend_dotnet.Tests;

public class BookingAllocationTests
{
    private static readonly DateTime Start = new(2030, 1, 10, 10, 0, 0, DateTimeKind.Utc);
    private static CreateBookingInput Input(params CreateBookingEquipmentInput[] equipment) =>
        new("Съемка", Start, Start.AddHours(2), null, equipment);

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "Повреждение оборудования")]
    public async Task BanRejectsDirectAndCartCreationWithReasonWithoutLosingCart(bool withReason, string? reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [1]);
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), null));
        // Leave a stale user in the change tracker to ensure the ban is read from the database.
        await fixture.Db.Users.Where(u => u.Id == 1).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.Banned, true).SetProperty(u => u.BanReason, reason));
        foreach (var operation in new Func<Task<Booking>>[] {
            () => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1),
            () => fixture.Carts.CreateBookingFromCartAsync(1) })
        {
            var error = await Assert.ThrowsAsync<GraphQLException>(operation);
            var message = Assert.Single(error.Errors).Message;
            Assert.Contains("Вам запрещено бронировать оборудование", message);
            if (withReason) Assert.Contains(reason!, message);
            Assert.Empty(await fixture.Db.Bookings.ToListAsync());
            Assert.Equal(new[] { 1 }, Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items).SelectedItemIds);
        }
        await new UserService(fixture.Db).SetBanned(1, false);
        Assert.NotNull(await fixture.Carts.CreateBookingFromCartAsync(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BanRejectsDirectAndCartBookingEdits(bool fromCart)
    {
        await using var fixture = await Fixture.CreateAsync();
        var input = Input(new CreateBookingEquipmentInput(1, 1, [1]));
        var booking = await fixture.Bookings.CreateBookingAsync(input, 1);
        await fixture.Carts.PrepareBookingEditAsync(1, booking.Id, false);
        await new UserService(fixture.Db).SetBanned(1, true, "Нарушение правил");
        var error = await Assert.ThrowsAsync<GraphQLException>(() => fromCart
            ? fixture.Carts.UpdateBookingFromCartAsync(1, booking.Id, false)
            : fixture.Bookings.UpdateBookingAsync(booking.Id, input, 1, false));
        Assert.Contains("Нарушение правил", Assert.Single(error.Errors).Message);
        Assert.Equal(1, (await fixture.Db.Bookings.AsNoTracking().SingleAsync()).Revision);
        Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items);
    }

    [Fact]
    public async Task BannedOwnerCanStillReadAndCancelExistingBooking()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        await new UserService(fixture.Db).SetBanned(1, true, "Нарушение правил");
        Assert.Equal(booking.Id, (await fixture.Bookings.GetBookingByIdAsync(booking.Id, 1, false)).Id);
        var cancelled = await fixture.Bookings.CancelBookingAsync(booking.Id, 1, false, booking.Revision);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task MultipleInventoryNumbersAreBookedExactlyAsSelected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 2, [2, 1])), 1);
        Assert.Equal(new[] { 1, 2 }, booking.BookingItems.Select(item => item.EqItemId).Order().ToArray());
        Assert.All(booking.BookingItems, item => Assert.True(item.IsExplicitlySelected));
    }

    [Fact]
    public async Task MissingOrIncompleteInventoryChoiceCannotUseAutomaticAllocation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 2, [2])), 1));
        Assert.Empty(await fixture.Db.Bookings.ToListAsync());
    }

    [Fact]
    public async Task OccupiedExplicitInventoryIsNotSilentlyReplacedAndCartSurvivesFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [2])), 2);
        await fixture.Carts.AddCartItemAsync(1, 1, 2);
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), null));

        var error = await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.CreateBookingFromCartAsync(1));

        Assert.Contains(error.Errors, item => item.Message.Contains("CAM-2") && item.Message.Contains("занят"));
        var saved = Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items);
        Assert.Equal(new[] { 2 }, saved.SelectedItemIds);
        Assert.Single(await fixture.Db.Bookings.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(3)]
    public async Task InvalidOrWrongModelInventoryIsRejected(int selectedId)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = "Other" });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "OTHER-1" });
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [selectedId])), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.AddCartItemAsync(1, 1, selectedId));
        Assert.Empty(await fixture.Db.Bookings.ToListAsync());
        Assert.Empty(await fixture.Db.CartItems.ToListAsync());
    }

    [Fact]
    public async Task FaultyExplicitInventoryIsRejectedEvenWhenAnotherItemIsFree()
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.Db.EqItems.FindAsync(2))!.Operable = false;
        await fixture.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [2])), 1));
        Assert.Contains(error.Errors, item => item.Message.Contains("CAM-2") && item.Message.Contains("неисправен"));
    }

    [Fact]
    public async Task DuplicateSelectionsAndMoreSelectionsThanQuantityAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 2, [2, 2])), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1, 2])), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [2]), new CreateBookingEquipmentInput(1, 1, [2])), 1));
        await fixture.Carts.AddCartItemAsync(1, 1, 2);
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.AddCartItemAsync(1, 1, 2));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.SetCartItemSelectionAsync(1, 1, [1, 1]));
        Assert.Equal(1, Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items).Quantity);
    }

    [Fact]
    public async Task SelectionDeterminesQuantityAndCanCreateCartPositionInOneRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [2, 1]);
        fixture.Db.ChangeTracker.Clear();
        var item = Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items);
        Assert.Equal(new[] { 2, 1 }, item.SelectedItemIds);
        Assert.Equal(2, item.Quantity);
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.UpdateCartItemQuantityAsync(1, 1, 3));
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, []);
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), null));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.CreateBookingFromCartAsync(1));
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [2]);
        var booking = await fixture.Carts.CreateBookingFromCartAsync(1);
        Assert.Equal(2, Assert.Single(booking.BookingItems).EqItemId);
    }

    [Fact]
    public async Task CartCheckoutAndEditRetainAllInventoryIncludingLegacyAssignments()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [1, 2]);
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), null));
        var booking = await fixture.Carts.CreateBookingFromCartAsync(1);
        Assert.Empty((await fixture.Carts.GetOrCreateCartAsync(1)).Items);
        foreach (var assignment in booking.BookingItems) assignment.IsExplicitlySelected = false;
        await fixture.Db.SaveChangesAsync();

        var cart = await fixture.Carts.PrepareBookingEditAsync(1, booking.Id, false);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(cart.Items).SelectedItemIds.Order());
        var ownOptions = await fixture.Carts.GetEquipmentOptionsAsync(1, 1, Start, Start.AddHours(2));
        Assert.All(ownOptions, item => Assert.True(item.Available));
        var otherOptions = await fixture.Carts.GetEquipmentOptionsAsync(2, 1, Start, Start.AddHours(2));
        Assert.All(otherOptions, item => Assert.False(item.Available));
        var updated = await fixture.Carts.UpdateBookingFromCartAsync(1, booking.Id, false);
        Assert.True(updated.BookingItems.Single(item => item.EqItemId == 2).IsExplicitlySelected);
        Assert.True(updated.BookingItems.Single(item => item.EqItemId == 1).IsExplicitlySelected);

        var repeated = await fixture.Carts.AddBookingItemsToCartAsync(1, booking.Id);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(repeated.Items).SelectedItemIds.Order());
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.AddBookingItemsToCartAsync(1, booking.Id));
        Assert.Equal(2, Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items).Quantity);
    }

    [Fact]
    public async Task EditCanChangeExplicitInventoryAndChangingDatesDoesNotPickAReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [2])), 1);
        var changed = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [1])), 1, false);
        Assert.Equal(1, Assert.Single(changed.BookingItems).EqItemId);

        var later = Input(new CreateBookingEquipmentInput(1, 1, [1])) with { StartTime = Start.AddDays(1), EndTime = Start.AddDays(1).AddHours(2) };
        await fixture.Bookings.CreateBookingAsync(later, 2);
        await fixture.Carts.PrepareBookingEditAsync(1, booking.Id, false);
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", later.StartTime, later.EndTime, null));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.UpdateBookingFromCartAsync(1, booking.Id, false));
        var original = await fixture.Bookings.GetBookingByIdAsync(booking.Id, 1, false);
        Assert.Equal(Start, original.StartTime);
        Assert.Equal(1, Assert.Single(original.BookingItems).EqItemId);
    }

    [Fact]
    public async Task EquipmentHistoryTracksEditorAndInventoryAcrossDirectAndCartEdits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        Assert.Empty(await fixture.Bookings.GetBookingEquipmentHistoryAsync(booking.Id, 1, false));

        var beforeEdit = DateTime.UtcNow;
        var ownerEdit = await fixture.Bookings.UpdateBookingAsync(
            booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [2])), 1, false);

        await fixture.Carts.PrepareBookingEditAsync(2, booking.Id, true);
        await fixture.Carts.SetCartItemSelectionAsync(2, 1, [1, 2]);
        var adminEdit = await fixture.Carts.UpdateBookingFromCartAsync(2, booking.Id, true);

        await fixture.Carts.PrepareBookingEditAsync(1, booking.Id, false);
        await fixture.Carts.SetCartDetailsAsync(1, new("Интервью", Start.AddHours(1), Start.AddHours(3), "Новая студия"));
        var detailsEdit = await fixture.Carts.UpdateBookingFromCartAsync(1, booking.Id, false);
        var unchanged = new CreateBookingInput("Интервью", Start.AddHours(1), Start.AddHours(3), "Новая студия",
            [new CreateBookingEquipmentInput(1, 2, [2, 1])]);
        await fixture.Bookings.UpdateBookingAsync(booking.Id, unchanged, 1, false);
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.UpdateBookingAsync(
            booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [999])), 1, false));

        var model = (await fixture.Db.EqModels.FindAsync(1))!;
        model.Name = "New camera name";
        await fixture.Db.SaveChangesAsync();

        var allChanges = await fixture.Bookings.GetBookingHistoryAsync(booking.Id, 1, false);
        var detailsChange = allChanges[0];
        Assert.Equal(new[] { detailsEdit.Revision, adminEdit.Revision, ownerEdit.Revision },
            allChanges.Select(change => change.Revision));
        Assert.Equal("Owner", detailsChange.ActorName);
        Assert.Empty(detailsChange.Added);
        Assert.Empty(detailsChange.Removed);
        Assert.Contains(new BookingFieldChange(BookingHistoryField.StartTime, Start.ToString("O"), Start.AddHours(1).ToString("O")), detailsChange.Fields);
        Assert.Contains(new BookingFieldChange(BookingHistoryField.EndTime, Start.AddHours(2).ToString("O"), Start.AddHours(3).ToString("O")), detailsChange.Fields);
        Assert.Contains(new BookingFieldChange(BookingHistoryField.Reason, "Съемка", "Интервью"), detailsChange.Fields);
        Assert.Contains(new BookingFieldChange(BookingHistoryField.Comment, null, "Новая студия"), detailsChange.Fields);

        var history = await fixture.Bookings.GetBookingEquipmentHistoryAsync(booking.Id, 1, false);
        Assert.Collection(history, adminChange =>
        {
            Assert.Equal(adminEdit.Revision, adminChange.Revision);
            Assert.Equal("Admin", adminChange.ActorName);
            Assert.Equal(new BookingEquipmentSnapshot(1, "Camera", "CAM-1"), Assert.Single(adminChange.Added));
            Assert.Empty(adminChange.Removed);
            Assert.InRange(adminChange.ChangedAt, beforeEdit, DateTime.UtcNow);
        }, ownerChange =>
        {
            Assert.Equal(ownerEdit.Revision, ownerChange.Revision);
            Assert.Equal("Owner", ownerChange.ActorName);
            Assert.Equal(new BookingEquipmentSnapshot(2, "Camera", "CAM-2"), Assert.Single(ownerChange.Added));
            Assert.Equal(new BookingEquipmentSnapshot(1, "Camera", "CAM-1"), Assert.Single(ownerChange.Removed));
            Assert.InRange(ownerChange.ChangedAt, beforeEdit, DateTime.UtcNow);
        });

        Assert.Equal(2, (await fixture.Bookings.GetBookingEquipmentHistoryAsync(booking.Id, 2, true)).Count);
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.GetBookingEquipmentHistoryAsync(booking.Id, 2, false));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.GetBookingHistoryAsync(booking.Id, 2, false));
    }

    [Fact]
    public async Task BookingHistoryIncludesStatusDecisionsAndReadsLegacyEquipmentEntries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var input = Input(new CreateBookingEquipmentInput(1, 1, [1]));
        var booking = await fixture.Bookings.CreateBookingAsync(input, 1);
        var edited = await fixture.Bookings.UpdateBookingAsync(
            booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [2])), 1, false);
        await fixture.Db.BookingEquipmentChanges.ExecuteUpdateAsync(changes => changes.SetProperty(
            change => change.EquipmentJson,
            """{"Added":[{"EqItemId":2,"ModelName":"Camera","InventoryNumber":"CAM-2"}],"Removed":[]}"""));

        var approved = await fixture.Bookings.ApproveBookingAsync(booking.Id, edited.Revision, 2, "Admin: Согласовано");
        var completed = await fixture.Bookings.CompleteBookingAsync(booking.Id, approved.Revision, 2);

        var changes = await fixture.Bookings.GetBookingHistoryAsync(booking.Id, 1, false);
        Assert.Collection(changes, completion =>
        {
            Assert.Equal(completed.Revision, completion.Revision);
            Assert.Equal("Admin", completion.ActorName);
            Assert.Equal(new BookingFieldChange(BookingHistoryField.Status, "Approved", "Completed"),
                Assert.Single(completion.Fields));
        }, approval =>
        {
            Assert.Equal(approved.Revision, approval.Revision);
            Assert.Contains(new BookingFieldChange(BookingHistoryField.Status, "Pending", "Approved"), approval.Fields);
            Assert.Contains(new BookingFieldChange(BookingHistoryField.AdminComment, null, "Admin: Согласовано"), approval.Fields);
        }, legacy =>
        {
            Assert.Equal(edited.Revision, legacy.Revision);
            Assert.Empty(legacy.Fields);
            Assert.Equal("CAM-2", Assert.Single(legacy.Added).InventoryNumber);
        });

        var another = await fixture.Bookings.CreateBookingAsync(input, 1);
        await fixture.Bookings.CancelBookingAsync(another.Id, 1, false, another.Revision);
        var cancellation = Assert.Single(await fixture.Bookings.GetBookingHistoryAsync(another.Id, 1, false));
        Assert.Equal("Owner", cancellation.ActorName);
        Assert.Equal(new BookingFieldChange(BookingHistoryField.Status, "Pending", "Cancelled"),
            Assert.Single(cancellation.Fields));
    }

    [Fact]
    public async Task ModelIdsSurviveRenamesAndDisambiguateIdenticalNames()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = await fixture.Db.EqModels.FindAsync(1);
        model!.Name = "Renamed camera";
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = model.Name });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "OTHER-1" });
        await fixture.Db.SaveChangesAsync();

        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(2, 1, [3])), 1);

        Assert.Equal(3, Assert.Single(booking.BookingItems).EqItemId);
        Assert.Equal("Renamed camera", booking.BookingItems.Single().EqItem.EqModel.Name);
    }

    [Fact]
    public async Task DuplicateIdsAreCombinedAndEachModelIsLockedOnceInIdOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = "Another camera" });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "OTHER-1" });
        await fixture.Db.SaveChangesAsync();

        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(2, 1, [3]), new CreateBookingEquipmentInput(1, 1, [1]), new CreateBookingEquipmentInput(1, 1, [2])), 1);

        Assert.Equal(3, booking.BookingItems.Count);
        Assert.Equal(3, booking.BookingItems.Select(item => item.EqItemId).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, fixture.Locks.ModelIds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(999, 1)]
    public async Task InvalidRequestsLeaveNoBooking(int modelId, int quantity)
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(modelId, quantity)), 1));

        Assert.Empty(await fixture.Db.Bookings.ToListAsync());
    }

    [Fact]
    public async Task HugeQuantityWithoutInventoryIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var error = await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, int.MaxValue), new CreateBookingEquipmentInput(1, 1, [1])), 1));
        Assert.Contains(error.Errors, item => item.Message.Contains("инвентарные номера"));
    }

    [Theory]
    [InlineData(BookingStatus.Pending, true, true)]
    [InlineData(BookingStatus.Approved, true, true)]
    [InlineData(BookingStatus.Cancelled, true, false)]
    [InlineData(BookingStatus.Completed, true, false)]
    [InlineData(BookingStatus.Pending, false, false)]
    public async Task AvailabilityAndAllocationAgreeOnStatusesAndAdjacentIntervals(
        BookingStatus status, bool overlaps, bool blocked)
    {
        await using var fixture = await Fixture.CreateAsync();
        var begin = overlaps ? Start : Start.AddHours(2);
        fixture.Db.Bookings.Add(new Booking
        {
            UserId = 1, Reason = "Existing", Status = status,
            StartTime = begin, EndTime = begin.AddHours(2),
            BookingItems = [new BookingItem { EqItemId = 1, StartDate = begin, EndDate = begin.AddHours(2) }]
        });
        (await fixture.Db.EqItems.FindAsync(2))!.Operable = false;
        await fixture.Db.SaveChangesAsync();

        var available = await new EquipmentService(fixture.Db).GetAvailableItemsByModelAsync(1, Start, Start.AddHours(2));
        Assert.Equal(blocked ? 0 : 1, available.Count);
        if (blocked)
            await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1));
        else
        {
            var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
            Assert.Equal(available.Single().Id, Assert.Single(booking.BookingItems).EqItemId);
        }
    }

    [Fact]
    public async Task EditReusesItsOwnEquipmentButStillExcludesOtherBookings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [2])), 2);

        var updated = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [1])), 1, false);
        Assert.Equal(booking.BookingItems.Single().EqItemId, Assert.Single(updated.BookingItems).EqItemId);
        Assert.Equal(booking.Revision + 1, updated.Revision);
        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 2, [1, 2])), 1, false));
        Assert.Single((await fixture.Bookings.GetBookingByIdAsync(booking.Id, 1, false)).BookingItems);
    }

    [Fact]
    public async Task CreationAndEditingKeepOsnovaWarningsAndRoninPermissions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = (await fixture.Db.EqModels.FindAsync(1))!;
        model.Access = EqAccess.Osnova;
        await fixture.Db.SaveChangesAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        var updated = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [1])), 1, false);
        Assert.Contains("missingOsnovaAccess_1", booking.WarningsJson);
        Assert.Contains("missingOsnovaAccess_1", updated.WarningsJson);

        model.Access = EqAccess.Ronin;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [1])), 1, false));
        var granted = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1, [1])), 2, true);
        Assert.Single(granted.BookingItems);
        Assert.DoesNotContain("missingOsnovaAccess", granted.WarningsJson);
    }

    [Fact]
    public async Task CartDetailsKeepOmittedFieldsAndClearExplicitNulls()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), "Нужен штатив"));

        // Only the reason is sent: the dates and the comment stay.
        var cart = await fixture.Carts.SetCartDetailsAsync(1, new UpdateCartDetailsInput(Reason: "  Монтаж  "));
        Assert.Equal("Монтаж", cart.Reason);
        Assert.Equal(Start, cart.StartTime);
        Assert.Equal(Start.AddHours(2), cart.EndTime);
        Assert.Equal("Нужен штатив", cart.Comment);

        cart = await fixture.Carts.SetCartDetailsAsync(1,
            new UpdateCartDetailsInput(Reason: null, StartTime: null, EndTime: null, Comment: null));
        Assert.Equal("", cart.Reason);
        Assert.Null(cart.StartTime);
        Assert.Null(cart.EndTime);
        Assert.Null(cart.Comment);

        // An empty comment is no comment, as on the booking form.
        cart = await fixture.Carts.SetCartDetailsAsync(1, new UpdateCartDetailsInput(Comment: "   "));
        Assert.Null(cart.Comment);
    }

    [Fact]
    public async Task CartRejectsAStartThatDoesNotPrecedeTheSavedEnd()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartDetailsAsync(1, new("Съемка", Start, Start.AddHours(2), null));

        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Carts.SetCartDetailsAsync(1, new UpdateCartDetailsInput(StartTime: Start.AddHours(3))));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(Start, (await fixture.Db.Carts.AsNoTracking().SingleAsync()).StartTime);
    }

    [Fact]
    public async Task EditingABookingNeverDiscardsTheCartWithoutConsent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        var second = await fixture.Bookings.CreateBookingAsync(
            new CreateBookingInput("Монтаж", Start.AddDays(1), Start.AddDays(1).AddHours(2), null,
                [new CreateBookingEquipmentInput(1, 1, [2])]), 1);
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [2]);
        await fixture.Carts.SetCartDetailsAsync(1, new("Своя съёмка", Start, Start.AddHours(1), null));

        // The cart holds a draft of its own: editing asks first and changes nothing.
        var error = await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.PrepareBookingEditAsync(1, first.Id, false));
        Assert.Equal(CartService.ReplaceRequiredCode, Assert.Single(error.Errors).Code);
        fixture.Db.ChangeTracker.Clear();
        var kept = await fixture.Carts.GetOrCreateCartAsync(1);
        Assert.Null(kept.EditingBookingId);
        Assert.Equal("Своя съёмка", kept.Reason);
        Assert.Equal(new[] { 2 }, Assert.Single(kept.Items).SelectedItemIds);

        // Consent replaces it.
        var editing = await fixture.Carts.PrepareBookingEditAsync(1, first.Id, false, replaceCart: true);
        Assert.Equal(first.Id, editing.EditingBookingId);
        Assert.Equal(new[] { 1 }, Assert.Single(editing.Items).SelectedItemIds);

        // Opening the same booking again continues the edit in progress.
        await fixture.Carts.SetCartDetailsAsync(1, new UpdateCartDetailsInput(Reason: "Уточнённая причина"));
        var continued = await fixture.Carts.PrepareBookingEditAsync(1, first.Id, false);
        Assert.Equal("Уточнённая причина", continued.Reason);

        // Another booking, or repeating one, would drop the unsaved edit: both ask first.
        Assert.Equal(CartService.ReplaceRequiredCode, Assert.Single((await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Carts.PrepareBookingEditAsync(1, second.Id, false))).Errors).Code);
        Assert.Equal(CartService.ReplaceRequiredCode, Assert.Single((await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Carts.AddBookingItemsToCartAsync(1, second.Id))).Errors).Code);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Уточнённая причина", (await fixture.Carts.GetOrCreateCartAsync(1)).Reason);

        var repeated = await fixture.Carts.AddBookingItemsToCartAsync(1, second.Id, replaceCart: true);
        Assert.Null(repeated.EditingBookingId);
        Assert.Equal(new[] { 2 }, Assert.Single(repeated.Items).SelectedItemIds);
    }

    [Fact]
    public async Task RepeatingABookingAddsToAnOrdinaryCartWithoutAsking()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = "Tripod" });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "TRI-1" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Carts.SetCartItemSelectionAsync(1, 2, [3]);

        var cart = await fixture.Carts.AddBookingItemsToCartAsync(1, booking.Id);

        Assert.Equal(new[] { 1, 2 }, cart.Items.Select(item => item.EqModelId).Order());
    }

    [Fact]
    public async Task UnitThatWasNeverBookedIsDeletedEvenWhenItIsNotTheLastOne()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Carts.SetCartItemSelectionAsync(1, 1, [1, 2]);
        fixture.Db.ChangeTracker.Clear();

        Assert.True(await new EquipmentService(fixture.Db).DeleteItemAsync(1));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { 2 }, await fixture.Db.EqItems.Select(item => item.Id).ToListAsync());
        // The unit also leaves the carts that had it selected.
        var cartItem = Assert.Single((await fixture.Carts.GetOrCreateCartAsync(1)).Items);
        Assert.Equal(new[] { 2 }, cartItem.SelectedItemIds);
        Assert.Equal(1, cartItem.Quantity);
    }

    [Fact]
    public async Task UnitWithFinishedHistoryIsArchivedAndStaysInThatHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        await fixture.Bookings.CancelBookingAsync(booking.Id, 1, false, booking.Revision);
        fixture.Db.ChangeTracker.Clear();
        var equipment = new EquipmentService(fixture.Db);

        Assert.True(await equipment.DeleteItemAsync(1));

        fixture.Db.ChangeTracker.Clear();
        Assert.NotNull((await fixture.Db.EqItems.AsNoTracking().SingleAsync(item => item.Id == 1)).ArchivedAt);
        // Gone from every list a unit can be picked from...
        Assert.Equal(new[] { 2 }, (await equipment.GetItemsByModelAsync(1)).Select(item => item.Id));
        Assert.Equal(new[] { 2 }, (await equipment.GetAllItemsAsync()).Select(item => item.Id));
        Assert.Equal(new[] { 2 }, Assert.Single(await equipment.GetModelsWithItemsAsync()).Items.Select(item => item.Id));
        Assert.Equal(new[] { 2 }, (await equipment.GetAvailableItemsByModelAsync(1, Start, Start.AddHours(2))).Select(item => item.Id));
        Assert.Equal(new[] { 2 }, (await fixture.Carts.GetEquipmentOptionsAsync(1, 1, Start, Start.AddHours(2))).Select(item => item.Id));
        // ...and it can no longer be booked or put into a cart,
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.AddCartItemAsync(1, 1, 1));
        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1));
        // while the old booking still shows which unit it was.
        var history = await fixture.Bookings.GetBookingByIdAsync(booking.Id, 1, false);
        Assert.Equal("CAM-1", Assert.Single(history.BookingItems).EqItem.InventoryNumber);
        // Deleting it again reports that it is gone.
        await Assert.ThrowsAsync<GraphQLException>(() => equipment.DeleteItemAsync(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnitInAnActiveBookingCannotBeRemoved(bool approved)
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1, [1])), 1);
        if (approved) await fixture.Bookings.ApproveBookingAsync(booking.Id, booking.Revision, 2);
        fixture.Db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<GraphQLException>(() => new EquipmentService(fixture.Db).DeleteItemAsync(1));

        Assert.Contains($"#{booking.Id}", Assert.Single(error.Errors).Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.Db.EqItems.AsNoTracking().SingleAsync(item => item.Id == 1)).ArchivedAt);
        Assert.Null(fixture.Db.Database.CurrentTransaction);
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, LockInterceptor locks) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public LockInterceptor Locks { get; } = locks;
        public BookingService Bookings { get; } = new(db, new TelegramNotificationService(
            new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"), db,
            NullLogger<TelegramNotificationService>.Instance));
        public CartService Carts => new(Db, Bookings);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var locks = new LockInterceptor();
            var db = new TestDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection).AddInterceptors(locks).Options);
            await db.Database.EnsureCreatedAsync();
            // No linked Telegram accounts, so these tests never send network requests.
            db.Users.AddRange(new User { Id = 1, Login = "owner", Name = "Owner" },
                new User { Id = 2, Login = "admin", Name = "Admin", Role = UserRole.Admin });
            db.EqModels.Add(new EqModel { Id = 1, Name = "Camera", Access = EqAccess.User });
            db.EqItems.AddRange(new EqItem { Id = 1, EqModelId = 1, InventoryNumber = "CAM-1" },
                new EqItem { Id = 2, EqModelId = 1, InventoryNumber = "CAM-2" });
            await db.SaveChangesAsync();
            return new Fixture(connection, db, locks);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }

    private sealed class LockInterceptor : DbCommandInterceptor
    {
        public List<int> ModelIds { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            if (command.Parameters.Count == 2 && Convert.ToInt32(command.Parameters[0].Value) == 1)
                ModelIds.Add(Convert.ToInt32(command.Parameters[1].Value));
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }
}
