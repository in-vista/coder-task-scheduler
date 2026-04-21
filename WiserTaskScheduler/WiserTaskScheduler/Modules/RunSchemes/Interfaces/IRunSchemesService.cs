using System;
using WiserTaskScheduler.Modules.RunSchemes.Models;

namespace WiserTaskScheduler.Modules.RunSchemes.Interfaces
{
    public interface IRunSchemesService
    {
        /// <summary>
        /// Get the time the worker has to wait till it can start its next run.
        /// </summary>
        /// <param name="runScheme">The run scheme of the action.</param>
        /// <returns></returns>
        TimeSpan GetTimeTillNextRun(RunSchemeModel runScheme);

        /// <summary>
        /// Get the date and time when the worker will perform its next run.
        /// </summary>
        /// <param name="runScheme">The run scheme of the action.</param>
        /// <param name="referenceTime">The time on which the next should be calculated.</param>
        /// <returns></returns>
        DateTime GetDateTimeTillNextRun(RunSchemeModel runScheme, DateTime referenceTime);
    }
}
