// <copyright file="ProcessCancellationTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Diagnostics;

    using Xunit;

    /// <summary>
    /// A cancellation must stop a process that stays silent on its output streams.
    /// </summary>
    public class ProcessCancellationTests
    {
        private static Process StartSilentProcess(int seconds)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"Start-Sleep -Seconds {seconds}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process process = Process.Start(startInfo);
            process.OutputDataReceived += (sender, args) => { };
            process.ErrorDataReceived += (sender, args) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        [Fact]
        public void CancelKillsSilentProcessQuickly()
        {
            using (Process process = StartSilentProcess(60))
            {
                try
                {
                    DateTime cancelTime = DateTime.MaxValue;
                    DateTime started = DateTime.UtcNow;

                    bool killed = ProcessCancellation.WaitForExit(process, () =>
                    {
                        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
                        {
                            if (cancelTime == DateTime.MaxValue)
                            {
                                cancelTime = DateTime.UtcNow;
                            }

                            return true;
                        }

                        return false;
                    });

                    Assert.True(killed);
                    Assert.True(process.HasExited);
                    Assert.True(DateTime.UtcNow - cancelTime < TimeSpan.FromSeconds(1), "The process should be killed within ~0.5 s of the cancellation.");
                }
                finally
                {
                    KillIfRunning(process);
                }
            }
        }

        [Fact]
        public void ProcessThatExitsOnItsOwnIsNotKilled()
        {
            using (Process process = StartSilentProcess(1))
            {
                try
                {
                    bool killed = ProcessCancellation.WaitForExit(process, () => false);

                    Assert.False(killed);
                    Assert.Equal(0, process.ExitCode);
                }
                finally
                {
                    KillIfRunning(process);
                }
            }
        }

        private static void KillIfRunning(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }
}
