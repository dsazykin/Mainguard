using System;
using System.Collections.Generic;
using System.IO;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// Records facts on the CI run's summary page, regardless of verbosity or outcome.
///
/// <para><b>Why not <c>ITestOutputHelper</c> alone.</b> At the <c>--verbosity normal</c> the workflow
/// uses, xUnit prints a test's output only when it FAILS. An instrument whose whole job is to report on
/// GREEN runs, which reports only on red ones, is not a diagnostic — it is the same class of lie this
/// suite has been burned by before. <c>$GITHUB_STEP_SUMMARY</c> lands the facts on the run's summary
/// page either way, and is simply absent (a no-op) on a developer machine.</para>
///
/// <para>Best-effort by construction: a diagnostic must never be the reason a suite fails.</para>
/// </summary>
internal static class JobSummary
{
    public static void Write(string heading, IReadOnlyList<string> lines)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, $"### {heading}\n\n```\n{string.Join("\n", lines)}\n```\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
