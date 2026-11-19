using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Registra LockManager come Singleton
builder.Services.AddSingleton<LockManager>();

var app = builder.Build();

app.UseWebSockets();

var lockManager = app.Services.GetRequiredService<LockManager>();
var connectedSockets = new List<WebSocket>();
var socketsLock = new object();

// Gestione notifiche WebSocket
lockManager.OnLockStateChanged += (projectId, lockInfo) =>
{
 var msg = new
 {
 type = "LOCK_STATE_CHANGED",
 projectId,
 isLocked = lockInfo != null,
 user = lockInfo?.User,
 machine = lockInfo?.Machine,
 expiresAtUtc = lockInfo?.ExpiresAtUtc
 };
 BroadcastJson(msg);
};

lockManager.OnProjectUpdated += (project) =>
{
 var msg = new
 {
 type = "PROJECT_UPDATED",
 project
 };
 BroadcastJson(msg);
};

void BroadcastJson(object obj)
{
 var json = JsonSerializer.Serialize(obj);
 var bytes = Encoding.UTF8.GetBytes(json);
 var segment = new ArraySegment<byte>(bytes);

 lock (socketsLock)
 {
 for (int i = connectedSockets.Count - 1; i >= 0; i--)
 {
 var ws = connectedSockets[i];
 if (ws.State == WebSocketState.Open)
 {
 _ = ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
 }
 else
 {
 connectedSockets.RemoveAt(i);
 }
 }
 }
}

// WebSocket endpoint per notifiche live ai plugin Revit
app.Map("/ws", async (HttpContext context) =>
{
 if (context.WebSockets.IsWebSocketRequest)
 {
 using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
 lock (socketsLock)
 {
 connectedSockets.Add(webSocket);
 }

 var buffer = new byte[1024 * 4];
 while (webSocket.State == WebSocketState.Open)
 {
 var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
 if (result.MessageType == WebSocketMessageType.Close)
 {
 break;
 }
 }

 lock (socketsLock)
 {
 connectedSockets.Remove(webSocket);
 }
 }
 else
 {
 context.Response.StatusCode = StatusCodes.Status400BadRequest;
 }
});

// REST API Endpoints
app.MapGet("/", () => Results.Ok(new
{
 service = "RevitDriveConnector Coordinator",
 status = "Online",
 version = "1.0.0",
 utcTime = DateTime.UtcNow
}));

app.MapPost("/api/lock/acquire", (LockRequest req) =>
{
 if (string.IsNullOrWhiteSpace(req.ProjectId))
 return Results.BadRequest("ProjectId obbligatorio");

 var response = lockManager.AcquireLock(req);
 return Results.Ok(response);
});

app.MapPost("/api/lock/release", (ReleaseLockDto req) =>
{
 bool released = lockManager.ReleaseLock(req.ProjectId, req.LockToken, req.User);
 return Results.Ok(new { success = released, projectId = req.ProjectId });
});

app.MapGet("/api/lock/{projectId}", (string projectId) =>
{
 var current = lockManager.GetCurrentLock(projectId);
 if (current == null) return Results.Ok(new { isLocked = false });
 return Results.Ok(new
 {
 isLocked = true,
 user = current.User,
 machine = current.Machine,
 expiresAtUtc = current.ExpiresAtUtc
 });
});

app.MapGet("/api/projects", () =>
{
 return Results.Ok(lockManager.GetAllProjects());
});

app.MapPost("/api/projects/register", (CloudProjectItem project) =>
{
 lockManager.RegisterOrUpdateProject(project);
 return Results.Ok(new { success = true, project });
});

app.Run();

public record ReleaseLockDto(string ProjectId, string LockToken, string User);
