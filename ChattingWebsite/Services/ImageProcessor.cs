using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ChattingWebsite.Services;

/// <summary>
/// 图片处理器：生成最大宽度 400px 的 JPEG 缩略图。
/// 透明 PNG 铺白底；动图（gif/webp）取第一帧（ImageSharp 默认行为）。
/// 生成失败则回退用原图作为缩略图。
/// </summary>
public class ImageProcessor : IMediaProcessor
{
    public string MediaType => "image";

    public Task<MediaResult> ProcessAsync(string savedPath, string relativeUrl, string saveDir, string mediaId)
    {
        try
        {
            var thumbFile = $"{mediaId}_thumb.jpg";
            var thumbPath = Path.Combine(saveDir, thumbFile);

            using (var image = Image.Load(savedPath))
            {
                const int maxWidth = 400;
                if (image.Width > maxWidth)
                {
                    var ratio = (double)maxWidth / image.Width;
                    var newHeight = Math.Max(1, (int)(image.Height * ratio));
                    image.Mutate(x => x.Resize(maxWidth, newHeight));
                }
                // 透明背景转 JPEG 前铺白，避免变黑
                image.Mutate(x => x.BackgroundColor(Color.White));
                image.SaveAsJpeg(thumbPath);
            }

            // 缩略图与原图同目录：把原 URL 的最后一段替换为缩略图文件名
            var dir = relativeUrl[..(relativeUrl.LastIndexOf('/') + 1)];
            return Task.FromResult(new MediaResult(relativeUrl, $"{dir}{thumbFile}"));
        }
        catch
        {
            // 缩略图失败 → 回退用原图
            return Task.FromResult(new MediaResult(relativeUrl, relativeUrl));
        }
    }
}
