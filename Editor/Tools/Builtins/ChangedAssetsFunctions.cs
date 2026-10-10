// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Which project files really changed, from git: an AssetPostprocessor cannot tell a no-op rewrite
    /// from a real change (the old bytes are gone), but git compares content against the index, so a
    /// rebuild that writes identical bytes reports nothing.
    /// </summary>
    [ToolProvider("Asset")]
    internal static class ChangedAssetsFunctions
    {
        internal const int GitTimeoutMs = 10000;

        [Description("List project files whose content differs from git (working tree and index vs HEAD), under Assets/, Packages/ and ProjectSettings/ by default: modified, added, deleted, renamed and untracked files, each with git's index/worktree status letters. Files rewritten with identical bytes (a no-op reimport or rebuild) are not reported, unlike asset-import callbacks. Paths are relative to the Unity project. Read-only: runs git with --no-optional-locks, so the index is not refreshed or locked. Returns NOT_A_GIT_REPOSITORY / GIT_UNAVAILABLE when git can't answer.")]
        [ReadOnlyTool]
        public static Task<object> ListChangedAssets(
            [ToolParam("Comma-separated project folders or files to check (default Assets,Packages,ProjectSettings)", Required = false)] string paths = null,
            [ToolParam("Include untracked (new, never committed) files", Required = false)] bool include_untracked = true,
            [ToolParam("Maximum entries returned, 1..5000", Required = false)] int max_results = 500)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
            var roots = string.IsNullOrWhiteSpace(paths)
                ? new[] { "Assets", "Packages", "ProjectSettings" }
                : paths.Split(',').Select(p => p.Trim().Replace('\\', '/').TrimEnd('/')).Where(p => p.Length > 0).ToArray();
            foreach (var root in roots)
                if (Path.IsPathRooted(root) || root.Split('/').Contains(".."))
                    return Task.FromResult(Response.Error("INVALID_PATH", new { detail = $"'{root}' must be relative to the project and stay inside it." }));
            return ListAsync(projectRoot, roots, include_untracked, Mathf.Clamp(max_results, 1, 5000));
        }

        internal sealed class Change
        {
            public string Path, Status, IndexStatus, WorktreeStatus, RenamedFrom;
        }

        /// <summary>Everything after the first await runs off the main thread: no Unity APIs below.</summary>
        internal static async Task<object> ListAsync(string projectRoot, string[] roots, bool includeUntracked, int maxResults)
        {
            var prefix = await RunGitAsync(projectRoot, "rev-parse --show-prefix").ConfigureAwait(false);
            if (prefix.Error != null) return Response.Error(prefix.Code, new { detail = prefix.Error, supported = false });
            var projectPrefix = prefix.Output.Trim();

            var args = new StringBuilder("--no-optional-locks -c core.quotepath=off status --porcelain=v1 -z --ignored=no");
            args.Append(includeUntracked ? " --untracked-files=all" : " --untracked-files=no");
            args.Append(" --");
            foreach (var root in roots) args.Append(" \"").Append(root.Replace("\"", "")).Append('"');
            var status = await RunGitAsync(projectRoot, args.ToString()).ConfigureAwait(false);
            if (status.Error != null) return Response.Error(status.Code, new { detail = status.Error, supported = status.Code == "NOT_A_GIT_REPOSITORY" ? false : (bool?)null });

            var changes = ParsePorcelain(status.Output, projectPrefix);
            var counts = changes.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            return Response.Success(changes.Count == 0 ? "No changes against git." : $"{changes.Count} changed file(s).", new
            {
                changes = changes.Take(maxResults).Select(c => new
                {
                    path = c.Path,
                    status = c.Status,
                    index = c.IndexStatus,
                    worktree = c.WorktreeStatus,
                    renamed_from = c.RenamedFrom
                }).ToList(),
                count = changes.Count,
                counts,
                truncated = changes.Count > maxResults,
                checked_paths = roots,
                project_prefix_in_repo = projectPrefix.Length > 0 ? projectPrefix : null
            });
        }

        /// <summary>
        /// Parses <c>git status --porcelain=v1 -z</c>: "XY path\0", renames/copies "XY new\0old\0".
        /// Porcelain paths are relative to the repository root; <paramref name="projectPrefix"/> (from
        /// rev-parse --show-prefix) is stripped so paths are relative to the Unity project.
        /// </summary>
        internal static List<Change> ParsePorcelain(string output, string projectPrefix)
        {
            var changes = new List<Change>();
            var parts = (output ?? "").Split('\0');
            for (var i = 0; i < parts.Length; i++)
            {
                var entry = parts[i];
                if (entry.Length < 4 || entry[2] != ' ') continue;
                char x = entry[0], y = entry[1];
                var change = new Change { Path = Relative(entry.Substring(3), projectPrefix), IndexStatus = x.ToString(), WorktreeStatus = y.ToString() };
                if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
                {
                    if (i + 1 < parts.Length) change.RenamedFrom = Relative(parts[++i], projectPrefix);
                }
                change.Status = Classify(x, y);
                if (change.Status != null) changes.Add(change);
            }
            return changes;
        }

        private static string Classify(char x, char y)
        {
            if (x == '!' && y == '!') return null;
            if (x == '?' && y == '?') return "untracked";
            if (x == 'U' || y == 'U' || x == 'A' && y == 'A' || x == 'D' && y == 'D') return "conflicted";
            if (x == 'R' || y == 'R') return "renamed";
            if (x == 'C' || y == 'C') return "copied";
            if (x == 'D' || y == 'D') return "deleted";
            if (x == 'A') return "added";
            if (x == 'T' || y == 'T') return "type_changed";
            return "modified";
        }

        private static string Relative(string repoPath, string projectPrefix) =>
            projectPrefix.Length > 0 && repoPath.StartsWith(projectPrefix, StringComparison.Ordinal) ? repoPath.Substring(projectPrefix.Length) : repoPath;

        internal sealed class GitResult
        {
            public string Output, Error, Code;
        }

        internal static async Task<GitResult> RunGitAsync(string workingDirectory, string arguments)
        {
            Process process;
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };
                process.Start();
            }
            catch (Exception ex)
            {
                return new GitResult { Code = "GIT_UNAVAILABLE", Error = "git could not be started (is it installed and on PATH?): " + ex.Message };
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = new TaskCompletionSource<bool>();
                process.Exited += (_, __) => exited.TrySetResult(true);
                if (process.HasExited) exited.TrySetResult(true);
                var finished = await Task.WhenAny(exited.Task, Task.Delay(GitTimeoutMs)).ConfigureAwait(false);
                if (finished != exited.Task)
                {
                    // Only the process this call started is killed.
                    try { process.Kill(); } catch (Exception) { /* already gone */ }
                    return new GitResult { Code = "GIT_TIMEOUT", Error = $"git did not answer within {GitTimeoutMs / 1000} s." };
                }
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    var notRepo = error.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new GitResult { Code = notRepo ? "NOT_A_GIT_REPOSITORY" : "GIT_FAILED", Error = notRepo ? "The Unity project is not inside a git repository; list_changed_assets is unsupported here." : error.Trim() };
                }
                return new GitResult { Output = output };
            }
        }
    }
}
