namespace VmsSingleton.EventRouting;

/// <summary>What the message handler should tell the bus.</summary>
public enum RouteResult
{
  /// <summary>Done, or nothing to do: acknowledge. A failed run is acknowledged too — its failure
  /// is final and visible in the registry, and a queue that keeps redelivering it helps nobody.</summary>
  Ack,

  /// <summary>An action failed and the run has attempts left: let the bus deliver it again.</summary>
  Retry,
}

/// <summary>
/// One journal event through the configured subscriptions.
/// </summary>
/// <remarks>
/// The flow is: read the event from the journal (never from the bus payload), select the
/// subscriptions, refuse a loop or a too-deep chain, mind the cooldown, then run the actions in
/// order, keeping the run's position in the state store after each one.
/// <para>
/// Delivery is at-least-once and the run record is what makes the second delivery benign: a
/// completed run is a no-op, a failed one is a no-op, and a running one continues from the first
/// action that did not finish. The run keeps the snapshot of the subscription it started with, so
/// an edit in the middle changes the next run, not this one.
/// </para>
/// </remarks>
public sealed class EventRouter(
  ISubscriptionSource subscriptions,
  IMapGateway map,
  ActionExecutor actions,
  IRunStore runs,
  IRoutingDiagnostics diagnostics,
  ILogger<EventRouter> logger)
{
  public async Task<RouteResult> HandleAsync(Guid eventId, CancellationToken ct)
  {
    var trigger = await map.GetEventAsync(eventId, ct);
    if (trigger is null)
    {
      // The camera was deleted with its history between the journal write and this delivery.
      logger.LogDebug("Event {EventId} is no longer in the journal; nothing to route", eventId);
      return RouteResult.Ack;
    }

    var configured = await subscriptions.ListAsync(ct);
    // One line per event at Debug: the question "why did nothing fire" is answered here first.
    logger.LogDebug(
      "Event {EventId} ({EventName}): {Count} enabled subscription(s)",
      trigger.Id, trigger.EventName, configured.Count);
    if (configured.Count == 0)
      return RouteResult.Ack;

    var chain = CausationChain.FromEvent(trigger);
    bool? armed = null;

    foreach (var subscription in configured)
    {
      if (!SubscriptionMatcher.Selects(subscription, trigger))
        continue;

      if (subscription.ArmedCondition != SubscriptionMatcher.ArmedConditionAny)
      {
        armed ??= await map.GetArmedAsync(trigger.ObjectId, ct);
        if (!SubscriptionMatcher.ArmedConditionMatches(subscription, armed))
          continue;
      }

      if (!chain.CanFire(subscription.Id))
      {
        Suppress(subscription, trigger, chain);
        continue;
      }

      if (await RunAsync(subscription, trigger, chain, ct) == RouteResult.Retry)
        return RouteResult.Retry;
    }

    return RouteResult.Ack;
  }

  private async Task<RouteResult> RunAsync(
    SubscriptionSnapshot subscription, EventFacts trigger, CausationChain chain, CancellationToken ct)
  {
    var run = await runs.GetAsync(subscription.Id, trigger.Id, ct);
    if (run is { Status: EventRun.Completed or EventRun.Failed })
      return RouteResult.Ack;

    if (run is null)
    {
      // The existing run is answered first, and only a *new* run consults the cooldown: a retry
      // must resume the run it left off, and a cooldown it took itself must not look like a storm
      // and swallow that retry. The record is written before the cooldown for the same reason —
      // if the process dies between them, the repeat finds the run and continues.
      var cooldown = TimeSpan.FromMilliseconds(subscription.CooldownMs);
      if (await runs.IsCoolingDownAsync(subscription.Id, trigger.ObjectId, cooldown, ct))
      {
        logger.LogDebug(
          "Subscription '{SubscriptionId}' is cooling down for {ObjectId}; event {EventId} is not routed",
          subscription.Id, trigger.ObjectId, trigger.Id);
        return RouteResult.Ack;
      }

      run = new EventRun
      {
        SubscriptionId = subscription.Id,
        TriggerEventId = trigger.Id,
        Version = subscription.Version,
        Snapshot = subscription,
        NextPosition = 0,
        Status = EventRun.Running,
        Attempts = 0,
        UpdatedAt = DateTime.UtcNow,
      };
      await runs.SaveAsync(run, ct);
      await runs.StartCooldownAsync(subscription.Id, trigger.ObjectId, cooldown, ct);
    }

    var snapshot = run.Snapshot ?? subscription;
    var position = run.NextPosition;
    run.Attempts += 1;

    try
    {
      while (position < snapshot.Actions.Count)
      {
        await actions.ExecuteAsync(snapshot, trigger, chain, snapshot.Actions[position], ct);
        position += 1;

        // Written after every action, so a crash between two of them resumes at the next one.
        run.NextPosition = position;
        run.UpdatedAt = DateTime.UtcNow;
        await runs.SaveAsync(run, ct);
      }

      run.Status = EventRun.Completed;
      run.UpdatedAt = DateTime.UtcNow;
      await runs.SaveAsync(run, ct);
      return RouteResult.Ack;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      run.Error = ex.Message;
      run.UpdatedAt = DateTime.UtcNow;

      if (run.Attempts >= Routing.MaxAttempts)
      {
        run.Status = EventRun.Failed;
        await runs.SaveAsync(run, ct);
        logger.LogError(ex,
          "Subscription '{SubscriptionId}' for event {EventId} failed after {Attempts} attempts; giving up",
          subscription.Id, trigger.Id, run.Attempts);
        diagnostics.Report(Routing.RunEntityType, EventRun.Key(run.SubscriptionId, run.TriggerEventId), new
        {
          subscription_id = run.SubscriptionId,
          trigger_event_id = run.TriggerEventId,
          version = run.Version,
          next_position = run.NextPosition,
          attempts = run.Attempts,
          status = run.Status,
          error = run.Error,
        });
        return RouteResult.Ack;
      }

      // The failure itself is recorded and the run is retried; the state is saved before the
      // handler returns so the retry continues from the same position.
      await runs.SaveAsync(run, ct);
      logger.LogWarning(ex,
        "Subscription '{SubscriptionId}' for event {EventId} failed at action {Position} (attempt {Attempts} of {Max})",
        subscription.Id, trigger.Id, run.NextPosition, run.Attempts, Routing.MaxAttempts);
      return RouteResult.Retry;
    }
  }

  private void Suppress(SubscriptionSnapshot subscription, EventFacts trigger, CausationChain chain)
  {
    var reason = chain.Depth > Routing.MaxDepth
      ? $"chain depth {chain.Depth} exceeds {Routing.MaxDepth}"
      : $"subscription already fired in this chain";

    logger.LogWarning(
      "Subscription '{SubscriptionId}' suppressed for event {EventId}: {Reason}",
      subscription.Id, trigger.Id, reason);

    diagnostics.Report(
      Routing.SuppressedEntityType,
      EventRun.Key(subscription.Id, trigger.Id),
      new
      {
        subscription_id = subscription.Id,
        trigger_event_id = trigger.Id,
        depth = chain.Depth,
        chain = chain.Serialize(),
        reason,
      });
  }
}
