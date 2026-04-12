using System.Security.Claims;
using System.Text;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT Key is not set");

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

builder.Services.AddCors(options =>
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddScoped<CartService>();

var botToken = builder.Configuration["BotConfiguration:BotToken"]
    ?? throw new InvalidOperationException("Bot Token is not configured");

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
    .AddTypeExtension<EventQueries>()
    .AddTypeExtension<EventMutations>()
    .AddTypeExtension<CartQueries>()
    .AddTypeExtension<CartMutations>()
    .AddSorting()
    .AddAuthorization();

var app = builder.Build();

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapGraphQL();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.Run();
