using FluentAssertions;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Infrastructure.Services;
using SkiaSharp;

namespace ProjectManagementWeb.UnitTests;

public sealed class AvatarImageProcessorTests
{
    private readonly AvatarImageProcessor _processor = new();

    [TestCase(SKEncodedImageFormat.Jpeg, "avatar.jpg", "image/jpeg")]
    [TestCase(SKEncodedImageFormat.Jpeg, "avatar.jpeg", "image/jpeg")]
    [TestCase(SKEncodedImageFormat.Png, "avatar.png", "image/png")]
    public void 已剪裁圖片應重新編碼為1080正方形(
        SKEncodedImageFormat format, string fileName, string contentType)
    {
        byte[] source = CreateImage(1080, 1080, format);

        var result = _processor.Process(source, fileName, contentType);

        result.IsSuccess.Should().BeTrue();
        AvatarImageResponse image = result.Value!;
        image.ContentType.Should().Be(contentType);
        image.Content.Should().NotBeEmpty();
        using SKBitmap decoded = SKBitmap.Decode(image.Content);
        decoded.Width.Should().Be(1080);
        decoded.Height.Should().Be(1080);
        decoded.GetPixel(0, 0).Red.Should().BeGreaterThan(200);
    }

    [TestCase(1079, 1080)]
    [TestCase(1080, 1079)]
    [TestCase(1081, 1080)]
    [TestCase(1080, 1081)]
    public void 非1080正方形應要求先剪裁(int width, int height)
    {
        var result = _processor.Process(CreateImage(width, height, SKEncodedImageFormat.Png),
            "avatar.png", "image/png");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("avatar_dimensions_invalid");
        result.Error.StatusCode.Should().Be(400);
    }

    [TestCase("avatar.gif", "image/gif")]
    [TestCase("avatar.png", "image/jpeg")]
    [TestCase("avatar.jpg", "image/png")]
    public void 檔名或宣告格式與實際圖片不符應拒絕(string fileName, string contentType)
    {
        var result = _processor.Process(CreateImage(1080, 1080, SKEncodedImageFormat.Png),
            fileName, contentType);

        result.Error!.Code.Should().Be("avatar_format_invalid");
        result.Error.StatusCode.Should().Be(415);
    }

    [Test]
    public void 損壞圖片應拒絕()
    {
        byte[] damaged = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];

        var result = _processor.Process(damaged, "avatar.png", "image/png");

        result.Error!.Code.Should().Be("avatar_image_invalid");
    }

    [TestCase(0)]
    [TestCase(AvatarImageProcessor.MaximumUploadBytes + 1)]
    public void 空檔或超出上傳限制應拒絕(int length)
    {
        var result = _processor.Process(new byte[length], "avatar.png", "image/png");

        result.Error!.Code.Should().Be("avatar_size_invalid");
        result.Error.StatusCode.Should().Be(413);
    }

    private static byte[] CreateImage(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Red);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }
}
