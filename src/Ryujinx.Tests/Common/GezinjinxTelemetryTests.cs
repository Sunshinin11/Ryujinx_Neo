using NUnit.Framework;
using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System.Collections.Generic;

namespace Ryujinx.Tests.Common
{
    public class GezinjinxTelemetryTests
    {
        private class TestLogTarget : ILogTarget
        {
            public readonly List<string> Messages = new();
            public string Name => "TestGezinjinx";
            public void Log(object sender, LogEventArgs args) => Messages.Add(args.Message);
            public void Dispose() { }
        }

        [Test]
        public void DetailedTelemetry_IsDisabledInCurrent()
        {
            Assert.That(GezinjinxPerformanceMode.DetailedTelemetry, Is.False);
        }

        [Test]
        public void GuestStallWatchdog_LogsNoticeOnEnable()
        {
            var target = new TestLogTarget();
            Logger.AddTarget(target);

            try
            {
                GezinjinxGuestStallTelemetry.Enable();
                bool found = target.Messages.Exists(m => m.Contains("[RYUJINX-NEO-GUEST-STALL] watchdog enabled"));
                Assert.That(found, Is.False, "Expected watchdog to remain disabled in Current");
            }
            finally
            {
                Logger.RemoveTarget(target.Name);
            }
        }
    }
}
