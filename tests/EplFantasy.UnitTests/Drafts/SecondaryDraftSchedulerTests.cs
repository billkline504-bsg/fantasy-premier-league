using EplFantasy.Drafts;
using Xunit;

namespace EplFantasy.UnitTests.Drafts;

/// <summary>
/// IT-45 (F-006.1, BR-069/BR-281): proves SecondaryDraftScheduler.ProposeStartDate against a fixed
/// fixture calendar — AC1's offset from the transfer window's close date, AC2's one-day-at-a-time
/// advance past every consecutive fixture day (not just a single one), and AC4's "no close date yet
/// confirmed means no proposal at all."
/// </summary>
public class SecondaryDraftSchedulerTests
{
    private static readonly DateOnly CloseDate = new(2027, 1, 31);

    [Fact]
    public void ProposeStartDate_is_the_close_date_plus_the_configured_offset_when_that_day_has_no_fixtures()
    {
        var proposed = SecondaryDraftScheduler.ProposeStartDate(CloseDate, schedulingOffsetDays: 1, datesWithScheduledFixtures: new HashSet<DateOnly>());

        Assert.Equal(new DateOnly(2027, 2, 1), proposed); // BR-281's own default: the day after close.
    }

    [Fact]
    public void ProposeStartDate_honors_a_different_configured_offset()
    {
        var proposed = SecondaryDraftScheduler.ProposeStartDate(CloseDate, schedulingOffsetDays: 3, datesWithScheduledFixtures: new HashSet<DateOnly>());

        Assert.Equal(new DateOnly(2027, 2, 3), proposed);
    }

    [Fact]
    public void ProposeStartDate_advances_one_day_past_a_single_fixture_day()
    {
        var proposedDay = CloseDate.AddDays(1); // 2027-02-01, the initial candidate.
        var fixtureDates = new HashSet<DateOnly> { proposedDay };

        var proposed = SecondaryDraftScheduler.ProposeStartDate(CloseDate, schedulingOffsetDays: 1, fixtureDates);

        Assert.Equal(proposedDay.AddDays(1), proposed);
    }

    [Fact]
    public void ProposeStartDate_advances_past_multiple_consecutive_fixture_days_one_day_at_a_time()
    {
        // Fixtures on 02-01, 02-02, and 02-03 — the first genuinely clear day is 02-04.
        var fixtureDates = new HashSet<DateOnly>
        {
            new(2027, 2, 1),
            new(2027, 2, 2),
            new(2027, 2, 3),
        };

        var proposed = SecondaryDraftScheduler.ProposeStartDate(CloseDate, schedulingOffsetDays: 1, fixtureDates);

        Assert.Equal(new DateOnly(2027, 2, 4), proposed);
    }

    [Fact]
    public void ProposeStartDate_is_unaffected_by_a_fixture_on_a_day_before_the_initial_candidate()
    {
        // A fixture on the close date itself (before the offset is even applied) must never advance
        // the result — only days at-or-after the initial candidate are ever checked.
        var fixtureDates = new HashSet<DateOnly> { CloseDate };

        var proposed = SecondaryDraftScheduler.ProposeStartDate(CloseDate, schedulingOffsetDays: 1, fixtureDates);

        Assert.Equal(CloseDate.AddDays(1), proposed);
    }

    [Fact]
    public void ProposeStartDate_returns_null_when_the_transfer_window_close_date_is_not_yet_confirmed()
    {
        var proposed = SecondaryDraftScheduler.ProposeStartDate(
            transferWindowCloseDate: null, schedulingOffsetDays: 1, datesWithScheduledFixtures: new HashSet<DateOnly>());

        Assert.Null(proposed);
    }
}
