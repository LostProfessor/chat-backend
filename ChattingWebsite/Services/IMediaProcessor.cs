namespace ChattingWebsite.Services;

/// <summary>媒体后处理结果：文件 URL + 可选缩略图 URL</summary>
public record MediaResult(string Url, string? ThumbUrl);

/// <summary>
/// 媒体处理器：文件接收完成后做"类型相关"的后处理（缩略图 / 封面 / 图标等）。
///
/// 扩展方式：新增文件类型只需实现本接口并注册到 DI，无需改动传输核心。
/// 若某类型没有对应处理器，则不做后处理（ThumbUrl 为 null）。
/// </summary>
public interface IMediaProcessor
{
    /// <summary>处理的媒体类型：image / video / audio / file</summary>
    string MediaType { get; }

    /// <summary>
    /// 处理已保存的文件。
    /// </summary>
    /// <param name="savedPath">已保存文件的绝对路径</param>
    /// <param name="relativeUrl">文件的相对 URL（如 /uploads/images/xxx.png）</param>
    /// <param name="saveDir">文件所在目录（缩略图写到同目录）</param>
    /// <param name="mediaId">媒体唯一 ID（用于缩略图命名）</param>
    Task<MediaResult> ProcessAsync(string savedPath, string relativeUrl, string saveDir, string mediaId);
}
