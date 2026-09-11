namespace ChattingWebsite.Model;

/// <summary>
/// 加群申请状态
/// </summary>
public enum JoinRequestStatus
{
    Pending = 0,   // 等待审核
    Accepted = 1,  // 已通过
    Rejected = 2   // 已拒绝
}

/// <summary>
/// 加群申请实体。
/// 普通群组需要群主/管理员审核后才能加入。
/// 默认群（IsDefault=true）跳过此流程，直接入群。
/// </summary>
public class GroupJoinRequest
{
    /// <summary>申请 ID（自增主键）</summary>
    public int Id { get; set; }

    /// <summary>目标群组 ID</summary>
    public string GroupId { get; set; }

    /// <summary>申请人用户 PublicId</summary>
    public string UserId { get; set; }

    /// <summary>申请状态</summary>
    public JoinRequestStatus Status { get; set; } = JoinRequestStatus.Pending;

    /// <summary>申请时间</summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>审核时间（通过或拒绝时填写）</summary>
    public DateTime? RespondedAt { get; set; }
}
