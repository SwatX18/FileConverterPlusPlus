// <copyright file="ProcessCancellation.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;

    public static class ProcessCancellation
    {
        public const int PollIntervalMilliseconds = 250;

        /// <summary>
        /// Waits for the process to exit and kills it as soon as a cancellation is requested (within one poll
        /// interval), whatever the process writes on its output streams. Then waits for the asynchronous output
        /// handlers to flush.
        /// </summary>
        /// <returns>True if the process was killed because of the cancellation.</returns>
        public static bool WaitForExit(Process process, Func<bool> isCancelRequested)
        {
            bool killed = false;
            while (!process.WaitForExit(PollIntervalMilliseconds))
            {
                if (!killed && isCancelRequested())
                {
                    try
                    {
                        process.Kill();
                        killed = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited in the meantime.
                    }
                    catch (Win32Exception)
                    {
                        // Exiting or already terminated: the next WaitForExit reports it.
                    }
                }
            }

            // Parameterless overload: also waits for the end of the redirected streams.
            process.WaitForExit();
            return killed;
        }
    }
}
