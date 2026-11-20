using System;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.Models
{
 public enum BorrowRequestStatus
 {
 Pending,
 Granted,
 Denied,
 Completed,
 Expired
 }

 public class BorrowRequest
 {
 [JsonPropertyName("requestId")]
 public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

 [JsonPropertyName("projectId")]
 public string ProjectId { get; set; } = string.Empty;

 [JsonPropertyName("elementId")]
 public long ElementId { get; set; }

 [JsonPropertyName("elementName")]
 public string ElementName { get; set; } = string.Empty;

 [JsonPropertyName("requesterUser")]
 public string RequesterUser { get; set; } = string.Empty;

 [JsonPropertyName("requesterMachine")]
 public string RequesterMachine { get; set; } = string.Empty;

 [JsonPropertyName("ownerUser")]
 public string OwnerUser { get; set; } = string.Empty;

 [JsonPropertyName("status")]
 public BorrowRequestStatus Status { get; set; } = BorrowRequestStatus.Pending;

 [JsonPropertyName("createdAtUtc")]
 public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

 [JsonIgnore]
 public DateTime CreatedAtLocal => CreatedAtUtc.Kind == DateTimeKind.Utc
 ? CreatedAtUtc.ToLocalTime()
 : DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc).ToLocalTime();

 [JsonPropertyName("respondedAtUtc")]
 public DateTime? RespondedAtUtc { get; set; }

 [JsonPropertyName("message")]
 public string Message { get; set; } = string.Empty;
 }
}
