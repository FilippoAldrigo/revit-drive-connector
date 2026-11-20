using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Server.Services
{
 public class LockInfo
 {
 public string ProjectId { get; set; } = string.Empty;
 public string User { get; set; } = string.Empty;
 public string Machine { get; set; } = string.Empty;
 public string LockToken { get; set; } = Guid.NewGuid().ToString("N");
 public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;
 public DateTime ExpiresAtUtc { get; set; }
 }

 public class LockManager
 {
 private readonly ConcurrentDictionary<string, LockInfo> _activeLocks = new(StringComparer.OrdinalIgnoreCase);
 private readonly ConcurrentDictionary<string, CloudProjectItem> _projects = new(StringComparer.OrdinalIgnoreCase);

 public event Action<string, LockInfo?>? OnLockStateChanged;
 public event Action<CloudProjectItem>? OnProjectUpdated;

 public LockResponse AcquireLock(LockRequest request)
 {
 var now = DateTime.UtcNow;

 lock (_activeLocks)
 {
 if (_activeLocks.TryGetValue(request.ProjectId, out var current))
 {
 // Controlla se il lock precedente è scaduto
 if (current.ExpiresAtUtc > now)
 {
 // Lock ancora valido e occupato da un altro
 if (current.User.Equals(request.User, StringComparison.OrdinalIgnoreCase) &&
 current.Machine.Equals(request.Machine, StringComparison.OrdinalIgnoreCase))
 {
 // Stesso utente e macchina: estendi lease
 current.ExpiresAtUtc = now.AddSeconds(request.TimeoutSeconds);
 return new LockResponse
 {
 Status = SyncLockStatus.LockAcquired,
 LockToken = current.LockToken,
 CurrentHolder = current.User,
 CurrentHolderMachine = current.Machine,
 AcquiredAtUtc = current.AcquiredAtUtc,
 ExpiresAtUtc = current.ExpiresAtUtc,
 Message = "Lock prolungato con successo."
 };
 }

 return new LockResponse
 {
 Status = SyncLockStatus.LockDeniedBusy,
 CurrentHolder = current.User,
 CurrentHolderMachine = current.Machine,
 AcquiredAtUtc = current.AcquiredAtUtc,
 ExpiresAtUtc = current.ExpiresAtUtc,
 Message = $"Progetto attualmente bloccato da {current.User} ({current.Machine})."
 };
 }
 else
 {
 // Lock scaduto, lo rimuoviamo
 _activeLocks.TryRemove(request.ProjectId, out _);
 }
 }

 // Crea nuovo lock
 var newLock = new LockInfo
 {
 ProjectId = request.ProjectId,
 User = request.User,
 Machine = request.Machine,
 AcquiredAtUtc = now,
 ExpiresAtUtc = now.AddSeconds(request.TimeoutSeconds)
 };

 _activeLocks[request.ProjectId] = newLock;
 OnLockStateChanged?.Invoke(request.ProjectId, newLock);

 return new LockResponse
 {
 Status = SyncLockStatus.LockAcquired,
 LockToken = newLock.LockToken,
 CurrentHolder = newLock.User,
 CurrentHolderMachine = newLock.Machine,
 AcquiredAtUtc = newLock.AcquiredAtUtc,
 ExpiresAtUtc = newLock.ExpiresAtUtc,
 Message = "Lock di sincronizzazione acquisito."
 };
 }
 }

 public bool ReleaseLock(string projectId, string lockToken, string user)
 {
 lock (_activeLocks)
 {
 if (_activeLocks.TryGetValue(projectId, out var current))
 {
 bool matchesToken = !string.IsNullOrWhiteSpace(lockToken) && current.LockToken == lockToken;
 bool matchesUserNoToken = string.IsNullOrWhiteSpace(lockToken) && current.User.Equals(user, StringComparison.OrdinalIgnoreCase);
 if (matchesToken || matchesUserNoToken)
 {
 _activeLocks.TryRemove(projectId, out _);
 OnLockStateChanged?.Invoke(projectId, null);
 return true;
 }
 }
 return false;
 }
 }

 public void RegisterOrUpdateProject(CloudProjectItem project)
 {
 _projects[project.Id] = project;
 OnProjectUpdated?.Invoke(project);
 }

 public List<CloudProjectItem> GetAllProjects()
 {
 return _projects.Values.ToList();
 }

 public CloudProjectItem? GetProject(string id)
 {
 _projects.TryGetValue(id, out var p);
 return p;
 }

 public LockInfo? GetCurrentLock(string projectId)
 {
 if (_activeLocks.TryGetValue(projectId, out var lockInfo))
 {
 if (lockInfo.ExpiresAtUtc > DateTime.UtcNow) return lockInfo;
 _activeLocks.TryRemove(projectId, out _);
 }
 return null;
 }
 }
}
