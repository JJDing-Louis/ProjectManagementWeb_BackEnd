using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Users;
using SkiaSharp;

namespace ProjectManagementWeb.Infrastructure.Services;

/// <summary>驗證並重新編碼已由使用者確認剪裁的圖片。</summary>
public sealed class AvatarImageProcessor
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    public const int SideLength = 1080;
    public const int MaximumUploadBytes = 10 * 1024 * 1024;
    public const int MaximumStoredBytes = 5 * 1024 * 1024;

    public ServiceResult<AvatarImageResponse> Process(byte[] content, string fileName, string contentType)
    {
        if (content.Length == 0 || content.Length > MaximumUploadBytes)
        {
            return ServiceResult<AvatarImageResponse>.Failure("avatar_size_invalid", "圖片檔案大小不符合限制。", 413);
        }

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        bool jpeg = IsJpeg(content);
        bool png = content.AsSpan().StartsWith(PngSignature);
        if ((!jpeg || (extension is not ".jpg" and not ".jpeg") || contentType != "image/jpeg") &&
            (!png || extension != ".png" || contentType != "image/png"))
        {
            return ServiceResult<AvatarImageResponse>.Failure("avatar_format_invalid", "只支援 JPG、JPEG 與 PNG 圖片。", 415);
        }

        using var encoded = SKData.CreateCopy(content);
        using SKCodec? codec = SKCodec.Create(encoded);
        if (codec is null)
        {
            return InvalidImage();
        }
        if (codec.Info.Width != SideLength || codec.Info.Height != SideLength)
        {
            return ServiceResult<AvatarImageResponse>.Failure(
                "avatar_dimensions_invalid", "請先將圖片剪裁為 1080 × 1080 像素。", 400);
        }

        using SKBitmap? bitmap = SKBitmap.Decode(content);
        if (bitmap is null || bitmap.Width != SideLength || bitmap.Height != SideLength)
        {
            return InvalidImage();
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData? normalized = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png,
            jpeg ? 85 : 100);
        if (normalized is null)
        {
            return InvalidImage();
        }
        byte[] result = normalized.ToArray();
        if (result.Length > MaximumStoredBytes)
        {
            return ServiceResult<AvatarImageResponse>.Failure(
                "avatar_size_invalid", "剪裁後的圖片仍過大，請選擇較小的圖片。", 413);
        }
        return ServiceResult<AvatarImageResponse>.Success(
            new AvatarImageResponse(result, jpeg ? "image/jpeg" : "image/png"));
    }

    private static ServiceResult<AvatarImageResponse> InvalidImage() =>
        ServiceResult<AvatarImageResponse>.Failure("avatar_image_invalid", "圖片無法讀取或內容已損壞。", 400);

    public static bool IsJpeg(ReadOnlySpan<byte> content) => content.StartsWith(JpegSignature);
}
