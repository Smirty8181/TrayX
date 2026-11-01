using System.Globalization;
using TrayX.UI.Services;

namespace TrayX.IntegrationTests;

public sealed class MonthCalendarTests
{
    [Fact]
    public void Build_UsesLocalizedFirstDayOfWeek()
    {
        var weeks = MonthCalendar.Build(
            new DateTime(2021, 2, 1),
            new DateTime(2021, 2, 1),
            CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal(4, weeks.Count);
        Assert.Equal(new DateTime(2021, 2, 1), weeks[0][0].Date);
        Assert.Equal("Mon", MonthCalendar.WeekdayNames(CultureInfo.GetCultureInfo("en-GB"))[0]);
    }

    [Fact]
    public void Build_IncludesLeapDayAndMarksSelectedDate()
    {
        var selectedDate = new DateTime(2024, 2, 29);
        var weeks = MonthCalendar.Build(
            new DateTime(2024, 2, 1),
            selectedDate,
            CultureInfo.GetCultureInfo("en-US"),
            selectedDate);

        var leapDay = Assert.Single(weeks.SelectMany(week => week), day => day.Date == selectedDate);

        Assert.Equal(5, weeks.Count);
        Assert.True(leapDay.IsCurrentMonth);
        Assert.True(leapDay.IsToday);
        Assert.True(leapDay.IsSelected);
        Assert.Equal("29", leapDay.Number);
    }
}
