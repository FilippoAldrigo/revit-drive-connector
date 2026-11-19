using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Services;

namespace RevitDriveConnector.Core.IPC
{
    public class NamedPipeIpcClient
    {
        public const string PipeName = "RevitDriveConnector_IPC";
        public static Func<Task>? EnsureDaemonRunningHandler { get; set; }

        public async Task<IpcResponse> SendRequestAsync(IpcRequest request, int timeoutMs = 10000, CancellationToken ct = default)
        {
            // Esegue direttamente nel motore in-process (LocalServiceEngine) evitando qualsiasi blocco
            // di processi esterni .exe da parte di Windows 11 Smart App Control o firewall locali.
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                return await LocalServiceEngine.HandleIpcRequestAsync(request, linkedCts.Token);
            }
            catch (Exception ex)
            {
                return new IpcResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    ErrorMessage = $"Errore esecuzione servizio cloud: {ex.Message}"
                };
            }
        }

        public Task<bool> PingServerAsync(int timeoutMs = 1000, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }
    }
}
