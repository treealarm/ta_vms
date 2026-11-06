using System;

namespace VmsMap.Db
{
  // The operator's alarm entity. See the `alarms` table in Grpc/migrations/0000_init.sql: the state
  // says the condition is here, this row says who has to deal with it and whether they did.
  public record DBAlarm
  {
    public Guid id { get; set; }
    public Guid object_id { get; set; }
    public Guid? definition_id { get; set; }
    public Guid? subscription_id { get; set; }
    public string source { get; set; } = null!;
    public string dedup_key { get; set; } = null!;
    public bool condition_active { get; set; }
    public string status { get; set; } = null!;
    public string severity { get; set; } = null!;
    public DateTime raised_at { get; set; }
    public DateTime? cleared_at { get; set; }
    public DateTime? expected_end_ts { get; set; }
    public DateTime? acknowledged_at { get; set; }
    public string? acknowledged_by { get; set; }
    public string? ack_comment { get; set; }
    public DateTime? closed_at { get; set; }
    public string? close_reason { get; set; }
    public string? close_comment { get; set; }
    public string? closed_by { get; set; }
    public Guid? source_event_id { get; set; }
  }

  /// <summary>The status values the table accepts, named once so no caller spells them itself.</summary>
  public static class AlarmStatus
  {
    public const string Active = "active";
    public const string Acknowledged = "acknowledged";
    public const string Returned = "returned";
    public const string Closed = "closed";
  }

  public static class AlarmSource
  {
    public const string Sensor = "sensor";
    public const string Retention = "retention";
    public const string Subscription = "subscription";
  }

  public static class AlarmSeverity
  {
    public const string Warning = "warning";
    public const string Critical = "critical";
  }

  /// <summary>Why an alarm was closed. The table constrains the same set.</summary>
  public static class AlarmCloseReason
  {
    public const string ReturnedToNormal = "returned_to_normal";
    public const string AcknowledgedAfterNormal = "acknowledged_after_normal";
    public const string AutoReturnedToNormal = "auto_returned_to_normal";
    public const string DeviceGone = "device_gone";
    public const string FalseAlarm = "false_alarm";
    public const string Other = "other";
  }

  /// <summary>The acknowledgement policies a definition may carry.</summary>
  public static class AlarmAckPolicy
  {
    public const string Required = "required";
    public const string AutoOnNormal = "auto_on_normal";
  }
}
