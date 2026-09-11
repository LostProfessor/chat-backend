namespace ChattingWebsite.Model;

/// <summary>
/// 群组公告实体。
/// 每个群组可有多条公告，支持历史查看。
/// GroupId = "public" 表示全服公告。
/// </summary>
public class Announcement
{
    /// <summary>公告 ID（自增主键）</summary>
    public int Id { get; set; }

    /// <summary>所属群组 ID（"public" = 全服公告）</summary>
    public string GroupId { get; set; }

    /// <summary>发布者 PublicId</summary>
    public string PublisherId { get; set; }

    /// <summary>公告内容</summary>
    public string Content { get; set; }

    /// <summary>发布时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
