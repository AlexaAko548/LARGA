namespace LARGA.ManagerWeb.Services.Faq;

/// <summary>A group of questions. <see cref="Id"/> is the anchor: /help#ledger scrolls to it.</summary>
public sealed record FaqCategory(string Id, string Title);

/// <summary>
/// One question. <see cref="Answer"/> is plain text: a blank line starts a new paragraph, and a line
/// starting with "• " is shown as a list item. <see cref="ManagerOnly"/> tags answers about things
/// only the Manager can do, so an Assistant Manager understands why a button is missing.
/// <see cref="Keywords"/> are extra words the search should match (no need to show them).
/// </summary>
public sealed record FaqEntry(string Id, string CategoryId, string Question, string Answer, bool ManagerOnly = false, string Keywords = "");

/// <summary>
/// All Help &amp; FAQs content, in one place. To add a question, add a <see cref="FaqEntry"/> to
/// <see cref="Entries"/> using an existing category id. To add a category, add it to
/// <see cref="Categories"/> first. Every number below was checked against the code
/// (ShiftRules, ShiftEligibilityRules, WorkOrderRules, SparePartStockRules, RetentionRules, ...):
/// if a rule changes in the code, change the answer here too.
/// Plain English only - the readers aren't technical.
/// </summary>
public static class FaqCatalog
{
    public static readonly IReadOnlyList<FaqCategory> Categories =
    [
        new("drivers", "Drivers & shifts"),
        new("ledger", "Payments & debt"),
        new("fuel", "Fuel"),
        new("garage", "Garage & spare parts"),
        new("sos", "Map, routes & SOS"),
        new("roles", "Roles, reports & records"),
    ];

    public static readonly IReadOnlyList<FaqEntry> Entries =
    [
        // ---- Drivers & shifts ----
        new("clock-in-blocked", "drivers", "Why can't a driver clock in?",
            """
            A driver can't clock in when any of these is true:

            • It is before 6:00 AM (Philippine time).
            • The driver's license is missing, has expired, or expires within a month.
            • The unit is marked Under Maintenance, or it has a Garage work order in the shop that day.

            If the driver is using a substitute unit, that unit is checked the same way.

            A shift that was never ended stays open. The driver's phone takes them back to that open shift so it can be ended. It closes by itself at 6:00 AM the next day (see "Auto-Closed" below).
            """,
            Keywords: "cannot clock in blocked license maintenance garage open shift 6am"),

        new("approve-deny-clock-in", "drivers", "How do I approve or deny a flagged clock-in?",
            """
            A clock-in is flagged when the driver's check of the unit before the shift reported a problem. It waits under "Clock-in approvals" at the top of Driver & Shifts (and in the Alert Center on the phone).

            Look at the photos and the driver's note, then choose:

            • Approve: the driver's phone clocks in. If the driver or unit is blocked (for example, an expired license), the button is greyed out and the reason is shown.
            • Deny: you must write a note so the driver knows why. You can also send the unit to the Garage so the problem is looked at.
            """,
            ManagerOnly: true, Keywords: "flagged clock-in approval approve deny inspection"),

        new("create-driver-account", "drivers", "How do I create a driver account?",
            """
            Go to Driver & Shifts and press "+ Add New Driver". Fill in the driver's name and phone number. You can also pick a unit and type a temporary password. If you leave the password empty, LARGA makes one for you.

            Give the temporary password to the driver. They must change it the first time they log in. Until they do, they can't use the app.
            """,
            ManagerOnly: true, Keywords: "new driver account register temporary password first login"),

        new("auto-closed", "drivers", "Why does a shift say \"Auto-Closed\"?",
            """
            The driver never clocked out. When a shift is still open at 6:00 AM the next day, LARGA closes it by itself and marks it Auto-Closed. A shift closed this way has no real end time, so check with the driver about what happened.
            """,
            Keywords: "auto closed missed clock out forgot"),

        new("day-offs-substitutes", "drivers", "How do day-offs and substitute units work on the weekly schedule?",
            """
            The Schedule Planner shows one week at a time. Every day starts as a working day.

            • Click a cell to mark that driver off for the day.
            • Or give the driver a different unit for that day. This is also how you lend a unit to a driver whose own unit is in the Garage.
            • A substitute unit must be allowed to go out: it can't be Under Maintenance or in the shop that day.

            Both roles can look at the schedule. Only the Manager can change it.
            """,
            ManagerOnly: true, Keywords: "day off schedule planner substitute unit lend temporary weekly"),

        // ---- Payments & debt ----
        new("amount-due", "ledger", "How is the amount due computed?",
            """
            Amount due = boundary + late fee + low-fuel fee + any debt carried over from earlier days.

            • Boundary: ₱800 a day. (If the owner sets a different boundary in the system settings, that is used instead.)
            • Late fee: ₱100 for each hour, or part of an hour, after 10:00 PM. The first 30 minutes are free, so nothing is charged if the unit is back by 10:29 PM. At 10:30 PM it becomes ₱100, after 11:00 PM ₱200, and so on.
            • Low fuel: ₱50 if the unit comes back with less than half a tank.
            • Carried-over debt: whatever the driver still owes from earlier shifts.

            The late fee is based on when the unit is returned (clock-out), not on when the driver pays.
            """,
            Keywords: "boundary 800 late fee 10pm penalty low fuel half tank debt computed"),

        new("partial-payments", "ledger", "How do partial payments roll over?",
            """
            Each payment is saved as its own record. An earlier payment is never changed.

            A payment goes first to the shift it was handed over for. Anything left over pays the driver's older unpaid shifts, oldest first, then any manual adjustments. If there is still money left, it is kept as advance credit.

            Whatever the driver has not paid stays on the ledger and is added to the next amount due. The Master Debt Ledger shows each driver's total.
            """,
            Keywords: "partial payment roll over carry credit advance balance short"),

        new("debt-flag", "ledger", "What is the 3-consecutive-day debt flag?",
            """
            A driver is flagged when they have gone 3 or more days without paying off a balance. The days are counted from the oldest unpaid shift. The ledger shows how long ago that was ("oldest 3 days ago") and marks it as overdue.
            """,
            Keywords: "debt flag overdue unpaid three days consecutive"),

        new("record-payment", "ledger", "How do I record a cash or GCash payment?",
            """
            Go to Financial Ledger > Daily Settlements and press "Record Payment" on the shift (it says "Add Payment" if part of it is already paid). Choose Cash or GCash, then enter the amount.

            For GCash, add a photo of the e-receipt. LARGA reads the amount, date, reference number and the account it was sent to from the photo. The amount you enter must match the receipt.

            To pay off older debt, use "Settle" on the Master Debt Ledger tab. Both the Manager and the Assistant Manager can record payments.
            """,
            Keywords: "record cash gcash payment receipt settle e-wallet"),

        new("gcash-rejected", "ledger", "Why was a GCash e-receipt rejected?",
            """
            There are two possible reasons:

            • The reference number was already used. Each GCash reference can be recorded only once, so LARGA refuses the payment and saves nothing. (Spaces and dashes in the number are ignored.)
            • The money was sent to an account that isn't one of BLM's authorized accounts. LARGA shows a warning and marks the payment in the ledger. The payment can still be saved, so check it before you do.

            The Manager adds BLM's accounts in the menu at the top right: Settings > Authorized payout accounts.
            """,
            Keywords: "gcash receipt rejected reference number duplicate account mismatch"),

        new("holds-money", "ledger", "Does LARGA hold money?",
            "No. LARGA only records and checks payments. The money itself is handed over in cash or sent to BLM's own GCash or bank account.",
            Keywords: "hold money wallet funds"),

        // ---- Fuel ----
        new("fuel-flagged", "fuel", "Why was a fuel submission flagged?",
            """
            LARGA flags a submission for these reasons:

            • The station isn't Petron or Shell. This is checked when the driver sends the report.
            • The OR (official receipt) number was already used on another report. The report is still saved, but it is flagged.
            • The distance on the odometer doesn't match the distance the GPS recorded. This is flagged on trips of at least 10 km when the difference is more than 8% (and at least 2 km). The owner can change these limits.

            When you review a report, LARGA also shows whether the cost per kilometre looks normal, and a note if the receipt date is not the day it was sent. Those two help you decide, but they don't flag the report by themselves.
            """,
            Keywords: "fuel flagged petron shell receipt duplicate or number odometer gps distance cost"),

        new("manually-edited", "fuel", "What does \"manually edited\" mean on an OCR field?",
            "When a driver photographs a receipt, the app reads the cost, litres, station, date and OR number from the photo (this is called OCR). If the driver typed over something the app read, that field is tagged as manually edited. It is a hint to check that value against the receipt photo.",
            Keywords: "ocr manually edited typed over receipt field"),

        new("fuel-statuses", "fuel", "What are the Pending, Verified and Flagged statuses?",
            """
            • Pending: waiting for a decision.
            • Verified: checked and approved.
            • Flagged: something looks wrong. It stays here until the Manager records how it was resolved, and then it moves to Verified.

            The Manager can also put a Verified or Flagged report back to Pending.
            """,
            Keywords: "pending verified flagged status resolve"),

        // ---- Garage & spare parts ----
        new("work-order-statuses", "garage", "What are the work order statuses?",
            """
            • Reported: a driver's problem report that nobody has looked at yet.
            • Scheduled: booked for a later day in the shop.
            • In Progress: the unit is in the shop now.
            • Resolved: the work is finished.
            • Dismissed: closed without any work being done.

            A unit that is In Progress, or Scheduled for today, can't go out on a shift.
            """,
            Keywords: "work order status reported scheduled in progress resolved dismissed"),

        new("scheduling-choices", "garage", "What are the three scheduling choices?",
            """
            When you book a work order you can choose:

            • Today: the unit goes into the shop right away.
            • Pick a date: you choose the day yourself. LARGA does not check how busy the shop is that day.
            • Earliest free day: LARGA picks the first day from tomorrow that has fewer than 2 jobs.
            """,
            ManagerOnly: true, Keywords: "schedule scheduling today date earliest capacity two jobs book"),

        new("driver-notified", "garage", "How is the driver notified?",
            """
            The driver gets a chat message, which also arrives as a push notification on their phone:

            • when the work order is booked,
            • again 7 days before the shop day (not needed if it was booked a week ahead or less, because they were already told), and
            • straight away if the date changes.

            The message tells them which day their unit is in the shop and that they will be given a temporary unit.
            """,
            Keywords: "driver notified notification push chat message seven days before date change"),

        new("stock-statuses", "garage", "What are the stock statuses?",
            """
            Each part has a minimum amount to keep ("Min stock").

            • Normal: more than the minimum is left.
            • Low Stock: the minimum or less is left.
            • Critical: none left, or half the minimum or less.

            The Spare Parts Inventory page is for the Manager only.
            """,
            Keywords: "stock normal low critical minimum reorder inventory"),

        new("archive-part", "garage", "Why archive a part instead of deleting it?",
            "A part that was used on past work orders must stay on record, with its name and price, so the history still makes sense. Archiving hides it from the list, the Garage parts picker and low-stock alerts. You can bring it back any time with \"Show archived\".",
            ManagerOnly: true, Keywords: "archive delete part restore hidden"),

        // ---- Map, routes & SOS ----
        new("location-stopped", "sos", "Why did a driver's location stop?",
            """
            Location is only tracked during an active shift, and it stops at clock-out. This is on purpose, to protect the driver's privacy. It is also paused while the driver is on a break.

            If the driver is still on shift, the phone may have lost signal or the app may have been closed.
            """,
            Keywords: "location stopped gps tracking map clock out privacy break"),

        new("idle-flags", "sos", "What are idle flags?",
            "A unit on an active shift is shown as Idle when it has not moved for 15 minutes. (The owner can change this time in the system settings.) It is a prompt to check on the driver, not a fault.",
            Keywords: "idle flag stopped not moving fifteen minutes"),

        new("sos-types", "sos", "What are the Standard, Hostile and Crash SOS types?",
            """
            • Standard Breakdown: the driver sends it by hand, for example for a breakdown or an emergency.
            • Hostile Protocol: a silent alert for a dangerous situation. The driver sends it without any sign on the screen, using a secret button pattern or the emergency PIN at clock-out.
            • Crash Protocol: the phone notices a sudden impact and sends the alert by itself.
            """,
            Keywords: "sos standard breakdown hostile crash emergency types"),

        new("resolve-sos", "sos", "How do I resolve an SOS alert?",
            """
            Open SOS Dispatch. Call the driver with "Call driver", send help if it's needed, then tick "Resolved" on the alert. If there are two or more open alerts you can use "Mark all as resolved".

            Resolved alerts stay at the bottom of the page for 7 days.
            """,
            ManagerOnly: true, Keywords: "resolve sos alert dispatch call driver"),

        // ---- Roles, reports & records ----
        new("assistant-manager", "roles", "What can the Assistant Manager do?",
            """
            The Assistant Manager can look at every page except Audit Logs and Spare Parts Inventory, message and call drivers, and record payments.

            Only the Manager can:
            • open Audit Logs or Spare Parts Inventory,
            • approve or deny clock-ins,
            • edit the schedule or driver accounts,
            • add debt adjustments (and export the ledger),
            • resolve fuel flags or SOS alerts,
            • manage work orders,
            • generate reports,
            • change the settings for payout accounts and the emergency PIN, and manage who can use the dashboard.
            """,
            Keywords: "assistant manager permissions role cannot can access"),

        new("standings-not-editable", "roles", "Why can't driver standings be edited?",
            "The standings are worked out automatically from each driver's recent shifts, so they can't be changed by hand. They cover the period shown next to \"Top Driver Standings\" on the Dashboard (currently the last 30 days).",
            Keywords: "standings leaderboard edit computed"),

        new("export-reports", "roles", "How do I export reports to PDF or CSV?",
            """
            • Dashboard: press "Generate Reports", pick the dates and choose PDF or CSV.
            • Audit Logs: use the Export CSV or PDF buttons above the table.
            • Financial Ledger: use the CSV export button.

            All of these are for the Manager only.
            """,
            ManagerOnly: true, Keywords: "export report pdf csv download generate"),

        new("record-retention", "roles", "How long are records kept?",
            """
            • Driver location trails (GPS): 90 days.
            • SOS alerts and chat messages: 12 months.

            These times are set in LARGA. Records are only deleted once the automatic clean-up is switched on in Firebase, which is done by whoever looks after the system. For other records, such as audit logs and photos, no automatic deletion time is set in LARGA.
            """,
            Keywords: "retention how long kept records logs photos gps delete expire privacy"),
    ];
}
