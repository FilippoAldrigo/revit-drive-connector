using System;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services;

namespace RevitDriveConnector.Core.IPC
{
    /// <summary>
    /// Direct in-process service dispatcher (100% serverless, no background Daemon/NamedPipe required).
    /// Supports real-time TransferProgressInfo callbacks for chunked uploads/downloads.
    /// </summary>
    public class NamedPipeIpcClient
    {
        public async Task<IpcResponse> SendRequestAsync(
            IpcRequest request,
            int timeoutMs = 10000,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            try
            {
                using var timeoutCts = timeoutMs > 0
                    ? new CancellationTokenSource(timeoutMs)
                    : new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                return await LocalServiceEngine.HandleIpcRequestAsync(request, linkedCts.Token, progressCallback);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new IpcResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    ErrorMessage = "Operazione annullata dall'utente."
                };
            }
            catch (OperationCanceledException)
            {
                return new IpcResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    ErrorMessage = $"Timeout dell'operazione cloud ({timeoutMs / 1000}s). Verifica la connessione Internet e riprova."
                };
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
