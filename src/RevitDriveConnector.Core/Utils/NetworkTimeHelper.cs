using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Core.Utils
{
 public static class NetworkTimeHelper
 {
 private static TimeSpan _clockOffset = TimeSpan.Zero;
 private static DateTime _lastSyncUtc = DateTime.MinValue;
 private static readonly SemaphoreSlim _syncLock = new(1, 1);
 private static bool _hasSuccessfulSync = false;

 public static TimeSpan CurrentClockOffset => _clockOffset;
 public static bool IsTimeSynchronized => _hasSuccessfulSync;

 /// <summary>
 /// Restituisce la data/ora UTC autoritativa corretta compensando l'eventuale Clock Skew della macchina locale.
 /// </summary>
 public static DateTime GetAccurateUtc()
 {
 // Se sono passati più di 15 minuti dall'ultima sincronizzazione, avvia refresh asincrono
 if ((DateTime.UtcNow - _lastSyncUtc).TotalMinutes > 15)
 {
 _ = Task.Run(SyncTimeOffsetAsync);
 }

 return DateTime.UtcNow.Add(_clockOffset);
 }

 /// <summary>
 /// Sincronizza l'offset rispetto all'orario internet ufficiale (NTP pool.ntp.org o HTTP Date header fallback).
 /// </summary>
 public static async Task<TimeSpan> SyncTimeOffsetAsync()
 {
 if (!await _syncLock.WaitAsync(100))
 {
 return _clockOffset; // Un'altra sincronizzazione è già in corso
 }

 try
 {
 if ((DateTime.UtcNow - _lastSyncUtc).TotalMinutes <= 15)
 {
 return _clockOffset;
 }
 DateTime? authoritativeUtc = null;

 // 1. Tentativo con server NTP (pool.ntp.org o time.google.com)
 try
 {
 authoritativeUtc = await QueryNtpTimeAsync("pool.ntp.org");
 }
 catch
 {
 try
 {
 authoritativeUtc = await QueryNtpTimeAsync("time.google.com");
 }
 catch
 {
 // UDP 123 potrebbe essere bloccato dal firewall aziendale
 }
 }

 // 2. Fallback affidabile via HTTP HEAD (porta 443, mai bloccata)
 if (!authoritativeUtc.HasValue)
 {
 try
 {
 authoritativeUtc = await QueryHttpTimeAsync("https://www.google.com");
 }
 catch
 {
 try
 {
 authoritativeUtc = await QueryHttpTimeAsync("https://www.cloudflare.com");
 }
 catch
 {
 }
 }
 }

 if (authoritativeUtc.HasValue)
 {
 _clockOffset = authoritativeUtc.Value - DateTime.UtcNow;
 _lastSyncUtc = DateTime.UtcNow;
 _hasSuccessfulSync = true;
 Console.WriteLine($"[NetworkTimeHelper] Orario UTC sincronizzato con successo. Offset locale: {_clockOffset.TotalSeconds:F2}s");
 }
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[NetworkTimeHelper] Avviso sincronizzazione orario: {ex.Message}");
 }
 finally
 {
 _syncLock.Release();
 }

 return _clockOffset;
 }

 private static async Task<DateTime> QueryNtpTimeAsync(string ntpServer)
 {
 return await Task.Run(() =>
 {
 var ntpData = new byte[48];
 ntpData[0] = 0x1B; // LeapIndicator = 0, VersionNum = 3, Mode = 3 (Client)

 var addresses = Dns.GetHostAddresses(ntpServer);
 var ipEndPoint = new IPEndPoint(addresses[0], 123);

 using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
 {
 socket.ReceiveTimeout = 2500;
 socket.SendTimeout = 2500;
 socket.Connect(ipEndPoint);
 socket.Send(ntpData);
 socket.Receive(ntpData);
 socket.Close();
 }

 ulong intPart = ((ulong)ntpData[40] << 24) | ((ulong)ntpData[41] << 16) | ((ulong)ntpData[42] << 8) | ((ulong)ntpData[43]);
 ulong fractPart = ((ulong)ntpData[44] << 24) | ((ulong)ntpData[45] << 16) | ((ulong)ntpData[46] << 8) | ((ulong)ntpData[47]);

 var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
 var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)milliseconds);

 return networkDateTime;
 });
 }

 private static async Task<DateTime> QueryHttpTimeAsync(string url)
 {
 using var handler = new HttpClientHandler { AllowAutoRedirect = false };
 using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
 using var req = new HttpRequestMessage(HttpMethod.Head, url);
 var resp = await client.SendAsync(req);

 if (resp.Headers.Date.HasValue)
 {
 return resp.Headers.Date.Value.UtcDateTime;
 }

 throw new InvalidOperationException("Header Date non presente nella risposta HTTP.");
 }
 }
}
