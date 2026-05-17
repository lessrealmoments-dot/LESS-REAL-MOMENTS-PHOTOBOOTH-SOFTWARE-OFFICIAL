using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

/// <summary>
/// Print limits: per event (all sessions), per session (print actions, persisted),
/// print window max (copies per single print job). Always uses full composite, never thumbnails.
/// </summary>
public static class SessionPrintService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public sealed class PrintQuota
    {
        public bool LimitsEnabled { get; init; }
        public bool CanPrint { get; init; }
        public bool EventLimitReached { get; init; }
        public bool SessionLimitReached { get; init; }
        public int SessionActionsUsed { get; init; }
        public int SessionActionsMax { get; init; }
        public int EventActionsUsed { get; init; }
        public int EventActionsMax { get; init; }
        public int CopiesThisJob { get; init; } = 1;

        public string UserMessage
        {
            get
            {
                if (!LimitsEnabled) return "";
                if (EventLimitReached)
                    return $"This event has reached its print limit ({EventActionsMax} print actions).";
                if (SessionLimitReached)
                    return $"This session has used all {SessionActionsMax} allowed print(s).";
                return "";
            }
        }
    }

    public static PrintQuota Evaluate(string eventId, string eventName, string sessionFolderAbs)
    {
        var behavior = GlobalSettingsService.Load().PrintBehavior;
        var eventUsed = EventPrintCounterService.LoadTotalPrints(eventId, eventName);
        var sessionUsed = GetSessionPrintActionsUsed(sessionFolderAbs);

        if (!behavior.LimitPrints)
        {
            return new PrintQuota
            {
                LimitsEnabled = false,
                CanPrint = true,
                SessionActionsUsed = sessionUsed,
                EventActionsUsed = eventUsed,
                CopiesThisJob = 1
            };
        }

        var eventMax = behavior.MaxPrintsPerEvent;
        var sessionMax = behavior.MaxPrintsPerSession;
        var eventReached = eventUsed >= eventMax;
        var sessionReached = sessionUsed >= sessionMax;

        return new PrintQuota
        {
            LimitsEnabled = true,
            CanPrint = !eventReached && !sessionReached,
            EventLimitReached = eventReached,
            SessionLimitReached = sessionReached,
            SessionActionsUsed = sessionUsed,
            SessionActionsMax = sessionMax,
            EventActionsUsed = eventUsed,
            EventActionsMax = eventMax,
            CopiesThisJob = 1
        };
    }

    public static int ResolveCopiesForJob(int printerSlot)
    {
        var global = GlobalSettingsService.Load();
        var slotSettings = printerSlot == 2 ? global.Printer2 : global.Printer1;
        var windowMax = Math.Clamp(global.PrintBehavior.PrintDialogMaxCopies, 1, 99);
        var slotCopies = Math.Clamp(slotSettings.Copies, 1, 99);
        return Math.Min(windowMax, slotCopies);
    }

    /// <summary>One print action: full-resolution composite, respects all limits, persists counts.</summary>
    public static bool TryPrintSession(string eventId, string eventName, string sessionFolderAbs, out string message)
    {
        message = "";

        if (!SessionPrintResolver.TryResolveFullQualityPrint(sessionFolderAbs, out var printPath, out var layoutId,
                out var resolveError))
        {
            message = resolveError ?? "Could not find the print file.";
            return false;
        }

        var quota = Evaluate(eventId, eventName, sessionFolderAbs);
        if (quota.LimitsEnabled && !quota.CanPrint)
        {
            message = quota.UserMessage;
            return false;
        }

        var slot = TemplatePrintRouting.ResolveSlotForLayout(layoutId ?? "");
        var copies = ResolveCopiesForJob(slot);

        var (ok, msg) = BoothPrintService.TryPrintForLayout(printPath!, layoutId ?? "", copies);
        if (!ok)
        {
            message = msg;
            return false;
        }

        RecordPrintAction(sessionFolderAbs, eventId, eventName);
        message = copies == 1
            ? msg
            : $"{msg} ({copies} copies this print.)";
        return true;
    }

    public static int GetSessionPrintActionsUsed(string sessionFolderAbs)
    {
        try
        {
            var path = Path.Combine(sessionFolderAbs, "session.json");
            if (!File.Exists(path)) return 0;
            var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), JsonOpts);
            return Math.Max(0, manifest?.PrintActionsUsed ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    public static void RecordPrintAction(string sessionFolderAbs, string eventId, string eventName)
    {
        var path = Path.Combine(sessionFolderAbs, "session.json");
        SessionManifest manifest;
        try
        {
            if (File.Exists(path))
                manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), JsonOpts)
                           ?? CreateEmptyManifest(sessionFolderAbs);
            else
                manifest = CreateEmptyManifest(sessionFolderAbs);
        }
        catch
        {
            manifest = CreateEmptyManifest(sessionFolderAbs);
        }

        manifest.PrintActionsUsed = Math.Max(0, manifest.PrintActionsUsed) + 1;
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOpts));

        var eventTotal = EventPrintCounterService.LoadTotalPrints(eventId, eventName) + 1;
        EventPrintCounterService.SaveTotalPrints(eventId, eventName, eventTotal);
    }

    private static SessionManifest CreateEmptyManifest(string sessionFolderAbs) =>
        new()
        {
            SessionId = Path.GetFileName(sessionFolderAbs),
            EventId = "",
            EventName = "",
            LayoutId = "",
            CreatedUtc = DateTime.UtcNow,
            Captures = [],
            PrintActionsUsed = 0
        };
}
