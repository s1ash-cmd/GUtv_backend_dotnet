using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace GUtv_backend_dotnet.Services;

public sealed class AvatarImageStore(IConfiguration configuration, ILogger<AvatarImageStore> logger)
    : ImageFileStore(configuration["AvatarStorage:Path"]
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GUtvBooker", "avatars"),
        "/avatars/", new Size(512, 512), ResizeMode.Crop, logger);
