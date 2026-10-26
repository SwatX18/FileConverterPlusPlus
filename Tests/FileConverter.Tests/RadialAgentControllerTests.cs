// <copyright file="RadialAgentControllerTests.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Tests
{
    using System;
    using System.Threading;

    using FileConverter.RadialMenu;

    using Microsoft.Win32;

    using Xunit;

    public class RadialAgentControllerTests
    {
        [Theory]
        [InlineData(false, false, false, false, false)]
        [InlineData(true, false, false, false, false)]
        [InlineData(true, true, false, false, true)]
        [InlineData(true, false, true, false, true)]
        [InlineData(true, true, true, false, true)]
        [InlineData(true, false, false, true, false)]
        [InlineData(false, true, true, true, false)]
        [InlineData(false, true, false, false, false)]
        public void ShouldRunRadialAgentMatrix(bool masterEnabled, bool hotkeyEnabled, bool ctrlDragEnabled, bool unrelatedExtraEntry, bool expected)
        {
            Settings settings = new Settings
            {
                RadialMenuEnabled = masterEnabled,
                RadialMenuHotkeyEnabled = hotkeyEnabled,
                RadialMenuCtrlDragEnabled = ctrlDragEnabled,
                RadialMenuExtraContextMenuEntry = unrelatedExtraEntry,
            };

            Assert.Equal(expected, settings.ShouldRunRadialAgent);
        }

        [Theory]
        [InlineData(true, false, RadialAgentController.AgentAction.Start)]
        [InlineData(true, true, RadialAgentController.AgentAction.Reload)]
        [InlineData(false, true, RadialAgentController.AgentAction.Stop)]
        [InlineData(false, false, RadialAgentController.AgentAction.None)]
        public void ComputeActionMatrix(bool shouldRun, bool isRunning, RadialAgentController.AgentAction expected)
        {
            Assert.Equal(expected, RadialAgentController.ComputeAction(shouldRun, isRunning));
        }

        [Fact]
        public void SetAutostartWritesAndRemovesRunValueInTestKey()
        {
            // Never touches the real Run key.
            string testKeyPath = @"Software\FileConverterPlusPlus.Tests\" + Guid.NewGuid().ToString("N");
            const string exePath = @"C:\Program Files\FileConverter++\FileConverterPlusPlus.exe";
            try
            {
                RadialAgentController.SetAutostart(true, exePath, testKeyPath);
                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKeyPath))
                {
                    Assert.NotNull(key);
                    Assert.Equal("\"" + exePath + "\" --agent", key.GetValue(RadialAgentNames.RunValueName));
                    Assert.Equal(RegistryValueKind.String, key.GetValueKind(RadialAgentNames.RunValueName));
                }

                // Enabling twice keeps a single value.
                RadialAgentController.SetAutostart(true, exePath, testKeyPath);

                RadialAgentController.SetAutostart(false, exePath, testKeyPath);
                using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKeyPath))
                {
                    Assert.NotNull(key);
                    Assert.Null(key.GetValue(RadialAgentNames.RunValueName));
                }
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FileConverterPlusPlus.Tests", false);
            }
        }

        [Fact]
        public void DisablingAutostartWithoutKeyOrValueDoesNotThrow()
        {
            string missingKeyPath = @"Software\FileConverterPlusPlus.Tests\" + Guid.NewGuid().ToString("N");
            RadialAgentController.SetAutostart(false, @"C:\x.exe", missingKeyPath);

            Assert.Null(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(missingKeyPath));
        }

        [Fact]
        public void AgentIsRunningWhileItsMutexExists()
        {
            // Skipped silently when a real agent already owns the mutex on this machine.
            using (Mutex mutex = new Mutex(false, RadialAgentNames.Mutex, out bool createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Assert.True(RadialAgentController.IsAgentRunning());

                // No exit event exists, so the agent cannot stop: the call must time out.
                Assert.False(RadialAgentController.StopAgent(TimeSpan.FromMilliseconds(100)));
            }

            Assert.False(RadialAgentController.IsAgentRunning());
            Assert.True(RadialAgentController.StopAgent(TimeSpan.Zero));
        }
    }
}
