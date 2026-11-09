namespace LARGA.ManagerWeb.Services.Tours;

/// <summary>
/// Every page tour, in one place. To change a tour, edit its steps below; to add one, add a
/// <see cref="PageTour"/> to <see cref="All"/> (its Id is the page's route, e.g. "garage") and put
/// <c>data-tour="..."</c> on the elements it points at. Four to five steps per tour: the
/// "More questions? Open Help &amp; FAQs" step is added automatically at the end.
/// Plain English only - the readers aren't technical.
/// </summary>
public static class TourCatalog
{
    public static readonly IReadOnlyList<PageTour> All =
    [
        new("dashboard", 1, "roles",
        [
            new("fleet-status", "Fleet status", "Each pill counts your taxis by their current status. Click a pill to see which units are in it."),
            new("generate-reports", "Reports", "Create a PDF or CSV report for any date range. Only the Manager can do this.", ManagerOnly: true),
            new("stats", "The week at a glance", "Revenue, drivers on shift, kilometres driven and open incidents. The small percentage compares with the week before."),
            new("utilization", "How busy the fleet is", "How much of the fleet worked, how many units are on the road right now, and how much of the boundary due has been collected."),
            new("standings", "Top driver standings", "Ranks drivers over the period shown in the heading. These numbers are worked out automatically, so they can't be edited."),
        ]),

        new("driver-shifts", 1, "drivers",
        [
            new("clockin-approvals", "Clock-in approvals", "When a driver's clock-in needs a second look, it waits here. Approving or denying it is for the Manager only.", ManagerOnly: true),
            new("driver-stats", "Driver counts", "How many drivers you have, who is on shift, who is off duty, and whose license has expired."),
            new("driver-tabs", "Three views", "Live Roster shows who is working now. Schedule Planner sets days off and substitute units. Shift Logs lists past shifts."),
            new("roster-table", "Live Roster", "One row per driver with their license status, current status and assigned unit."),
            new("add-driver", "Add a new driver", "Creates the driver's account with a temporary password. They must change it the first time they log in. Only the Manager can do this.", ManagerOnly: true),
        ]),

        new("driver-shifts-schedule", 1, "drivers",
        [
            new("driver-tabs", "Schedule Planner", "You are on the Schedule Planner. Use the tabs to switch between the roster, the schedule and the shift logs."),
            new("schedule-week", "Pick a week", "Use the arrows to move between weeks. Every day starts as a working day."),
            new("schedule-grid", "The weekly grid", "Rows are drivers and columns are days. A cell shows a day off or a substitute unit when there is one."),
            new("schedule-grid", "Change a day", "Click a cell to mark a day off, or to give the driver a different unit for that day. Only the Manager can do this.", ManagerOnly: true),
        ]),

        new("driver-shifts-logs", 1, "drivers",
        [
            new("driver-tabs", "Shift Logs", "You are looking at past shifts. Use the tabs to go back to the roster or the schedule."),
            new("log-filters", "Narrow it down", "Filter by date, driver, unit or status to find the shift you want."),
            new("view-route", "Route review", "View route opens a map of where the unit went during that shift. Locations are only recorded while a shift is running, and old routes expire."),
        ]),

        new("financial-ledger", 1, "ledger",
        [
            new("ledger-tabs", "Two views", "Daily Settlements shows what each shift owes today. Master Debt Ledger shows what each driver still owes in total."),
            new("settlement-summary", "Today's totals", "What should be collected, what has come in, and what is still outstanding."),
            new("ledger-search", "Find a driver", "Type a driver's name or unit to narrow the list."),
            new("record-payment", "Record a payment", "Use this to record cash or GCash money that a driver has handed over. LARGA does not hold any money. It only records and checks payments."),
            new("adjustment-btn", "Adjustments", "Add a manual debt adjustment or correct a payment that was recorded wrongly. Only the Manager can do this.", ManagerOnly: true),
        ]),

        new("financial-ledger-master", 1, "ledger",
        [
            new("ledger-tabs", "Master Debt Ledger", "You are looking at the total each driver owes. Use the tabs to go back to today's settlements."),
            new("debt-stats", "Debt at a glance", "How many drivers owe money, how much is owed in total, and how many drivers owe nothing."),
            new("ledger-search", "Find a driver", "Type a driver's name to narrow the list."),
            new("settle-debt", "Settle a debt", "Record a payment against what a driver already owes from earlier days."),
            new("adjustment-btn", "Adjustments", "Add a manual debt adjustment or correct a payment that was recorded wrongly. Only the Manager can do this.", ManagerOnly: true),
        ]),

        new("garage", 1, "garage",
        [
            new("garage-nav", "Jump to a section", "Use these links to go straight to driver reports, upcoming jobs, jobs in the shop, or routine checks."),
            new("pending-reports", "Driver reports", "Problems reported by drivers wait here until someone reviews them."),
            new("create-ticket", "Create a work order", "Turns a driver report into a work order and books the shop day. Only the Manager can do this.", ManagerOnly: true),
            new("active-orders", "In the shop", "Units that are in the shop right now. A unit in the shop can't go out on a shift."),
            new("garage-history", "History", "See past work orders, including finished and dismissed ones."),
        ]),

        new("inventory", 1, "garage",
        [
            new("stock-filters", "Stock levels", "Filter parts by Normal, Low Stock or Critical, and see how many parts are in each group."),
            new("parts-table", "Spare parts list", "One row per part with how many are left, the minimum to keep, and its price. Click a row to see where it was used."),
            new("show-archived", "Archived parts", "Parts you no longer stock are archived, not deleted, so their past use stays on record. Tick this to see them."),
            new("log-part", "Add a part", "Log a new spare part here."),
        ]),

        new("fuel-verification", 1, "fuel",
        [
            new("fuel-stats", "Fuel reports", "How many fuel reports are waiting, approved, or flagged for a problem."),
            new("fuel-tabs", "Pending, Verified, Flagged", "Pending reports need a look. Verified ones are approved. Flagged ones have a problem that needs an answer."),
            new("fuel-table", "The queue", "Each row is one refuel: who, which unit, how much it cost, and how many litres."),
            new("review-fuel", "Review a report", "Open a report to compare the receipt with the trip, then approve or flag it. Only the Manager can do this.", ManagerOnly: true),
            new("resolve-fuel", "Resolve a flag", "Record what was found out about a flagged report to close it. Only the Manager can do this.", ManagerOnly: true),
        ]),

        new("sos-dispatch", 1, "sos",
        [
            new("sos-summary", "Emergencies right now", "The number of open SOS alerts. This page refreshes by itself."),
            new("sos-list", "Alert cards", "Each card shows the driver, the type of emergency, and where they are."),
            new("sos-call", "Call the driver", "Call the driver first, then send help if it is needed."),
            new("sos-resolve", "Mark it resolved", "Tick this once the emergency has been handled. Only the Manager can do this.", ManagerOnly: true),
            new("sos-resolved", "Recently resolved", "Alerts that were resolved in the last 7 days stay here for reference."),
        ]),

        new("audit-logs", 1, "roles",
        [
            new("audit-date", "Choose the dates", "Pick the period you want to look at."),
            new("audit-search", "Search the trail", "Type a name or a word to find an entry."),
            new("audit-table", "Who did what", "Each row shows the time, who did something, and what they did."),
            new("audit-export", "Save a copy", "Download the logs as a CSV or PDF file."),
        ]),
    ];

    /// <summary>The tour with this id, or null.</summary>
    public static PageTour? ById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The tour for a page. A page with tabs passes its own tour id (<paramref name="key"/>);
    /// otherwise the first part of the address picks it ("garage?x=1#top" -> "garage").
    /// </summary>
    public static PageTour? Find(string? key, string? relativePath) => ById(key) ?? ById(RouteOf(relativePath));

    /// <summary>"/garage/", "garage?x=1" and "garage#top" all give "garage".</summary>
    public static string RouteOf(string? relativePath)
    {
        string path = relativePath ?? string.Empty;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        path = path.Trim('/');
        int slash = path.IndexOf('/');
        return slash >= 0 ? path[..slash] : path;
    }
}
