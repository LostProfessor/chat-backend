using System.Net.WebSockets;
using System.Text;

namespace ChattingWebsite.Helper
{
    public class MessageManager
    {
        public static async Task SendMessage(WebSocket socket, string message)
        {
            if (socket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                //
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public static async Task BroadcastMessage(string message)
        {
            Console.WriteLine(message);
        }
    }
}
