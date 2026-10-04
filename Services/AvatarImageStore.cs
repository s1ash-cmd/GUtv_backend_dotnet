using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using Path = System.IO.Path;

namespace GUtv_backend_dotnet.Services;

// Only re-encoded images are public; original uploads and filesystem paths are never exposed.
public sealed class AvatarImageStore(IConfiguration configuration, ILogger<AvatarImageStore> logger)
{
    public const int MaxFileSize = 5 * 1024 * 1024;
    private const string UrlPrefix = "/avatars/";
    private readonly string directory = Path.GetFullPath(configuration["AvatarStorage:Path"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GUtvBooker", "avatars"));
    private readonly SemaphoreSlim processing = new(2, 2);

    public string? GetPath(string filename)
    {
        if (filename.Length != 37 || !filename.EndsWith(".webp", StringComparison.Ordinal) ||
            !Guid.TryParseExact(filename[..32], "N", out _)) return null;
        return Path.Combine(directory, filename);
    }

    public async Task<string> SaveAsync(string imageBase64, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(imageBase64) || imageBase64.Length > (MaxFileSize + 2) / 3 * 4)
            throw new GraphQLException("Фото должно быть не больше 5 МБ");

        await processing.WaitAsync(cancellationToken);
        string? path = null;
        try
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(imageBase64); }
            catch (FormatException) { throw new GraphQLException("Не удалось прочитать фото"); }
            if (bytes.Length == 0 || bytes.Length > MaxFileSize)
                throw new GraphQLException("Фото должно быть не больше 5 МБ");

            using var input = new MemoryStream(bytes, writable: false);
            var options = new DecoderOptions { MaxFrames = 1, TargetSize = new Size(1024, 1024) };
            var info = await Image.IdentifyAsync(options, input, cancellationToken);
            var format = info.Metadata.DecodedImageFormat?.Name.ToUpperInvariant();
            if (format is not ("JPEG" or "PNG" or "WEBP"))
                throw new GraphQLException("Выберите фото в формате JPG, PNG или WebP");
            if (info.Width > 8000 || info.Height > 8000 || (long)info.Width * info.Height > 20_000_000)
                throw new GraphQLException("Фото слишком большое. Максимум 20 мегапикселей и 8000 пикселей по стороне");

            input.Position = 0;
            using var image = await Image.LoadAsync(options, input, cancellationToken);
            image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
            {
                Size = new Size(512, 512), Mode = ResizeMode.Crop
            }));
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            Directory.CreateDirectory(directory);
            var filename = $"{Guid.NewGuid():N}.webp";
            path = GetPath(filename)!;
            await image.SaveAsWebpAsync(path, new WebpEncoder { Quality = 85, SkipMetadata = true }, cancellationToken);
            return UrlPrefix + filename;
        }
        catch (UnknownImageFormatException) { throw new GraphQLException("Выберите фото в формате JPG, PNG или WebP"); }
        catch (InvalidImageContentException) { throw new GraphQLException("Фото повреждено или имеет неподдерживаемый формат"); }
        catch
        {
            if (path is not null) Delete(UrlPrefix + Path.GetFileName(path));
            throw;
        }
        finally { processing.Release(); }
    }

    public void Delete(string? url)
    {
        if (url is null || !url.StartsWith(UrlPrefix, StringComparison.Ordinal)) return;
        var path = GetPath(url[UrlPrefix.Length..]);
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete an unused avatar image");
        }
    }
}
