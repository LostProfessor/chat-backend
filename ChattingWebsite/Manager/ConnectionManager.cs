
using System.Collections.Concurrent;
using ChattingWebsite.Services;

namespace ChattingWebsite.Services;

/// <summary>
/// 管理所有在线 WebSocket 连接（支持同一用户多个连接）。
///
/// ⚠ 单实例约束：连接表存在【进程内存】里，所以整个后端只能跑一个实例。
///   若同时跑两个后端进程，HTTP 请求和 WebSocket 连接会落到不同进程，
///   广播（新消息 / 撤回 / 上传完成）会静默丢失 —— 因为发送方在自己的连接表里查不到连接。
///   要水平扩展必须把连接表迁到 Redis 之类的共享存储，并用发布订阅做跨实例广播。
///   （FileTransferSessionManager 的分块会话同理，也是进程内状态。）
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<string, List<MyWebSocketServer>> _connections = new();
    private readonly ConcurrentDictionary<string, string> _userNicknames = new();

    public void AddConnection(string userId, string nickname, MyWebSocketServer socket)
    {
        _userNicknames.TryAdd(userId, nickname);
        _connections.AddOrUpdate(userId,
            _ => new List<MyWebSocketServer> { socket },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(socket);
                }
                return list;
            });
    }

    public void RemoveConnection(string userId, MyWebSocketServer socket)
    {
        if (_connections.TryGetValue(userId, out var list))
        {
            lock (list)
            {
                list.Remove(socket);
                if (list.Count == 0)
                {
                    _connections.TryRemove(userId, out _);
                    _userNicknames.TryRemove(userId, out _);
                }
            }
        }
    }

    public IReadOnlyList<MyWebSocketServer> GetConnections(string userId)
    {
        if (_connections.TryGetValue(userId, out var list))
        {
            lock (list)
            {
                return list.ToList();
            }
        }
        return Array.Empty<MyWebSocketServer>();
    }

    public IEnumerable<MyWebSocketServer> GetAllConnections()
    {
        foreach (var list in _connections.Values)
        {
            lock (list)
            {
                foreach (var socket in list)
                    yield return socket;
            }
        }
    }

    public string GetNickname(string userId) =>
        _userNicknames.TryGetValue(userId, out var nick) ? nick : null;

    public bool IsOnline(string userId) => _connections.ContainsKey(userId);

    /// <summary>获取所有在线用户（userId + nickname）</summary>
    public IEnumerable<(string userId, string nickname)> GetOnlineUsers()
    {
        foreach (var kv in _connections)
        {
            lock (kv.Value)
            {
                if (kv.Value.Any(s => s.IsOpen))
                {
                    var nick = GetNickname(kv.Key);
                    if (nick != null)
                        yield return (kv.Key, nick);
                }
            }
        }
    }
}