using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Jibo.Cloud.Infrastructure.Media;

/// <summary>
/// Center-crops and downscales loop-member profile photos to a square JPEG sized for
/// Jibo's 330×330 ContactButton (stored at 384×384 to avoid upscaling).
/// </summary>
public sealed class LoopMemberPhotoProcessor
{
    public const int TargetSize = 384;
    public const int MaxUploadBytes = 8 * 1024 * 1024;
    public const int JpegQuality = 82;

    public sealed record ProcessedPhoto(byte[] Content, string ContentType, string ContentHash, int Width, int Height);

    public ProcessedPhoto Process(byte[] uploadBytes)
    {
        ArgumentNullException.ThrowIfNull(uploadBytes);
        if (uploadBytes.Length == 0)
            throw new InvalidOperationException("Photo upload was empty.");
        if (uploadBytes.Length > MaxUploadBytes)
            throw new InvalidOperationException($"Photo upload exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit.");

        try
        {
            using var image = Image.Load(uploadBytes);
            var side = Math.Min(image.Width, image.Height);
            if (side <= 0)
                throw new InvalidOperationException("Photo dimensions are invalid.");

            var cropX = (image.Width - side) / 2;
            var cropY = (image.Height - side) / 2;
            image.Mutate(ctx =>
            {
                ctx.Crop(new Rectangle(cropX, cropY, side, side));
                ctx.Resize(TargetSize, TargetSize);
            });

            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = JpegQuality });
            var content = output.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            return new ProcessedPhoto(content, "image/jpeg", hash, TargetSize, TargetSize);
        }
        catch (UnknownImageFormatException)
        {
            throw new InvalidOperationException("Upload is not a recognized image format.");
        }
        catch (ImageFormatException)
        {
            throw new InvalidOperationException("Upload is not a valid image.");
        }
    }

    public static string MediaStorePath(string loopId, string memberId) =>
        $"loop-member-photo/{Sanitize(loopId)}/{Sanitize(memberId)}";

    private static string Sanitize(string value)
    {
        var trimmed = value.Trim();
        Span<char> buffer = stackalloc char[trimmed.Length];
        var written = 0;
        foreach (var ch in trimmed)
        {
            buffer[written++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_';
        }

        return new string(buffer[..written]);
    }
}
