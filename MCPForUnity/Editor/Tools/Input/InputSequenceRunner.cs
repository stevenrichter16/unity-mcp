using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// Manages async timed input sequences. Steps execute via EditorApplication.update ticks
    /// with timing based on EditorApplication.timeSinceStartup.
    /// Follows the run_tests/get_test_job async pattern — returns a job_id immediately,
    /// polled via get_sequence_status.
    /// </summary>
    internal static class InputSequenceRunner
    {
        private static readonly Dictionary<string, SequenceJob> _jobs = new Dictionary<string, SequenceJob>();
        private static readonly int MaxJobs = 10;
        private static readonly int MaxStepsPerSequence = 200;

        private class SequenceJob
        {
            public string JobId;
            public List<SequenceStep> Steps;
            public int CurrentStepIndex;
            public string Status; // "running", "completed", "failed", "cancelled"
            public double StartedTime;
            public double? FinishedTime;
            public double NextStepTime;
            public string Error;
            public List<string> Log;
        }

        private class SequenceStep
        {
            public string Action;
            public JObject Params;
            public float DelaySeconds;
        }

        public static object StartSequence(JObject @params)
        {
            if (_jobs.Count >= MaxJobs)
            {
                // Clean up completed jobs
                var toRemove = new List<string>();
                foreach (var kvp in _jobs)
                {
                    if (kvp.Value.Status != "running")
                        toRemove.Add(kvp.Key);
                }
                foreach (var key in toRemove)
                    _jobs.Remove(key);

                if (_jobs.Count >= MaxJobs)
                    return new ErrorResponse($"Too many active sequence jobs ({MaxJobs}). Wait for existing jobs to complete.");
            }

            var sequenceToken = @params["sequence"];
            if (sequenceToken == null || sequenceToken.Type != JTokenType.Array)
                return new ErrorResponse("Required parameter 'sequence' must be an array of steps. Each step: {action, params?, delay?}.");

            var stepsArray = (JArray)sequenceToken;
            if (stepsArray.Count == 0)
                return new ErrorResponse("Sequence must contain at least one step.");
            if (stepsArray.Count > MaxStepsPerSequence)
                return new ErrorResponse($"Sequence exceeds maximum of {MaxStepsPerSequence} steps.");

            var steps = new List<SequenceStep>();
            for (int i = 0; i < stepsArray.Count; i++)
            {
                var stepObj = stepsArray[i] as JObject;
                if (stepObj == null)
                    return new ErrorResponse($"Step {i} must be a JSON object with 'action' field.");

                var actionStr = stepObj["action"]?.ToString();
                if (string.IsNullOrEmpty(actionStr))
                    return new ErrorResponse($"Step {i} is missing required 'action' field.");

                float delay = stepObj["delay"]?.Value<float>() ?? 0f;
                var stepParams = stepObj["params"] as JObject ?? new JObject();
                // Copy action into params so ManageInput.ExecuteAction can find it
                stepParams["action"] = actionStr;

                steps.Add(new SequenceStep
                {
                    Action = actionStr.ToLowerInvariant(),
                    Params = stepParams,
                    DelaySeconds = delay
                });
            }

            string jobId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var job = new SequenceJob
            {
                JobId = jobId,
                Steps = steps,
                CurrentStepIndex = 0,
                Status = "running",
                StartedTime = EditorApplication.timeSinceStartup,
                NextStepTime = EditorApplication.timeSinceStartup + steps[0].DelaySeconds,
                Log = new List<string>()
            };

            _jobs[jobId] = job;

            // Register update tick if not already registered
            EditorApplication.update -= TickJobs;
            EditorApplication.update += TickJobs;

            return new SuccessResponse($"Input sequence started with {steps.Count} steps.", new
            {
                job_id = jobId,
                total_steps = steps.Count,
                status = "running"
            });
        }

        public static object GetStatus(ToolParams p)
        {
            var jobIdResult = p.GetRequired("job_id");
            if (!jobIdResult.IsSuccess)
                return new ErrorResponse(jobIdResult.ErrorMessage);

            if (!_jobs.TryGetValue(jobIdResult.Value, out var job))
                return new ErrorResponse($"No sequence job found with ID '{jobIdResult.Value}'.");

            return new SuccessResponse($"Sequence job {job.Status}.", new
            {
                job_id = job.JobId,
                status = job.Status,
                current_step = job.CurrentStepIndex,
                total_steps = job.Steps.Count,
                elapsed_seconds = EditorApplication.timeSinceStartup - job.StartedTime,
                error = job.Error,
                log = job.Log
            });
        }

        private static void TickJobs()
        {
            if (!EditorApplication.isPlaying)
            {
                // Cancel all running jobs if we exit Play Mode
                foreach (var job in _jobs.Values)
                {
                    if (job.Status == "running")
                    {
                        job.Status = "cancelled";
                        job.Error = "Play Mode exited during sequence execution.";
                        job.FinishedTime = EditorApplication.timeSinceStartup;
                    }
                }
                EditorApplication.update -= TickJobs;
                return;
            }

            bool anyRunning = false;
            foreach (var job in _jobs.Values)
            {
                if (job.Status != "running")
                    continue;

                anyRunning = true;

                if (EditorApplication.timeSinceStartup < job.NextStepTime)
                    continue;

                // Execute current step
                var step = job.Steps[job.CurrentStepIndex];
                try
                {
                    var p = new ToolParams(step.Params);
                    var result = ManageInput.ExecuteAction(step.Action, p, step.Params);

                    if (result is ErrorResponse err)
                    {
                        job.Log.Add($"Step {job.CurrentStepIndex} ({step.Action}): FAILED - {err.Error}");
                        job.Status = "failed";
                        job.Error = $"Step {job.CurrentStepIndex} ({step.Action}) failed: {err.Error}";
                        job.FinishedTime = EditorApplication.timeSinceStartup;
                        continue;
                    }

                    job.Log.Add($"Step {job.CurrentStepIndex} ({step.Action}): OK");
                }
                catch (Exception e)
                {
                    job.Log.Add($"Step {job.CurrentStepIndex} ({step.Action}): EXCEPTION - {e.Message}");
                    job.Status = "failed";
                    job.Error = $"Step {job.CurrentStepIndex} ({step.Action}) threw: {e.Message}";
                    job.FinishedTime = EditorApplication.timeSinceStartup;
                    continue;
                }

                // Advance to next step
                job.CurrentStepIndex++;
                if (job.CurrentStepIndex >= job.Steps.Count)
                {
                    job.Status = "completed";
                    job.FinishedTime = EditorApplication.timeSinceStartup;
                }
                else
                {
                    job.NextStepTime = EditorApplication.timeSinceStartup + job.Steps[job.CurrentStepIndex].DelaySeconds;
                }
            }

            if (!anyRunning)
                EditorApplication.update -= TickJobs;
        }

        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            // Clean up on domain reload (e.g., recompile during Play Mode)
            EditorApplication.update -= TickJobs;
            _jobs.Clear();
        }
    }
}
