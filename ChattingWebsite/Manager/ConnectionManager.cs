
using System.Collections.Concurrent;
using ChattingWebsite.Services;

namespace ChattingWebsite.Services;

/// <summary>
/// 管理所有在线 WebSocket 连接（支持同一用户多个连接）
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