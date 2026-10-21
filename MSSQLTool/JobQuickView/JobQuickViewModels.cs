using System;
using System.Collections.Generic;
using System.Globalization;

namespace MSSQLTool.JobQuickView
{
    internal sealed class JobQuickViewSnapshot
    {
        public Guid JobId { get; internal set; }
        public string Name { get; internal set; }
        public string Owner { get; internal set; }
        public string Category { get; internal set; }
        public string Description { get; internal set; }
        public DateTime? CreatedAt { get; internal set; }
        public DateTime? ModifiedAt { get; internal set; }
        public bool IsEnabled { get; internal set; }
        public int StartStepId { get; internal set; }
        public bool? IsRunning { get; internal set; }
        public string ExecutionStatus { get; internal set; }
        public DateTime? RunningSince { get; internal set; }
        public DateTime? NextRun { get; internal set; }
        public DateTime? LastRunStarted { get; internal set; }
        public TimeSpan? LastRunDuration { get; internal set; }
        public string LastRunOutcome { get; internal set; }
        public string LastRunMessage { get; internal set; }
        public string ActivityWarning { get; internal set; }
        public bool SchedulesLoaded { get; internal set; } = true;
        public bool HistoryLoaded { get; internal set; } = true;
        public IReadOnlyList<JobQuickViewStep> Steps { get; internal set; } = new JobQuickViewStep[0];
        public IReadOnlyList<JobQuickViewSchedule> Schedules { get; internal set; } = new JobQuickViewSchedule[0];
        public IReadOnlyList<JobQuickViewHistoryRun> History { get; internal set; } = new JobQuickViewHistoryRun[0];
        public string HistoryNote { get; internal set; } = LocalizationManager.T("Latest 100 retained executions. Select a run to load its steps and messages. Times use the SQL Server's local time.");

        public const string NextRunNote = "Times use the SQL Server's local time. Next run is an estimate; SQL Server Agent's schedule cache can lag by up to 20 minutes.";
    }

    internal sealed class JobQuickViewStep
    {
        public int StepId { get; internal set; }
        public Guid StepUid { get; internal set; }
        public string Name { get; internal set; }
        public string Subsystem { get; internal set; }
        public string DatabaseName { get; internal set; }
        public string DatabaseUserName { get; internal set; }
        public int ProxyId { get; internal set; }
        public string Command { get; internal set; }
        public string SuccessAction { get; internal set; }
        public string FailureAction { get; internal set; }
        public bool IsSql => string.Equals(Subsystem, "TSQL", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class JobQuickViewSchedule
    {
        public int ScheduleId { get; internal set; }
        public string Name { get; internal set; }
        public string Description { get; internal set; }
        public bool IsEnabled { get; internal set; }
        public DateTime? NextRun { get; internal set; }
    }

    internal class JobQuickViewHistoryItem
    {
        public int InstanceId { get; internal set; }
        public int StepId { get; internal set; }
        public string StepName { get; internal set; }
        public string Server { get; internal set; }
        public int RunStatus { get; internal set; }
        public DateTime? StartedAt { get; internal set; }
        public TimeSpan? Duration { get; internal set; }
        public string Outcome { get; internal set; }
        public string Message { get; internal set; }
        public int SqlSeverity { get; internal set; }
        public int SqlMessageId { get; internal set; }
    }

    internal sealed class JobQuickViewHistoryRun : JobQuickViewHistoryItem
    {
        public IReadOnlyList<JobQuickViewHistoryItem> Steps { get; internal set; } = new JobQuickViewHistoryItem[0];
        public bool IsPartial { get; internal set; }
        public string Note { get; internal set; }
    }

    internal static class JobQuickViewHistory
    {
        public const int MaximumRuns = 100;

        // Input belongs to one job. Final step_id=0 rows close runs independently per server.
        // The extra preceding summary is a boundary, not an additional run displayed in the UI.
        internal static IReadOnlyList<JobQuickViewHistoryRun> Group(
            IEnumerable<JobQuickViewHistoryItem> rows, int maximumRuns = MaximumRuns)
        {
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));
            if (maximumRuns < 1 || maximumRuns > MaximumRuns)
                throw new ArgumentOutOfRangeException(nameof(maximumRuns));

            var ordered = new List<JobQuickViewHistoryItem>(rows);
            ordered.Sort((left, right) =>
            {
                int cmp = string.Compare(left.Server, right.Server, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return left.InstanceId.CompareTo(right.InstanceId);
            });

            var servers = new Dictionary<string, ServerHistoryState>(StringComparer.OrdinalIgnoreCase);
            var runs = new List<JobQuickViewHistoryRun>();

            foreach (var item in ordered)
            {
                string serverKey = item.Server ?? string.Empty;
                if (!servers.TryGetValue(serverKey, out var state))
                {
                    state = new ServerHistoryState();
                    servers[serverKey] = state;
                }

                var steps = state.Steps;
                if (item.StepId != 0)
                {
                    steps.Add(item);
                    continue;
                }

                var notes = new List<string>();
                if (!state.HasPreviousSummary)
                    notes.Add(LocalizationManager.T("Earliest retained run. Step messages recorded before this retention boundary are unavailable."));

                var matchedSteps = new List<JobQuickViewHistoryItem>();
                bool omittedSteps = false;
                bool unknownTimestamps = false;
                DateTime? runEnd = item.StartedAt.HasValue && item.Duration.HasValue
                    ? (DateTime?)(item.StartedAt.Value + item.Duration.Value)
                    : null;

                foreach (var step in steps)
                {
                    if (item.StartedAt.HasValue && step.StartedAt.HasValue)
                    {
                        if (step.StartedAt.Value < item.StartedAt.Value)
                        {
                            omittedSteps = true;
                            continue;
                        }

                        if (runEnd.HasValue && step.Duration.HasValue
                            && step.StartedAt.Value + step.Duration.Value > runEnd.Value.AddSeconds(1))
                        {
                            omittedSteps = true;
                            continue;
                        }
                    }
                    else if (!item.StartedAt.HasValue || !step.StartedAt.HasValue)
                    {
                        unknownTimestamps = true;
                    }

                    matchedSteps.Add(step);
                }

                if (omittedSteps)
                    notes.Add(LocalizationManager.T("Some retained steps fall outside this run's recorded time range and were omitted because their run could not be identified reliably."));
                if (unknownTimestamps)
                    notes.Add(LocalizationManager.T("Some timestamps are unavailable. Step-to-run matches use retained record order and could not be fully verified."));
                if (matchedSteps.Count == 0)
                    notes.Add(LocalizationManager.T("No matching retained step messages are available for this run."));

                var run = new JobQuickViewHistoryRun
                {
                    InstanceId = item.InstanceId,
                    StepId = item.StepId,
                    StepName = item.StepName,
                    Server = item.Server,
                    RunStatus = item.RunStatus,
                    StartedAt = item.StartedAt,
                    Duration = item.Duration,
                    Outcome = item.Outcome,
                    Message = item.Message,
                    SqlSeverity = item.SqlSeverity,
                    SqlMessageId = item.SqlMessageId,
                    Steps = matchedSteps.ToArray(),
                    IsPartial = !state.HasPreviousSummary || omittedSteps || unknownTimestamps,
                    Note = string.Join(" ", notes)
                };
                runs.Add(run);
                steps.Clear();
                state.HasPreviousSummary = true;
            }

            if (runs.Count > maximumRuns)
                runs.RemoveRange(0, runs.Count - maximumRuns);
            runs.Reverse();
            return runs;
        }

        private sealed class ServerHistoryState
        {
            public bool HasPreviousSummary;
            public List<JobQuickViewHistoryItem> Steps { get; } = new List<JobQuickViewHistoryItem>();
        }
    }

    internal sealed class JobCommandConflictException : InvalidOperationException
    {
        public JobCommandConflictException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    internal static class JobQuickViewValue
    {
        public static DateTime? DateTimeFromAgent(int date, int time)
        {
            if (date <= 0 || time < 0)
                return null;

            DateTime parsed;
            return DateTime.TryParseExact(
                date.ToString("D8", CultureInfo.InvariantCulture) + time.ToString("D6", CultureInfo.InvariantCulture),
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? (DateTime?)DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
                : null;
        }

        public static TimeSpan? DurationFromAgent(int duration)
        {
            if (duration < 0 || duration % 100 >= 60 || duration / 100 % 100 >= 60)
                return null;
            return TimeSpan.FromSeconds((long)(duration / 10000) * 3600
                + duration / 100 % 100 * 60 + duration % 100);
        }

        public static string Outcome(int outcome)
        {
            switch (outcome)
            {
                case 0: return LocalizationManager.T("Failed");
                case 1: return LocalizationManager.T("Succeeded");
                case 2: return LocalizationManager.T("Retry");
                case 3: return LocalizationManager.T("Canceled");
                case 4: return LocalizationManager.T("In progress");
                default: return LocalizationManager.T("Unknown");
            }
        }

        public static bool? IsRunning(int status)
        {
            switch (status)
            {
                case 1:
                case 2:
                case 3:
                case 7: return true;
                case 4:
                case 5: return false;
                default: return null;
            }
        }

        public static string ExecutionStatus(int status)
        {
            switch (status)
            {
                case 1: return LocalizationManager.T("Running");
                case 2: return LocalizationManager.T("Waiting for thread");
                case 3: return LocalizationManager.T("Between retries");
                case 4: return LocalizationManager.T("Idle");
                case 5: return LocalizationManager.T("Suspended");
                case 7: return LocalizationManager.T("Completing");
                default: return LocalizationManager.T("Unknown");
            }
        }

        public static string StepAction(int action, int target)
        {
            switch (action)
            {
                case 1: return LocalizationManager.T("Quit with success");
                case 2: return LocalizationManager.T("Quit with failure");
                case 3: return LocalizationManager.T("Go to next step");
                case 4: return string.Format(LocalizationManager.T("Go to step {0}"), target.ToString(CultureInfo.InvariantCulture));
                default: return LocalizationManager.T("Unknown");
            }
        }
    }
}
