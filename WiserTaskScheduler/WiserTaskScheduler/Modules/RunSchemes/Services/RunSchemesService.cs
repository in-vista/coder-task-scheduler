using System;
using System.Collections.Generic;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using WiserTaskScheduler.Modules.RunSchemes.Enums;
using WiserTaskScheduler.Modules.RunSchemes.Interfaces;
using WiserTaskScheduler.Modules.RunSchemes.Models;

namespace WiserTaskScheduler.Modules.RunSchemes.Services
{
    public class RunSchemesService : IRunSchemesService, IScopedService, ISingletonService
    {
        /// <inheritdoc />
        public TimeSpan GetTimeTillNextRun(RunSchemeModel runScheme)
        {
            var now = DateTime.Now;
            return GetDateTimeTillNextRun(runScheme, now) - now;
        }

        /// <inheritdoc />
        public DateTime GetDateTimeTillNextRun(RunSchemeModel runScheme, DateTime referenceTime)
        {
            switch (runScheme.Type)
            {
                case RunSchemeTypes.Continuous:
                    return CalculateNextDelayedDateTime(runScheme, referenceTime);
                case RunSchemeTypes.Daily:
                    return CalculateNextDailyDateTime(runScheme, referenceTime);
                case RunSchemeTypes.Weekly:
                    return CalculateNextWeeklyDateTime(runScheme, referenceTime);
                case RunSchemeTypes.Monthly:
                    return CalculateNextMonthlyDateTime(runScheme, referenceTime);
                default:
                    throw new ArgumentOutOfRangeException(nameof(runScheme.Type), runScheme.Type.ToString());
            }
        }

        /// <summary>
        /// Calculate the time till the next run based on the delay of the run scheme.
        /// </summary>
        /// <param name="runScheme">The run scheme to use.</param>
        /// <returns></returns>
        private DateTime CalculateNextDelayedDateTime(RunSchemeModel runScheme, DateTime referenceTime)
        {
            var minimumNextRun = referenceTime.AddSeconds(1);
            var nextDateTime = referenceTime.Date;
            
            nextDateTime = HandleSkipDays(runScheme, nextDateTime);
            nextDateTime = SetupStartStopTimes(runScheme, nextDateTime, referenceTime);

            while (nextDateTime <= minimumNextRun)
                nextDateTime += runScheme.Delay;

            nextDateTime = ForceStartStopTimes(runScheme, nextDateTime);
            nextDateTime = HandleSkipDays(runScheme, nextDateTime);
            
            while (nextDateTime <= minimumNextRun)
                nextDateTime += runScheme.Delay;

            return nextDateTime;
        }

        /// <summary>
        /// Calculate the time till the next date.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <returns></returns>
        private DateTime CalculateNextDailyDateTime(RunSchemeModel runScheme, DateTime referenceTime)
        {
            var minimumNextRun = referenceTime.AddSeconds(1);
            var nextDateTime = referenceTime.Date;

            nextDateTime = nextDateTime.AddHours(runScheme.Hour.Hours).AddMinutes(runScheme.Hour.Minutes).AddSeconds(runScheme.Hour.Seconds);

            if (nextDateTime <= minimumNextRun)
            {
                nextDateTime = nextDateTime.AddDays(1);
            }

            nextDateTime = HandleSkipDays(runScheme, nextDateTime);

            return nextDateTime;
        }

        /// <summary>
        /// Calculate the time till the next date of the day of the week.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <returns></returns>
        private DateTime CalculateNextWeeklyDateTime(RunSchemeModel runScheme, DateTime referenceTime)
        {
            var nextDateTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, runScheme.Hour.Hours, runScheme.Hour.Minutes, runScheme.Hour.Seconds);
            var dayOfWeek = (DayOfWeek)(runScheme.DayOfWeek % 7);

            while (nextDateTime.DayOfWeek != dayOfWeek || nextDateTime <= referenceTime)
            {
                nextDateTime = nextDateTime.AddDays(1);
            }

            return nextDateTime;
        }

        /// <summary>
        /// Calculate the time till the next date of the day of the month.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <returns></returns>
        private DateTime CalculateNextMonthlyDateTime(RunSchemeModel runScheme, DateTime referenceTime)
        {
            var nextDateTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, Math.Min(runScheme.DayOfMonth, DateTime.DaysInMonth(DateTime.Now.Year, DateTime.Now.Month)), runScheme.Hour.Hours, runScheme.Hour.Minutes, runScheme.Hour.Seconds);

            if (nextDateTime >= referenceTime)
            {
                return nextDateTime;
            }

            nextDateTime = nextDateTime.AddMonths(1);
            if (nextDateTime.Day < runScheme.DayOfMonth)
            {
                nextDateTime = nextDateTime.AddDays(DateTime.DaysInMonth(nextDateTime.Year, nextDateTime.Month) - nextDateTime.Day);
            }

            return nextDateTime;
        }

        /// <summary>
        /// Modify the next date time to be on an allowed day.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <param name="nextDateTime"></param>
        /// <returns></returns>
        private DateTime HandleSkipDays(RunSchemeModel runScheme, DateTime nextDateTime)
        {
            var daysToSkip = GetDaysToSkip(runScheme);

            if (daysToSkip.Count <= 0)
            {
                return nextDateTime;
            }

            while(daysToSkip.Contains(nextDateTime.DayOfWeek) || runScheme.SkipWeekend && nextDateTime.DayOfWeek == DayOfWeek.Friday && nextDateTime.Hour >= 16)
            {
                nextDateTime = nextDateTime.AddDays(1);
            }

            return nextDateTime;
        }

        /// <summary>
        /// Modify the next date time to start at the starting time if provided.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <param name="nextDateTime"></param>
        /// <returns></returns>
        private DateTime SetupStartStopTimes(RunSchemeModel runScheme, DateTime nextDateTime, DateTime referenceTime)
        {
            if (runScheme.StartTime == runScheme.StopTime)
            {
                return nextDateTime;
            }

            nextDateTime = nextDateTime.AddSeconds(runScheme.StartTime.TotalSeconds);
                
            // Start yesterday if the current start time has not been passed when the start time is later than the stop time.
            if (runScheme.StartTime > runScheme.StopTime && referenceTime.TimeOfDay < runScheme.StartTime)
            {
                nextDateTime = nextDateTime.AddDays(-1);
            }

            return nextDateTime;
        }

        /// <summary>
        /// Modify the next date time after it has been calculated to be within bounds.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <param name="nextDateTime"></param>
        /// <returns></returns>
        private DateTime ForceStartStopTimes(RunSchemeModel runScheme, DateTime nextDateTime)
        {
            if (runScheme.StartTime == runScheme.StopTime)
            {
                return nextDateTime;
            }

            // Start tomorrow if the stop time has been passed and the start time is earlier than the stop time.
            if (runScheme.StartTime < runScheme.StopTime && nextDateTime.TimeOfDay >= runScheme.StopTime)
            {
                nextDateTime = nextDateTime.Date.AddDays(1).AddSeconds(runScheme.StartTime.TotalSeconds);
            }
            // Start at the start time if the current start time is between the stop and start time.
            else if (runScheme.StartTime > runScheme.StopTime && nextDateTime.TimeOfDay < runScheme.StartTime && nextDateTime.TimeOfDay >= runScheme.StopTime)
            {
                nextDateTime = nextDateTime.Date.AddSeconds(runScheme.StartTime.TotalSeconds);
            }

            return nextDateTime;
        }

        /// <summary>
        /// Get a hash set of the days to skip, includes weekend if skipWeekend is true.
        /// </summary>
        /// <param name="runScheme"></param>
        /// <returns></returns>
        private HashSet<DayOfWeek> GetDaysToSkip(RunSchemeModel runScheme)
        {
            var daysToSkip = new HashSet<DayOfWeek>();

            if (runScheme.SkipDays == null)
            {
                return daysToSkip;
            }

            foreach (var day in runScheme.SkipDays)
            {
                    daysToSkip.Add((DayOfWeek)(day % 7));
            }

            if (!runScheme.SkipWeekend)
            {
                return daysToSkip;
            }

            daysToSkip.Add(DayOfWeek.Saturday);
            daysToSkip.Add(DayOfWeek.Sunday);

            return daysToSkip;
        }
    }
}
