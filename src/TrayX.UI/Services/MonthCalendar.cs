using System.Globalization;

namespace TrayX.UI.Services;

public sealed record CalendarDay(
    DateTime Date,
    string Number,
    bool IsToday,
    bool IsCurrentMonth,
    bool IsSelected);

/// <summary>
/// Сетка месяца для панели: недели начинаются с первого дня недели той локали,
/// что выбрана в интерфейсе.
/// </summary>
public static class MonthCalendar
{
    public static string[] WeekdayNames(IFormatProvider culture)
    {
        var format = DateTimeFormatInfo.GetInstance(culture);
        var first = (int)format.FirstDayOfWeek;
        var names = new string[7];

        for (var index = 0; index < 7; index++)
        {
            names[index] = format.AbbreviatedDayNames[(first + index) % 7];
        }

        return names;
    }

    public static IReadOnlyList<IReadOnlyList<CalendarDay>> Build(
        DateTime month,
        DateTime today,
        IFormatProvider culture,
        DateTime? selectedDate = null)
    {
        var format = DateTimeFormatInfo.GetInstance(culture);
        var first = (int)format.FirstDayOfWeek;
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        var leading = ((int)firstOfMonth.DayOfWeek - first + 7) % 7;
        var total = leading + DateTime.DaysInMonth(month.Year, month.Month);
        var start = firstOfMonth.AddDays(-leading);
        var todayDate = today.Date;
        var selected = selectedDate?.Date;

        var weeks = new List<IReadOnlyList<CalendarDay>>((total + 6) / 7);
        for (var offset = 0; offset < total; offset += 7)
        {
            var week = new List<CalendarDay>(7);
            for (var day = 0; day < 7; day++)
            {
                var current = start.AddDays(offset + day);
                week.Add(new CalendarDay(
                    current,
                    current.Day.ToString(culture),
                    current.Date == todayDate,
                    current.Month == month.Month && current.Year == month.Year,
                    current.Date == selected));
            }

            weeks.Add(week);
        }

        return weeks;
    }
}
