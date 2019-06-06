using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Elastic.Apm.Api;
using Elastic.Apm.Logging;

namespace Elastic.Apm.Metrics.MetricsProvider
{
	public class SystemTotalCpuProvider : IMetricsProvider
	{
		private const string SystemCpuTotalPct = "system.cpu.total.norm.pct";
		private readonly IApmLogger _logger;

		public SystemTotalCpuProvider(IApmLogger logger) => _logger = logger.Scoped(nameof(SystemTotalCpuProvider));

		private bool _firstTotal = true;

		private bool _getTotalProcessorTimeFailedLog;
		private TimeSpan _lastCurrentTotalProcessCpuTime;
		private DateTime _lastTotalTick;
		private Version _processAssemblyVersion;
		private PerformanceCounter _processorTimePerfCounter;

		public int ConsecutiveNumberOfFailedReads { get; set; }
		public string DbgName => "total system CPU time";

		public IEnumerable<MetricSample> GetSamples()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				if (_processorTimePerfCounter == null)
					_processorTimePerfCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

				var val = _processorTimePerfCounter.NextValue();

				return new List<MetricSample> { new MetricSample(SystemCpuTotalPct, (double)val / 100) };
			}

			//The x-plat implementation is ~18x slower than perf. counters on Windows
			//Therefore this is only a fallback in case of non-Windows OSs
			var timeSpan = DateTime.UtcNow;
			TimeSpan cpuUsage;

			foreach (var proc in Process.GetProcesses())
			{
				try
				{
					cpuUsage += proc.TotalProcessorTime;
				}
				catch (Exception)
				{
					//can happen if the current process does not have access to `proc`.
					if (_getTotalProcessorTimeFailedLog) continue;

					_getTotalProcessorTimeFailedLog = true;
					_logger.Info()
						?.Log(
							"The overall CPU usage reported by the agent may be inaccurate. This is because the agent was unable to access the CPU usage of some other processes");
				}
			}

			var currentTimeStamp = DateTime.UtcNow;

			if (!_firstTotal)
			{
				if (_processAssemblyVersion == null)
					_processAssemblyVersion = typeof(Process).Assembly.GetName().Version;

				double cpuUsedMs;

				//workaround for a CoreFx bug. See: https://github.com/dotnet/corefx/issues/37614#issuecomment-492489373
				if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && _processAssemblyVersion < new Version(4, 3, 0))
					cpuUsedMs = (cpuUsage - _lastCurrentTotalProcessCpuTime).TotalMilliseconds / 100;
				else
					cpuUsedMs = (cpuUsage - _lastCurrentTotalProcessCpuTime).TotalMilliseconds;

				var totalMsPassed = (currentTimeStamp - _lastTotalTick).TotalMilliseconds;

				double cpuUsageTotal;

				// ReSharper disable once CompareOfFloatsByEqualityOperator
				if (totalMsPassed != 0)
					cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
				else
					cpuUsageTotal = 0;

				_firstTotal = false;
				_lastTotalTick = timeSpan;
				_lastCurrentTotalProcessCpuTime = cpuUsage;

				return new List<MetricSample> { new MetricSample(SystemCpuTotalPct, cpuUsageTotal) };
			}

			_firstTotal = false;
			_lastTotalTick = timeSpan;
			_lastCurrentTotalProcessCpuTime = cpuUsage;

			return null;
		}
	}
}
