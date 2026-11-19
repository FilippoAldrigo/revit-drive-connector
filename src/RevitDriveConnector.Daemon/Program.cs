using System;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services;

namespace RevitDriveConnector.Daemon
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "Revit Google Drive Connector - Local Daemon";
            Console.WriteLine("======================================================");
            Console.WriteLine(" REVIT GOOGLE DRIVE CONNECTOR - LOCAL DAEMON v1.2 ");
            Console.WriteLine("======================================================");

            var config = AppConfig.Load();
            Console.WriteLine($"[Config] Cloud Storage Path: {config.CloudStoragePath}");
            Console.WriteLine($"[Config] Local Cache Path: {config.LocalCachePath}");

            await LocalServiceEngine.EnsureInitializedAsync();

            var ipcServer = new NamedPipeIpcServer(req => LocalServiceEngine.HandleIpcRequestAsync(req));
            ipcServer.Start();
            Console.WriteLine($"[IPC] Server Named Pipes avviato su '{NamedPipeIpcServer.PipeName}'. In ascolto per Revit...");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await Task.Delay(-1, cts.Token);
            }
            catch (TaskCanceledException)
            {
            }

            ipcServer.Stop();
            Console.WriteLine("[Daemon] Arresto completato.");
        }
    }
}
