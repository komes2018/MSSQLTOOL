using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace MSSQLTool.JobQuickView
{
    /// <summary>
    /// Uses the selected SSMS login. SQL Server Agent enforces authorization for every action.
    /// Each operation owns a fresh connection; nothing runs on the editor's query connection.
    /// </summary>
    internal sealed class JobQuickViewService
    {
        private const int CommandTimeoutSeconds = 15;
        private readonly Func<SqlConnection> _connectionFactory;

        public JobQuickViewService(Func<SqlConnection> connectionFactory)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        public JobQuickViewService(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "msdb" };
            string msdbConnectionString = builder.ConnectionString;
            _connectionFactory = () => new SqlConnection(msdbConnectionString);
        }

        public Task<JobQuickViewSnapshot> LoadAsync(string jobName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(jobName))
                throw new ArgumentException(LocalizationManager.T("A job name is required."), nameof(jobName));
            return LoadCoreAsync(null, jobName, cancellationToken);
        }

        public Task<JobQuickViewSnapshot> LoadAsync(Guid jobId, CancellationToken cancellationToken)
        {
            ValidateJobId(jobId);
            return LoadCoreAsync(jobId, null, cancellationToken);
        }

        private async Task<JobQuickViewSnapshot> LoadCoreAsync(Guid? jobId, string jobName, CancellationToken token)
        {
            using (var connection = await OpenAsync(token).ConfigureAwait(false))
            {
                var snapshot = new JobQuickViewSnapshot();
                using (var command = Procedure(connection, "dbo.sp_help_job"))
                {
                    if (jobId.HasValue)
                        command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId.Value;
                    else
                        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = jobName;
                    command.Parameters.Add("@job_aspect", SqlDbType.VarChar, 9).Value = "JOB";
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false))
                            throw new InvalidOperationException(LocalizationManager.T("The job no longer exists or the current login cannot view it."));
                        snapshot.JobId = (Guid)reader["job_id"];
                        snapshot.Name = Text(reader, "name");
                        snapshot.Owner = Text(reader, "owner");
                        snapshot.Category = Text(reader, "category");
                        snapshot.Description = Text(reader, "description");
                        snapshot.CreatedAt = ServerDateTime(reader, "date_created");
                        snapshot.ModifiedAt = ServerDateTime(reader, "date_modified");
                        snapshot.IsEnabled = Number(reader, "enabled") != 0;
                        snapshot.StartStepId = Number(reader, "start_step_id");
                        int status = Number(reader, "current_execution_status");
                        snapshot.IsRunning = JobQuickViewValue.IsRunning(status);
                        snapshot.ExecutionStatus = JobQuickViewValue.ExecutionStatus(status);
                        snapshot.LastRunStarted = JobQuickViewValue.DateTimeFromAgent(Number(reader, "last_run_date"), Number(reader, "last_run_time"));
                        snapshot.LastRunOutcome = snapshot.LastRunStarted.HasValue
                            ? JobQuickViewValue.Outcome(Number(reader, "last_run_outcome")) : LocalizationManager.T("No history");
                        snapshot.LastRunMessage = string.Empty;
                        snapshot.NextRun = snapshot.IsEnabled
                            ? JobQuickViewValue.DateTimeFromAgent(Number(reader, "next_run_date"), Number(reader, "next_run_time")) : null;
                    }
                }

                var warnings = new List<string>();
                snapshot.Steps = await ReadStepsAsync(connection, snapshot.JobId, token).ConfigureAwait(false);
                try
                {
                    snapshot.Schedules = await ReadSchedulesAsync(connection, snapshot.JobId, token).ConfigureAwait(false);
                }
                catch (SqlException ex)
                {
                    snapshot.SchedulesLoaded = false;
                    warnings.Add(LocalizationManager.T("Schedules could not be loaded:") + " " + ex.Message);
                }

                try
                {
                    await ReadActivityDetailsAsync(connection, snapshot, token).ConfigureAwait(false);
                }
                catch (SqlException ex)
                {
                    warnings.Add(LocalizationManager.T("Current execution status could not be verified:") + " " + ex.Message);
                }

                try
                {
                    snapshot.History = await ReadHistoryAsync(connection, snapshot.JobId, token).ConfigureAwait(false);
                }
                catch (SqlException ex)
                {
                    snapshot.HistoryLoaded = false;
                    warnings.Add(LocalizationManager.T("Execution history could not be loaded:") + " " + ex.Message);
                }

                snapshot.ActivityWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
                return snapshot;
            }
        }

        public async Task<IReadOnlyList<JobQuickViewHistoryRun>> LoadHistoryAsync(Guid jobId, CancellationToken token)
        {
            ValidateJobId(jobId);
            using (var connection = await OpenAsync(token).ConfigureAwait(false))
                return await ReadHistoryAsync(connection, jobId, token).ConfigureAwait(false);
        }

        private static async Task<IReadOnlyList<JobQuickViewStep>> ReadStepsAsync(
            SqlConnection connection, Guid jobId, CancellationToken token)
        {
            var steps = new List<JobQuickViewStep>();
            using (var command = Query(connection, StepsSql, jobId))
            using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    int onFail = Number(reader, "on_fail_action");
                    int onFailStep = Number(reader, "on_fail_step_id");
                    int onSuccess = Number(reader, "on_success_action");
                    int onSuccessStep = Number(reader, "on_success_step_id");

                    steps.Add(new JobQuickViewStep
                    {
                        StepId = Number(reader, "step_id"),
                        StepUid = (Guid)reader["step_uid"],
                        Name = Text(reader, "step_name"),
                        Subsystem = Text(reader, "subsystem"),
                        DatabaseName = Text(reader, "database_name"),
                        DatabaseUserName = Text(reader, "database_user_name"),
                        ProxyId = Number(reader, "proxy_id"),
                        Command = Text(reader, "command"),
                        SuccessAction = JobQuickViewValue.StepAction(onSuccess, onSuccessStep),
                        FailureAction = JobQuickViewValue.StepAction(onFail, onFailStep)
                    });
                }
            }
            return steps;
        }

        private static async Task<IReadOnlyList<JobQuickViewSchedule>> ReadSchedulesAsync(
            SqlConnection connection, Guid jobId, CancellationToken token)
        {
            var schedules = new List<JobQuickViewSchedule>();
            using (var command = Procedure(connection, "dbo.sp_help_jobschedule", jobId))
            using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    schedules.Add(new JobQuickViewSchedule
                    {
                        ScheduleId = Number(reader, "schedule_id"),
                        Name = ReadScheduleName(reader),
                        Description = Text(reader, "schedule_description"),
                        IsEnabled = Number(reader, "enabled") != 0,
                        NextRun = JobQuickViewValue.DateTimeFromAgent(Number(reader, "next_run_date"), Number(reader, "next_run_time"))
                    });
                }
            }
            return schedules;
        }

        private static async Task ReadActivityDetailsAsync(
            SqlConnection connection, JobQuickViewSnapshot snapshot, CancellationToken token)
        {
            using (var command = Procedure(connection, "dbo.sp_help_jobactivity", snapshot.JobId))
            using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    snapshot.RunningSince = ServerDateTime(reader, "start_execution_date");
                    DateTime? stop = ServerDateTime(reader, "stop_execution_date");
                    DateTime? queued = ServerDateTime(reader, "job_history_number");
                    if (snapshot.RunningSince.HasValue && !stop.HasValue)
                        snapshot.IsRunning = true;
                    else if (stop.HasValue)
                        snapshot.IsRunning = false;
                    int lastRunSession = Number(reader, "last_executed_step_id");
                    int lastStepStatus = Number(reader, "last_execution_status");
                    if (snapshot.LastRunStarted.HasValue && lastStepStatus != 0)
                        snapshot.LastRunOutcome = JobQuickViewValue.Outcome(lastStepStatus);
                }
            }
        }

        private static async Task<IReadOnlyList<JobQuickViewHistoryRun>> ReadHistoryAsync(
            SqlConnection connection, Guid jobId, CancellationToken token)
        {
            var items = new List<JobQuickViewHistoryItem>();
            using (var command = Procedure(connection, "dbo.sp_help_jobhistory", jobId))
            {
                command.Parameters.Add("@mode", SqlDbType.VarChar, 7).Value = "FULL";
                using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        items.Add(new JobQuickViewHistoryItem
                        {
                            InstanceId = Number(reader, "instance_id"),
                            StepId = Number(reader, "step_id"),
                            StepName = Text(reader, "step_name"),
                            Server = Text(reader, "server"),
                            RunStatus = Number(reader, "run_status"),
                            StartedAt = JobQuickViewValue.DateTimeFromAgent(Number(reader, "run_date"), Number(reader, "run_time")),
                            Duration = JobQuickViewValue.DurationFromAgent(Number(reader, "run_duration")),
                            Outcome = JobQuickViewValue.Outcome(Number(reader, "run_status")),
                            Message = Text(reader, "message"),
                            SqlSeverity = Number(reader, "sql_severity"),
                            SqlMessageId = Number(reader, "sql_message_id")
                        });
                    }
                }
            }
            return JobQuickViewHistory.Group(items);
        }

        public async Task StartJobAsync(Guid jobId, CancellationToken token)
        {
            ValidateJobId(jobId);
            using (var connection = await OpenAsync(token).ConfigureAwait(false))
            using (var command = Procedure(connection, "dbo.sp_start_job", jobId))
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        public async Task StopJobAsync(Guid jobId, CancellationToken token)
        {
            ValidateJobId(jobId);
            using (var connection = await OpenAsync(token).ConfigureAwait(false))
            using (var command = Procedure(connection, "dbo.sp_stop_job", jobId))
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        public async Task SaveStepCommandAsync(
            Guid jobId, JobQuickViewStep originalStep, string newCommand, CancellationToken token)
        {
            ValidateJobId(jobId);
            if (originalStep == null)
                throw new ArgumentNullException(nameof(originalStep));

            using (var connection = await OpenAsync(token).ConfigureAwait(false))
            using (var command = new SqlCommand(SaveCommandSql, connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId;
                command.Parameters.Add("@step_id", SqlDbType.Int).Value = originalStep.StepId;
                command.Parameters.Add("@step_uid", SqlDbType.UniqueIdentifier).Value = originalStep.StepUid;
                command.Parameters.Add("@original_command", SqlDbType.NVarChar, -1).Value = (object)originalStep.Command ?? string.Empty;
                command.Parameters.Add("@original_subsystem", SqlDbType.NVarChar, 40).Value = originalStep.Subsystem;
                command.Parameters.Add("@original_database", SqlDbType.NVarChar, 128).Value = (object)originalStep.DatabaseName ?? string.Empty;
                command.Parameters.Add("@original_database_user", SqlDbType.NVarChar, 128).Value = (object)originalStep.DatabaseUserName ?? string.Empty;
                command.Parameters.Add("@original_proxy", SqlDbType.Int).Value = originalStep.ProxyId;
                command.Parameters.Add("@command", SqlDbType.NVarChar, -1).Value = (object)newCommand ?? string.Empty;

                try
                {
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number == 50001)
                {
                    throw new JobCommandConflictException(LocalizationManager.T("The job step was modified by another user or session after it was loaded. Refresh the job to review the current command."), ex);
                }
            }
        }

        internal const string StepsSql = @"
SELECT
    s.step_id,
    s.step_uid,
    s.step_name,
    s.subsystem,
    s.database_name,
    s.database_user_name,
    s.proxy_id,
    s.on_success_action,
    s.on_success_step_id,
    s.on_fail_action,
    s.on_fail_step_id,
    s.command
FROM dbo.sysjobsteps AS s
INNER JOIN dbo.sysjobs_view AS j ON j.job_id = s.job_id
WHERE s.job_id = @job_id
ORDER BY s.step_id ASC;";

        internal const string SaveCommandSql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.sysjobsteps AS s WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.sysjobs_view AS j ON j.job_id = s.job_id
        WHERE s.job_id = @job_id AND s.step_id = @step_id AND s.step_uid = @step_uid
          AND DATALENGTH(ISNULL(s.command, N'')) = DATALENGTH(@original_command)
          AND CONVERT(varbinary(max), ISNULL(s.command, N'')) = CONVERT(varbinary(max), @original_command)
          AND CONVERT(varbinary(80), s.subsystem) = CONVERT(varbinary(80), @original_subsystem)
          AND CONVERT(varbinary(256), ISNULL(s.database_name, N'')) = CONVERT(varbinary(256), @original_database)
          AND CONVERT(varbinary(256), ISNULL(s.database_user_name, N'')) = CONVERT(varbinary(256), @original_database_user)
          AND ISNULL(s.proxy_id, 0) = @original_proxy
    )
        THROW 50001, 'The job step changed after it was loaded.', 1;

    DECLARE @result int;
    EXEC @result = dbo.sp_update_jobstep @job_id = @job_id, @step_id = @step_id, @command = @command;
    IF @result <> 0
        THROW 50002, 'SQL Server Agent did not save the step command.', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;";

        private async Task<SqlConnection> OpenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var connection = await Task.Run(_connectionFactory, token).ConfigureAwait(false);
            if (connection == null)
                throw new InvalidOperationException(LocalizationManager.T("The selected SSMS connection could not be created."));
            try
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                if (!string.Equals(connection.Database, "msdb", StringComparison.OrdinalIgnoreCase))
                {
                    using (var command = new SqlCommand("USE [msdb];", connection) { CommandTimeout = CommandTimeoutSeconds })
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static SqlCommand Procedure(SqlConnection connection, string name, Guid? jobId = null)
        {
            var command = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = CommandTimeoutSeconds };
            if (jobId.HasValue)
                command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId.Value;
            return command;
        }

        private static SqlCommand Query(SqlConnection connection, string sql, Guid jobId)
        {
            var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add("@job_id", SqlDbType.UniqueIdentifier).Value = jobId;
            return command;
        }

        private static int Number(SqlDataReader reader, string name)
        {
            return reader[name] == DBNull.Value ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
        }

        private static string Text(SqlDataReader reader, string name)
        {
            return reader[name] == DBNull.Value ? string.Empty : Convert.ToString(reader[name], CultureInfo.InvariantCulture);
        }

        internal static string ReadScheduleName(IDataRecord reader)
        {
            for (int index = 0; index < reader.FieldCount; index++)
            {
                if (string.Equals(reader.GetName(index), "name", StringComparison.OrdinalIgnoreCase))
                    return reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
            }
            object value = reader["schedule_name"];
            return value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static DateTime? ServerDateTime(SqlDataReader reader, string name)
        {
            return reader[name] == DBNull.Value
                ? (DateTime?)null
                : DateTime.SpecifyKind((DateTime)reader[name], DateTimeKind.Unspecified);
        }

        private static void ValidateJobId(Guid jobId)
        {
            if (jobId == Guid.Empty)
                throw new ArgumentException(LocalizationManager.T("A job ID is required."), nameof(jobId));
        }
    }
}
