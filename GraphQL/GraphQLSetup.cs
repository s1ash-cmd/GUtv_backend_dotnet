using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using HotChocolate.AspNetCore;
using HotChocolate.Execution.Configuration;

namespace GUtv_backend_dotnet.GraphQL;

public static class GraphQLSetup
{
    // The deepest frontend query (myBookingsPage → items → bookingItems → eqItem → eqModel → id) is 6.
    public const int MaxExecutionDepth = 10;

    // Base64 of a 5 MB upload is about 6.7 MB.
    public const int MaxRequestSize = 8 * 1024 * 1024;

    // Outside Development the schema cannot be explored: no Nitro IDE, no SDL download, no introspection.
    // `__typename` stays available for health checks.
    public static IRequestExecutorBuilder AddGutvGraphQL(this IServiceCollection services, IHostEnvironment environment) =>
        services
            .AddGraphQLServer(maxAllowedRequestSize: MaxRequestSize)
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddProjections()
            .AddFiltering()
            .AddType<UserRoleType>()
            .AddType<EqCategoryType>()
            .AddType<EqAccessType>()
            .AddType<BookingStatusType>()
            .AddTypeExtension<UserQueries>()
            .AddTypeExtension<UserMutation>()
            .AddTypeExtension<EquipmentQueries>()
            .AddTypeExtension<EquipmentMutations>()
            .AddTypeExtension<BookingQueries>()
            .AddTypeExtension<BookingMutations>()
            .AddTypeExtension<CartQueries>()
            .AddTypeExtension<CartMutations>()
            .AddTypeExtension<AnnouncementQueries>()
            .AddTypeExtension<AnnouncementMutations>()
            .AddSorting()
            .AddAuthorization()
            .AddMaxExecutionDepthRule(MaxExecutionDepth)
            .DisableIntrospection(!environment.IsDevelopment());

    public static void MapGutvGraphQL(this WebApplication app)
    {
        var isDevelopment = app.Environment.IsDevelopment();
        var options = new GraphQLServerOptions { EnableSchemaRequests = isDevelopment };
        options.Tool.Enable = isDevelopment;
        app.MapGraphQL().WithOptions(options);
    }
}
