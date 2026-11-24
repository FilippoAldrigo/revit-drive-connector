using System;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.IPC
{
    public enum IpcCommand
    {
        Ping,
        GetDaemonStatus,
        ListCloudProjects,
        DownloadAndPrepareProject,
        AcquireSyncLock,
        HeartbeatSyncLock,
        ReleaseSyncLock,
        UploadProjectChanges,
        UploadProjectLinks,
        NotifyDocumentOpened,
        NotifyDocumentClosed,
        CreateCloudProject,
        SendBorrowRequest,
        GetPendingBorrowRequests,
        RespondToBorrowRequest,
        GetOutgoingBorrowRequests,
        MarkBorrowRequestCompleted,
        VerifySyncIntegrity,
        ForceBreakLock,
        GetGoogleAccountStatus,
        ConnectGoogleAccount,
        DisconnectGoogleAccount,
        SaveGoogleCredentials,
        MountCloudProject,
        AddCollaborator,
        ListCollaborators,
        RemoveCollaborator,
        PublishLiveEdits,
        GetRemoteLiveEdits,
        PollConnectorState,
        PreflightCheck,
        GetVersionHistory,
        RollbackProjectVersion,
        CheckCloudLinkUpdates,
        DownloadCloudLink,
        RunDiskCleanup,
        ExportDiagnostics
    }

    public class IpcRequest
    {
        [JsonPropertyName("command")]
        public IpcCommand Command { get; set; }

        [JsonPropertyName("payloadJson")]
        public string PayloadJson { get; set; } = string.Empty;

        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
    }

    public class IpcResponse
    {
        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("dataJson")]
        public string? DataJson { get; set; }
    }
}
