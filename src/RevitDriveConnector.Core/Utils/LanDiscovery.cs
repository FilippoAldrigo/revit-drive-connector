using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Core.Utils
{
 public class LanDiscoveryBroadcaster : IDisposable
 {
 public const int DiscoveryPort = 54321;
 private readonly UdpClient _udpClient;
 private readonly string _serverUrl;
 private CancellationTokenSource? _cts;

 public LanDiscoveryBroadcaster(string serverUrl)
 {
 _serverUrl = serverUrl;
 _udpClient = new UdpClient { EnableBroadcast = true };
 }

 public void Start()
 {
 _cts = new CancellationTokenSource();
 _ = BroadcastLoopAsync(_cts.Token);
 }

 private async Task BroadcastLoopAsync(CancellationToken ct)
 {
 var endPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
 var messageBytes = Encoding.UTF8.GetBytes($"REVIT_DRIVE_COORDINATOR|{_serverUrl}");

 while (!ct.IsCancellationRequested)
 {
 try
 {
 await _udpClient.SendAsync(messageBytes, messageBytes.Length, endPoint);
 await Task.Delay(5000, ct);
 }
 catch (OperationCanceledException)
 {
 break;
 }
 catch
 {
 await Task.Delay(10000, ct);
 }
 }
 }

 public void Dispose()
 {
 _cts?.Cancel();
 _udpClient.Dispose();
 }
 }

 public class LanDiscoveryListener : IDisposable
 {
 public const int DiscoveryPort = 54321;
 private UdpClient? _udpClient;
 private CancellationTokenSource? _cts;
 public event Action<string>? OnCoordinatorDiscovered;

 public void Start()
 {
 try
 {
 _udpClient = new UdpClient();
 _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
 _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

 _cts = new CancellationTokenSource();
 _ = ListenLoopAsync(_cts.Token);
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[LanDiscoveryListener] Errore avvio: {ex.Message}");
 }
 }

 private async Task ListenLoopAsync(CancellationToken ct)
 {
 while (!ct.IsCancellationRequested && _udpClient != null)
 {
 try
 {
 var result = await _udpClient.ReceiveAsync(ct);
 string message = Encoding.UTF8.GetString(result.Buffer);

 if (message.StartsWith("REVIT_DRIVE_COORDINATOR|"))
 {
 string remoteUrl = message.Substring("REVIT_DRIVE_COORDINATOR|".Length);
 // Sostituisce localhost con l'indirizzo IP del mittente
 if (remoteUrl.Contains("localhost") || remoteUrl.Contains("127.0.0.1"))
 {
 remoteUrl = remoteUrl.Replace("localhost", result.RemoteEndPoint.Address.ToString())
 .Replace("127.0.0.1", result.RemoteEndPoint.Address.ToString());
 }

 OnCoordinatorDiscovered?.Invoke(remoteUrl);
 }
 }
 catch (OperationCanceledException)
 {
 break;
 }
 catch
 {
 await Task.Delay(2000, ct);
 }
 }
 }

 public void Dispose()
 {
 _cts?.Cancel();
 _udpClient?.Dispose();
 }
 }
}
