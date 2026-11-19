using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Core.IPC
{
 public class NamedPipeIpcServer
 {
 public const string PipeName = "RevitDriveConnector_IPC";
 private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
 private CancellationTokenSource? _cts;

 public NamedPipeIpcServer(Func<IpcRequest, Task<IpcResponse>> handler)
 {
 _handler = handler ?? throw new ArgumentNullException(nameof(handler));
 }

 public void Start()
 {
 _cts = new CancellationTokenSource();
 _ = ListenLoopAsync(_cts.Token);
 }

 public void Stop()
 {
 _cts?.Cancel();
 }

 private async Task ListenLoopAsync(CancellationToken ct)
 {
 while (!ct.IsCancellationRequested)
 {
 try
 {
 var pipeServer = new NamedPipeServerStream(
 PipeName,
 PipeDirection.InOut,
 NamedPipeServerStream.MaxAllowedServerInstances,
 PipeTransmissionMode.Byte,
 PipeOptions.Asynchronous);

 await pipeServer.WaitForConnectionAsync(ct);
 _ = HandleClientAsync(pipeServer, ct);
 }
 catch (OperationCanceledException)
 {
 break;
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[NamedPipeServer] Errore in attesa connessione: {ex.Message}");
 await Task.Delay(500, ct);
 }
 }
 }

 private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
 {
 using (pipe)
 {
 try
 {
 // Read request length
 var lengthBytes = new byte[4];
 int read = 0;
 while (read < 4)
 {
 int r = await pipe.ReadAsync(lengthBytes, read, 4 - read, ct);
 if (r == 0) return;
 read += r;
 }

 int requestLength = BitConverter.ToInt32(lengthBytes, 0);
 var requestBytes = new byte[requestLength];
 read = 0;
 while (read < requestLength)
 {
 int r = await pipe.ReadAsync(requestBytes, read, requestLength - read, ct);
 if (r == 0) return;
 read += r;
 }

 var requestJson = Encoding.UTF8.GetString(requestBytes);
 var request = JsonSerializer.Deserialize<IpcRequest>(requestJson);

 IpcResponse response;
 if (request == null)
 {
 response = new IpcResponse { Success = false, ErrorMessage = "Richiesta non valida" };
 }
 else
 {
 response = await _handler(request);
 }

 var responseJson = JsonSerializer.Serialize(response);
 var respBytes = Encoding.UTF8.GetBytes(responseJson);
 var respLenBytes = BitConverter.GetBytes(respBytes.Length);

 await pipe.WriteAsync(respLenBytes, 0, 4, ct);
 await pipe.WriteAsync(respBytes, 0, respBytes.Length, ct);
 await pipe.FlushAsync(ct);
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[NamedPipeServer] Errore gestione client: {ex.Message}");
 }
 }
 }
 }
}
