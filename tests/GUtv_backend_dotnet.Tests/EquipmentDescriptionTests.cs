using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GUtv_backend_dotnet.Tests;

public class EquipmentDescriptionTests
{
    public static TheoryData<string, string?, string> AcceptedDescriptions => new()
    {
        { "create", null, "" },
        { "create", "", "" },
        { "create", " \t\r\n ", "" },
        { "create", "  abcde  ", "abcde" },
        { "create", "  Описание камеры  ", "Описание камеры" },
        { "update", null, "" },
        { "update", "", "" },
        { "update", " \t\r\n ", "" },
        { "update", "  abcde  ", "abcde" },
        { "update", "  Описание камеры  ", "Описание камеры" },
        { "properties", "", "" },
        { "properties", " \t\r\n ", "" },
        { "properties", "  abcde  ", "abcde" },
        { "properties", "  Описание камеры  ", "Описание камеры" }
    };

    [Theory]
    [MemberData(nameof(AcceptedDescriptions))]
    public async Task OptionalDescriptionIsNormalizedAndPersistedForEveryWritePath(
        string operation, string? description, string expectedDescription)
    {
        await using var db = CreateContext();
        await SeedExistingModelForUpdateAsync(db, operation);

        await WriteModelAsync(new EquipmentService(db), operation, description);

        db.ChangeTracker.Clear();
        var persisted = await db.EqModels.SingleAsync();
        Assert.Equal(expectedDescription, persisted.Description);
        Assert.Equal("Updated camera", persisted.Name);
        // Properties-only edits retain classification; full writes still resolve access.
        Assert.Equal(operation == "properties" ? EqCategory.Sound : EqCategory.Lens, persisted.Category);
        Assert.Equal(operation == "properties" ? EqAccess.Ronin : EqAccess.Osnova, persisted.Access);
    }

    [Theory]
    [InlineData("create", " x ")]
    [InlineData("create", "  abcd  ")]
    [InlineData("update", " x ")]
    [InlineData("update", "  abcd  ")]
    [InlineData("properties", " x ")]
    [InlineData("properties", "  abcd  ")]
    public async Task ShortNonemptyDescriptionIsRejectedBeforeChangingStoredModel(
        string operation, string description)
    {
        await using var db = CreateContext();
        await SeedExistingModelForUpdateAsync(db, operation);

        var error = await Assert.ThrowsAsync<GraphQLException>(() =>
            WriteModelAsync(new EquipmentService(db), operation, description));
        Assert.Contains(error.Errors, item => item.Message == "Описание должно содержать не менее 5 символов");
        Assert.False(db.ChangeTracker.HasChanges());

        db.ChangeTracker.Clear();
        if (operation == "create")
        {
            Assert.Empty(await db.EqModels.ToListAsync());
        }
        else
        {
            var persisted = await db.EqModels.SingleAsync();
            Assert.Equal("Original camera", persisted.Name);
            Assert.Equal("Original description", persisted.Description);
            Assert.Equal(EqCategory.Sound, persisted.Category);
            Assert.Equal(EqAccess.Ronin, persisted.Access);
        }
    }

    [Fact]
    public async Task NullablePropertiesDescriptionCanBeClearedWithNull()
    {
        await using var db = CreateContext();
        await SeedExistingModelForUpdateAsync(db, "properties");

        await WriteModelAsync(new EquipmentService(db), "properties", null);

        db.ChangeTracker.Clear();
        Assert.Equal(string.Empty, (await db.EqModels.SingleAsync()).Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData(", description: null")]
    public async Task GraphqlDescriptionCanBeOmittedOrNullWithoutRelaxingOtherRequiredFields(
        string descriptionField)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGraphQLServer()
            .AddType<EqCategoryType>()
            .AddQueryType(descriptor => descriptor.Name("Query")
                .Field("accepted")
                .Type<NonNullType<BooleanType>>()
                .Argument("create", argument => argument.Type<InputObjectType<CreateEqModelInput>>())
                .Argument("properties", argument => argument.Type<InputObjectType<UpdateEqModelPropertiesInput>>())
                .Resolve(context =>
                    context.ArgumentValue<CreateEqModelInput>("create").Description is null &&
                    context.ArgumentValue<UpdateEqModelPropertiesInput>("properties").Description is null));
        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync();
        var arguments = executor.Schema.QueryType.Fields["accepted"].Arguments;
        var createType = Assert.IsAssignableFrom<InputObjectType>(arguments["create"].Type.NamedType());
        var propertiesType = Assert.IsAssignableFrom<InputObjectType>(arguments["properties"].Type.NamedType());

        Assert.IsType<StringType>(createType.Fields["description"].Type);
        Assert.IsType<StringType>(propertiesType.Fields["description"].Type);
        Assert.IsType<NonNullType>(createType.Fields["name"].Type);
        Assert.IsType<NonNullType>(createType.Fields["category"].Type);
        Assert.IsType<NonNullType>(propertiesType.Fields["name"].Type);

        await using var result = await executor.ExecuteAsync($$"""
            {
                accepted(
                    create: { name: "Camera", category: Camera{{descriptionField}} },
                    properties: { name: "Camera"{{descriptionField}} }
                )
            }
            """);
        var operation = Assert.IsAssignableFrom<IOperationResult>(result);
        Assert.Null(operation.Errors);
        Assert.Equal(true, operation.Data!["accepted"]);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("properties")]
    public async Task MakingDescriptionOptionalDoesNotAllowAnEmptyName(string operation)
    {
        await using var db = CreateContext();
        await SeedExistingModelForUpdateAsync(db, operation);

        var error = await Assert.ThrowsAsync<GraphQLException>(() =>
            WriteModelAsync(new EquipmentService(db), operation, "", "  "));

        Assert.Contains(error.Errors, item => item.Message == "Название не может быть пустым");
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task SeedExistingModelForUpdateAsync(AppDbContext db, string operation)
    {
        if (operation == "create")
            return;

        db.EqModels.Add(new EqModel
        {
            Id = 1,
            Name = "Original camera",
            Description = "Original description",
            Category = EqCategory.Sound,
            Access = EqAccess.Ronin
        });
        await db.SaveChangesAsync();
    }

    private static Task<EqModel> WriteModelAsync(EquipmentService service, string operation,
        string? description, string name = "  Updated camera  ") => operation switch
    {
        "create" => service.CreateModelAsync(new CreateEqModelInput(
            name, description, EqCategory.Lens, "{}", Osnova: true)),
        "update" => service.UpdateModelAsync(1, new CreateEqModelInput(
            name, description, EqCategory.Lens, "{}", Osnova: true)),
        "properties" => service.UpdateModelPropertiesAsync(1, new UpdateEqModelPropertiesInput(
            name, description, null)),
        _ => throw new ArgumentException("Unknown write operation", nameof(operation))
    };
}
