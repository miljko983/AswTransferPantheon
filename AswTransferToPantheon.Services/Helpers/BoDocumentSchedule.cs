using System;
using System.IO;
using System.Linq;
using AswTransferToPantheon.Infrastructure.Configuration;

namespace AswTransferToPantheon.Services.Helpers;

public static class BoDocumentSchedule
{
    public static void Validate(BoDocumentTaskConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Name)
            || configuration.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "BO raspored mora imati ispravan Name.");
        }

        if (configuration.Start < TimeSpan.Zero
            || configuration.Start >= TimeSpan.FromDays(1))
        {
            throw new ArgumentException(
                "BO Start mora biti od 00:00:00 do pre 24:00:00.");
        }
        if (configuration.PeriodInMinutes is < 1 or > 1440)
        {
            throw new ArgumentException(
                "BO PeriodInMinutes mora biti između 1 i 1440 ili izostavljen.");
        }
        if (configuration.BatchSize is < 1 or > 1000)
        {
            throw new ArgumentException(
                "BO BatchSize mora biti između 1 i 1000.");
        }

        if (configuration.CommandTimeoutSeconds <= 0)
        {
            throw new ArgumentException(
                "BO CommandTimeoutSeconds mora biti veći od nule.");
        }

        if (configuration.UserId is <= 0)
        {
            throw new ArgumentException(
                "BO UserId mora biti pozitivan ili null.");
        }

        foreach (var day in configuration.DaysOfWeek ?? [])
        {
            ParseDay(day);
        }
    }

    public static bool IsAllowedDay(BoDocumentTaskConfiguration configuration, DateTime date)
    {
        return configuration.DaysOfWeek is null
            || configuration.DaysOfWeek.Count == 0
            || configuration.DaysOfWeek.Any(
                day => ParseDay(day) == date.DayOfWeek);
    }

    public static DateTime NextRun(BoDocumentTaskConfiguration configuration, DateTime now,
                                   bool firstCall, DateTime? lastAttemptAt)
    {
        Validate(configuration);

        var repeats = configuration.PeriodInMinutes.HasValue;

        if (firstCall
            && configuration.ExecuteOnStartup
            && IsAllowedDay(configuration, now)
            && (!lastAttemptAt.HasValue || now > lastAttemptAt.Value)
            && (repeats || lastAttemptAt?.Date != now.Date))
        {
            return now;
        }

        var interval = configuration.PeriodInMinutes is int minutes
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromDays(1);

        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.Date.AddDays(offset);

            if (!IsAllowedDay(configuration, day)
                || (!repeats && day == lastAttemptAt?.Date))
            {
                continue;
            }

            // Termini se računaju od Start, samo do kraja ovog dana.
            for (var time = configuration.Start;
                 time < TimeSpan.FromDays(1);
                 time += interval)
            {
                var candidate = day.Add(time);

                // Preskače propuštene termine i već izvršen pokušaj.
                if (candidate >= now
                    && (!lastAttemptAt.HasValue
                        || candidate > lastAttemptAt.Value))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "Nije pronađen sledeći termin BO rasporeda.");
    }

    private static DayOfWeek ParseDay(string day)
    {
        var name = Enum.GetNames<DayOfWeek>().FirstOrDefault(
            value => string.Equals(
                value,
                day?.Trim(),
                StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new ArgumentException(
                $"Neispravan BO dan '{day}'. Koristi Monday, Tuesday, " +
                "Wednesday, Thursday, Friday, Saturday ili Sunday.");
        }

        return Enum.Parse<DayOfWeek>(name);
    }
}