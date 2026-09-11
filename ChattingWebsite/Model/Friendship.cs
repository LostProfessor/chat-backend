namespace ChattingWebsite.Model
{
    /// <summary>
    /// 好友关系状态
    /// </summary>
    public enum FriendshipStatus
    {
        Pending = 0,   // 等待确认
        Accepted = 1,  // 已接受（成为好友）
        Rejected = 2   // 已拒绝
    }

    /// <summary>
    /// 好友关系实体
    /// 同时承载"好友申请"和"好友关系"两种含义，通过 Status 区分
    /// </summary>
    public class Friendship
    {
        /// <summary>关系 ID（自增主键）</summary>
        public int Id { get; set; }

        /// <summary>发起方用户 ID（加好友的请求者）</summary>
        public string RequesterId { get; set; }

        /// <summary>接收方用户 ID（被加好友的人）</summary>
        public string AddresseeId { get; set; }

        /// <summary>当前状态</summary>
        public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

        /// <summary>请求发起时间</summary>
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        /// <summary>响应时间（接受或拒绝时填写）</summary>
        public DateTime? RespondedAt { get; set; }
    }
}
