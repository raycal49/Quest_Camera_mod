using System;
using System.Collections;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using Unity.VisualScripting.Antlr3.Runtime;

public class Socket
{
	private readonly ClientWebSocket _socket = new ClientWebSocket();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    public string Url;

	public Socket()
    {
        _socket.Options.AddSubProtocol("json.webpubsub.azure.v1");

    }

    public void SetUrl(string url)
    {
        Url = url;
    }

    public Task Send(object data)
    {
        var json = JsonConvert.SerializeObject(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        return _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
    }

    public async Task ReceiveLoop(ConcurrentQueue<string> messageQueue)
    {
        byte[] buffer = new byte[8192];

        while (_socket.State == WebSocketState.Open)
        {
            string message = await ReceiveMessage(buffer);

            if (string.IsNullOrEmpty(message) == false)
            {
                messageQueue.Enqueue(message);
            }
        }
    }

    private async Task<string> ReceiveMessage(byte[] buffer)
    {
        var sb = new StringBuilder();

        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        return sb.ToString();
    }

    public async Task ConnectWebSocket(string url)
    {
        await _socket.ConnectAsync(new Uri(url), _cts.Token);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
