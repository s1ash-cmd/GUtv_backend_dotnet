using System.Net;
using System.Net.Http.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL;
using GUtv_backend_dotnet.Services;
using GUtv_backend_dotnet.Services.Telegram;
using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace GUtv_backend_dotnet.Tests;

public class GraphqlSecurityTests
{
    private const string Introspection = "{ __schema { queryType { name } } }";

    [Fact]
    public async Task AnonymousQueriesCannotReachUsersOrBookings()
    {
        var executor = await CreateExecutorAsync(Environments.Production);
        var reachable = new HashSet<string>();
        var pending = new Stack<INamedType>(executor.Schema.QueryType.Fields
            .Where(field => !field.IsIntrospectionField && field.Directives.All(d => d.Type.Name != "authorize"))
            .Select(field => field.Type.NamedType()));
        while (pending.TryPop(out var type))
        {
            if (!reachable.Add(type.Name) || type is not IComplexOutputType complex) continue;
            foreach (var field in complex.Fields.Where(field => !field.IsIntrospectionField))
                pending.Push(field.Type.NamedType());
        }

        Assert.Contains("EqModel", reachable);
        Assert.Contains("EqItem", reachable);
        foreach (var name in new[] { "User", "Booking", "BookingItem", "Cart", "CartItem", "UserSession",
                     "BookingEquipmentChangePayload", "BookingEquipmentSnapshot" })
            Assert.DoesNotContain(name, reachable);
    }

    [Fact]
    public async Task ReverseNavigationsAreNotPartOfTheSchema()
    {
        var schema = (await CreateExecutorAsync(Environments.Production)).Schema;
        var hidden = new Dictionary<string, string[]>
        {
            ["EqItem"] = ["bookingItems"],
            ["EqModel"] = ["cartItems"],
            ["User"] = ["bookings", "cart"],
            ["BookingItem"] = ["booking"],
            ["Cart"] = ["user"],
            ["CartItem"] = ["cart"]
        };
        foreach (var (type, fields) in hidden)
        foreach (var field in fields)
            Assert.False(schema.GetType<ObjectType>(type).Fields.ContainsField(field), $"{type}.{field}");

        // Navigations the frontend relies on stay available.
        Assert.True(schema.GetType<ObjectType>("Booking").Fields.ContainsField("user"));
        Assert.True(schema.GetType<ObjectType>("Booking").Fields.ContainsField("bookingItems"));
        Assert.True(schema.GetType<ObjectType>("BookingItem").Fields.ContainsField("eqItem"));
        Assert.True(schema.GetType<ObjectType>("Cart").Fields.ContainsField("items"));
        Assert.True(schema.GetType<ObjectType>("CartItem").Fields.ContainsField("eqModel"));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task IntrospectionIsAvailableOnlyInDevelopment(string environment, bool allowed)
    {
        var executor = await CreateExecutorAsync(environment);

        var introspection = Assert.IsAssignableFrom<IOperationResult>(await executor.ExecuteAsync(Introspection));
        Assert.Equal(allowed, introspection.Errors is null or { Count: 0 });

        // Health checks of the server dashboard use `{ __typename }`.
        var health = Assert.IsAssignableFrom<IOperationResult>(await executor.ExecuteAsync("{ __typename }"));
        Assert.True(health.Errors is null or { Count: 0 });
    }

    [Fact]
    public async Task QueriesDeeperThanTheLimitAreRejected()
    {
        var executor = await CreateExecutorAsync(Environments.Production);

        // A frontend-shaped query of depth 6 passes validation; it fails only on authorization.
        var regular = Assert.IsAssignableFrom<IOperationResult>(await executor.ExecuteAsync(
            "{ myBookingsPage { items { bookingItems { eqItem { eqModel { id } } } } } }"));
        Assert.DoesNotContain(regular.Errors ?? [], error => error.Message.Contains("depth"));

        var deep = Assert.IsAssignableFrom<IOperationResult>(await executor.ExecuteAsync(
            "{ allEquipmentModels { eqItems { eqModel { eqItems { eqModel { eqItems { eqModel { eqItems { eqModel { eqItems { id } } } } } } } } } } }"));
        Assert.Contains(deep.Errors ?? [], error => error.Message.Contains("depth"));
    }

    [Fact]
    public async Task ContractOfTheChangedOperationsStaysCompatibleWithDeployedPages()
    {
        var schema = (await CreateExecutorAsync(Environments.Production)).Schema;
        static string Format(IType type) => type switch
        {
            NonNullType nonNull => Format(nonNull.Type) + "!",
            ListType list => $"[{Format(list.ElementType)}]",
            INamedType named => named.Name,
            _ => throw new ArgumentException("Unknown GraphQL type", nameof(type))
        };

        // A page loaded before the cookie existed still sends its token; the new one sends nothing.
        var refresh = schema.MutationType!.Fields["refreshToken"];
        Assert.Equal("String", Format(refresh.Arguments["refreshToken"].Type));
        var auth = Assert.IsAssignableFrom<ObjectType>(refresh.Type.NamedType());
        Assert.Equal("String", Format(auth.Fields["refreshToken"].Type));
        Assert.Equal("String!", Format(auth.Fields["accessToken"].Type));

        // Every cart detail may be omitted (kept) or null (cleared).
        var details = Assert.IsAssignableFrom<InputObjectType>(
            schema.MutationType.Fields["setCartDetails"].Arguments["input"].Type.NamedType());
        Assert.Equal(new[] { "comment:String", "endTime:DateTime", "reason:String", "startTime:DateTime" },
            details.Fields.Select(field => $"{field.Name}:{Format(field.Type)}").Order());

        // The old page still passes a quantity; it is accepted and ignored.
        var add = schema.MutationType.Fields["addCartItem"];
        Assert.Equal("Int!", Format(add.Arguments["eqModelId"].Type));
        Assert.Equal("Int", Format(add.Arguments["selectedItemId"].Type));
        Assert.Equal("Int", Format(add.Arguments["quantity"].Type));
        Assert.True(add.Arguments["quantity"].IsDeprecated);

        var users = schema.QueryType.Fields["usersPage"];
        Assert.Equal(new[] { "Admin" },
            Assert.Single(users.Directives, d => d.Type.Name == "authorize").AsValue<AuthorizeDirective>().Roles);
        Assert.Equal("UserListGroup!", Format(users.Arguments["group"].Type));
        Assert.Equal("All", users.Arguments["group"].DefaultValue!.ToString());
        Assert.Equal(new[] { "Admins", "All", "Banned", "Osnova", "Users" },
            Assert.IsAssignableFrom<EnumType>(users.Arguments["group"].Type.NamedType()).Values.Select(v => v.Name).Order());
        Assert.True(schema.QueryType.Fields["users"].IsDeprecated);
        // The archive marker is internal: the catalogue simply stops listing such units.
        Assert.DoesNotContain(schema.GetType<ObjectType>("EqItem").Fields, field => field.Name == "archivedAt");
    }

    [Fact]
    public async Task CartDetailsInputTellsAnOmittedFieldFromAnExplicitNull()
    {
        var executor = await new ServiceCollection().AddGraphQLServer()
            .AddQueryType<CartDetailsProbe>().BuildRequestExecutorAsync();

        async Task<string> Probe(string input)
        {
            var result = await executor.ExecuteAsync($"{{ probe(input: {input}) }}");
            return System.Text.Json.JsonDocument.Parse(result.ToJson())
                .RootElement.GetProperty("data").GetProperty("probe").GetString()!;
        }

        Assert.Equal("reason=<kept> start=<kept> end=<kept> comment=<kept>", await Probe("{}"));
        Assert.Equal("reason=<null> start=<kept> end=<kept> comment=x",
            await Probe("{ reason: null, comment: \"x\" }"));
        Assert.Equal("reason=r start=2030-01-10T10:00:00Z end=<null> comment=<kept>",
            await Probe("{ reason: \"r\", startTime: \"2030-01-10T10:00:00Z\", endTime: null }"));
    }

    public class CartDetailsProbe
    {
        public string Probe(UpdateCartDetailsInput input)
        {
            static string Show<T>(Optional<T> value, Func<T, string> format) =>
                !value.HasValue ? "<kept>" : value.Value is null ? "<null>" : format(value.Value);
            return $"reason={Show(input.Reason, v => v!)} " +
                   $"start={Show(input.StartTime, v => v!.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))} " +
                   $"end={Show(input.EndTime, v => v!.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))} " +
                   $"comment={Show(input.Comment, v => v!)}";
        }
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task NitroAndSchemaDownloadAreServedOnlyInDevelopment(string environment, bool served)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddSchemaServices(builder.Services);
        builder.Services.AddGutvGraphQL(builder.Environment);
        await using var app = builder.Build();
        app.MapGutvGraphQL();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var nitroRequest = new HttpRequestMessage(HttpMethod.Get, "/graphql/");
            nitroRequest.Headers.Accept.ParseAdd("text/html");
            using var nitro = await client.SendAsync(nitroRequest);
            var isNitro = nitro.IsSuccessStatusCode && nitro.Content.Headers.ContentType?.MediaType == "text/html";
            Assert.Equal(served, isNitro);

            using var sdl = await client.GetAsync("/graphql?sdl");
            var isSdl = sdl.IsSuccessStatusCode && (await sdl.Content.ReadAsStringAsync()).Contains("type Query");
            Assert.Equal(served, isSdl);

            // Regular POST requests keep working in every environment.
            using var post = await client.PostAsJsonAsync("/graphql", new { query = "{ __typename }" });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(string environment)
    {
        var services = new ServiceCollection();
        AddSchemaServices(services);
        services.AddGutvGraphQL(new TestEnvironment(environment));
        var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync();
    }

    // Resolver services are only constructed when a field runs; the schema needs them registered.
    private static void AddSchemaServices(IServiceCollection services)
    {
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql("Host=127.0.0.1;Database=unused"));
        services.AddScoped<UserService>();
        services.AddScoped<UserSessionService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAvatarService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<EquipmentPhotoService>();
        services.AddScoped<BookingService>();
        services.AddScoped<CartService>();
        services.AddScoped<AnnouncementService>();
        services.AddScoped<TelegramNotificationService>();
        services.AddSingleton<RequestRateLimiter>();
        services.AddSingleton<AvatarImageStore>();
        services.AddSingleton<EquipmentImageStore>();
        services.AddSingleton<ITelegramBotClient>(new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"));
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "GUtv_backend_dotnet";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
