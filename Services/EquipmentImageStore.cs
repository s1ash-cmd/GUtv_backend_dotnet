using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace GUtv_backend_dotnet.Services;

public sealed class EquipmentImageStore(IConfiguration configuration, ILogger<EquipmentImageStore> logger)
    : ImageFileStore(configuration["EquipmentPhotoStorage:Path"]
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GUtvBooker", "equipment-photos"),
        "/equipment-photos/", new Size(1600, 1600), ResizeMode.Max, logger);
