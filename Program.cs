using System.Security.Claims;
using System.Text;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Services;
using GUtv_backend_dotnet.Services.Telegram;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT Key is not set");
if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("JWT Key is not set");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy("ConfiguredOrigins", policy =>
    {
        if (builder.Environment.IsDevelopment() && allowedOrigins.Length == 0)
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        if (allowedOrigins.Length == 0)
            throw new InvalidOperationException("Cors:AllowedOrigins must be configured outside Development");

        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }));

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<AnnouncementService>();
builder.Services.AddScoped<BotSecurityService>();

var botToken = builder.Configuration["BotConfiguration:BotToken"]
    ?? throw new InvalidOperationException("Bot Token is not configured");
if (string.IsNullOrWhiteSpace(botToken))
    throw new InvalidOperationException("Bot Token is not configured");

builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
builder.Services.AddSingleton<TelegramUpdateHandler>();
builder.Services.AddSingleton<AnnouncementBotHandler>();
builder.Services.AddScoped<TelegramNotificationService>();
builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddHostedService<AnnouncementDeliveryWorker>();

builder.Services
    .AddGraphQLServer()
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
    .AddAuthorization();

var app = builder.Build();

app.UseCors("ConfiguredOrigins");
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var db = context.RequestServices.GetRequiredService<AppDbContext>();
        var currentUser = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Banned, user.Role })
            .SingleOrDefaultAsync(context.RequestAborted);

        if (currentUser is null || currentUser.Banned)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Authorize against current permissions, even while an older JWT is still valid.
        CurrentUserClaims.SetRole(context.User, currentUser.Role);
    }

    await next();
});
app.UseAuthorization();

app.MapGraphQL();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.Run();
