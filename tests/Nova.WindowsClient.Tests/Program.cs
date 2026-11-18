using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CommunityToolkit.WinUI.Notifications;
using Nova.WindowsClient;
using Nova.WindowsClient.Services;

if (args.Contains("--windows-toast-smoke", StringComparer.Ordinal))
{
    var testTrain = new ScheduledTrain(
        new TimeOnly(23, 59),
        new TimeOnly(0, 29),
        1,
        "TEST");
    var testDate = new DateOnly(2026, 11, 18);
    var testId = TrainDepartureReminder.CreateId(testDate, testTrain);
    new WindowsToastNotificationService().Show(new TrainDepartureReminder(
        testId,
        "Проверка Windows Toast · NOVA",
        testDate,
        testTrain.Departure,
        testTrain.Arrival,
        testTrain.ArrivalDayOffset,
        testTrain.TrainNumber,
        1,
        true,
        false,
        null));

    var testTag = testId[..16];
    var found = ToastNotificationManagerCompat.History.GetHistory()
        .Any(notification => notification.Tag == testTag && notification.Group == "NOVA-Train");
    Require(found, "Windows did not record the NOVA test toast in notification history.");
    Console.WriteLine("PASS  Windows Toast was accepted and appears in notification history.");
    return 0;
}

var passed = 0;
var failed = 0;

Run("silence keeps all audio bands at rest", TestSilence);
Run("bass and treble energy are separated", TestFrequencyBands);
Run("louder audio produces a stronger level", TestVolumeResponse);
Run("sharp peaks swell briefly and decay", TestTransientDecay);
Run("local schedule parser skips invalid and duplicate rows", TestLocalScheduleParsing);
Run("local schedule selects the three nearest weekday trains", TestWeekdayScheduleSelection);
Run("local schedule uses weekend data after the Moscow date changes", TestWeekendAndMidnightScheduleSelection);
Run("local schedule handles arrivals after midnight and unverified data", TestScheduleMidnightAndUnverified);
Run("partial local schedule is displayed without confirmed validity dates", TestPartialScheduleDisplay);
Run("production schedule resource loads and returns real listed departures", TestProductionScheduleResource);
Run("the schedule picker exposes every departure for its date", TestAllTrainsForDate);
Run("reminder presets and custom minutes are validated", TestReminderLeadOptionsAndValidation);
Run("reminders persist, update, disable, re-enable, and delete", TestReminderPersistenceAndManagement);
Run("a due reminder is delivered once, including after restart", TestReminderIsDeliveredOnce);
Run("simultaneous reminders are delivered independently", TestSimultaneousReminders);
Run("missed and departed reminders never fire late", TestMissedAndDepartedReminders);
Run("Moscow reminder times roll correctly across midnight", TestReminderMidnightRollover);
Run("Yandex LRC parser preserves timestamps and untimed text", TestYandexLrcParser);
Run("Yandex lyrics failures map to distinct user-visible states", TestYandexLyricsFailureClassification);
Run("OBS WebSocket authentication vector is stable", TestObsAuthentication);
Run("OBS installation discovery never launches the program", TestObsInstallationDiscovery);
await RunAsync("OBS WebSocket reads state/scenes and sends recording/scene commands", TestObsWebSocketProtocol);
Run("browser adaptation finds a non-overlapping window position", TestBrowserWindowGeometry);
Run("dock geometry respects edge orientation and snap threshold", TestPanelDockGeometry);
Run("widget icon size stays within its DIP range and resizes navigation buttons", TestWidgetIconSize);
Run("screen text cleanup preserves paragraphs and merges wrapped lines", TestScreenTextCleanup);
await RunAsync("screen translation rejects empty and oversized input before sending it", TestTranslationLimits);
Run("default Windows output volume is readable", TestSystemMasterVolume);
await RunAsync("Windows OCR recognizes an in-memory text image", TestWindowsOcr);
await RunAsync("WASAPI loopback capture starts without recording audio", TestLiveLoopback);
Run("analysis throughput stays within the realtime budget", TestThroughput);

Console.WriteLine($"Итог: успешно {passed}, ошибок {failed}.");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS  {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
        failed++;
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS  {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
        failed++;
    }
}

void TestSilence()
{
    var analyzer = new AudioBandAnalyzer();
    var sample = ProcessBlock(analyzer, new float[480]);
    Require(sample.Level == 0 && sample.Bass == 0 && sample.Treble == 0 && sample.Beat == 0,
        "silence produced non-zero animation input");
}

void TestFrequencyBands()
{
    var bass = AnalyzeTone(60, 0.8f);
    var treble = AnalyzeTone(7000, 0.8f);
    Require(bass.Bass > bass.Treble, $"60 Hz was not bass-dominant ({bass.Bass:0.00}/{bass.Treble:0.00})");
    Require(treble.Treble > treble.Bass, $"7 kHz was not treble-dominant ({treble.Bass:0.00}/{treble.Treble:0.00})");
}

void TestVolumeResponse()
{
    var quiet = AnalyzeTone(440, 0.15f).Level;
    var loud = AnalyzeTone(440, 0.75f).Level;
    Require(loud > quiet + 0.25f, $"volume response was too small ({quiet:0.00} -> {loud:0.00})");
}

void TestTransientDecay()
{
    var analyzer = new AudioBandAnalyzer();
    for (var index = 0; index < 12; index++)
        ProcessBlock(analyzer, new float[480]);

    var impulse = new float[480];
    impulse[0] = 1;
    var peak = ProcessBlock(analyzer, impulse).Beat;
    Require(peak > 0.1f, "a sharp peak did not create a pulse");

    var settled = 0f;
    for (var index = 0; index < 100; index++)
        settled = ProcessBlock(analyzer, new float[480]).Beat;
    Require(settled < peak * 0.05f, $"pulse did not decay ({peak:0.00} -> {settled:0.00})");
}

void TestLocalScheduleParsing()
{
    var schedule = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "09:00 | 09:30 | 6501\n" +
        "09:00 | 09:30 | 6501\n" +
        "25:00 | 25:30 | 6502\n" +
        "10:00 | 10:30 | 6503",
        "09:15 | 09:45 | 6504"));

    Require(schedule.IsVerified && schedule.Error is null,
        $"valid metadata was rejected: {schedule.Error}");
    Require(schedule.Weekdays.Count == 2 && schedule.Weekends.Count == 1,
        "invalid and duplicate rows were not skipped independently");
    Require(schedule.Warnings.Count == 2,
        $"expected two row warnings, got {schedule.Warnings.Count}");

    var repeatedSection = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "09:00 | 09:30 | 6501\n[WEEKDAYS]\n10:00 | 10:30 | 6502",
        "09:15 | 09:45 | 6504"));
    Require(repeatedSection.Weekdays.Count == 1 &&
        repeatedSection.Warnings.Any(warning => warning.Contains("повторная секция", StringComparison.OrdinalIgnoreCase)),
        "records from a repeated section were not skipped");

    var malformed = LocalTrainScheduleService.Parse("# NOVA LOCAL SCHEDULE\n[WEEKDAYS]\n");
    var malformedView = LocalTrainScheduleService.GetUpcoming(malformed, DateTimeOffset.UtcNow);
    Require(malformedView.State == TrainScheduleState.Invalid,
        "a file missing required metadata did not produce an invalid state");
}

void TestWeekdayScheduleSelection()
{
    var schedule = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "12:00 | 12:30 | 6504\n" +
        "09:00 | 09:30 | 6501\n" +
        "11:00 | 11:30 | 6503\n" +
        "10:00 | 10:30 | 6502",
        "08:00 | 08:30 | 6599"));

    var view = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 18, 5, 30, 0, TimeSpan.Zero));
    Require(view.State == TrainScheduleState.Available && !view.IsWeekend,
        $"weekday schedule was not selected: {view.State}");
    Require(view.Upcoming.Count == 3 &&
        view.Upcoming.Select(train => train.TrainNumber).SequenceEqual(["6501", "6502", "6503"]),
        "the three nearest departures were not sorted or selected");

    var later = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 18, 7, 30, 0, TimeSpan.Zero));
    Require(later.Upcoming.Select(train => train.TrainNumber).SequenceEqual(["6503", "6504"]),
        "departed trains were not filtered or the remaining trains were not retained");
}

void TestWeekendAndMidnightScheduleSelection()
{
    var schedule = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "09:00 | 09:30 | 6501",
        "09:15 | 09:45 | 6601\n10:15 | 10:45 | 6602"));
    var weekend = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero));
    Require(weekend.IsWeekend && weekend.Upcoming[0].TrainNumber == "6601",
        "the weekend schedule was not selected using Moscow local time");

    var fridayLate = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 20, 20, 59, 0, TimeSpan.Zero));
    var saturdayEarly = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 20, 21, 0, 0, TimeSpan.Zero));
    Require(!fridayLate.IsWeekend && fridayLate.State == TrainScheduleState.NoUpcomingTrains &&
        saturdayEarly.IsWeekend && saturdayEarly.Upcoming[0].TrainNumber == "6601",
        "the selected day type did not roll over at midnight in Europe/Moscow");
}

void TestScheduleMidnightAndUnverified()
{
    var overnight = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "23:50 | 00:20 | 6701",
        "23:40 | 00:10 | 6702"));
    var late = LocalTrainScheduleService.GetUpcoming(
        overnight,
        new DateTimeOffset(2026, 11, 20, 20, 30, 0, TimeSpan.Zero));
    Require(late.Upcoming.Count == 1 && late.Upcoming[0].ArrivalDayOffset == 1,
        "an arrival after midnight was not marked as occurring on the following day");

    var unverified = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "09:00 | 09:30 | TEST1",
        "09:15 | 09:45 | TEST2",
        dataStatus: "UNVERIFIED"));
    var view = LocalTrainScheduleService.GetUpcoming(
        unverified,
        new DateTimeOffset(2026, 11, 18, 5, 0, 0, TimeSpan.Zero));
    Require(view.State == TrainScheduleState.Unverified && view.Upcoming.Count == 0,
        "unverified train rows were exposed as operational schedule data");
}

void TestPartialScheduleDisplay()
{
    const string note = "Weekday comparison and Saturday sample.";
    var partial = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "11:07 | 11:22 | 6330\n12:05 | 12:20 | 6530\n14:21 | 14:36 | 6346",
        "05:38 | 05:53 | 6502",
        dataStatus: "PARTIAL",
        effectiveFrom: "NOT_CONFIRMED",
        effectiveThrough: "NOT_CONFIRMED",
        dataNote: note));

    Require(partial.Error is null && partial.IsPartial && !partial.IsVerified,
        $"partial metadata was rejected: {partial.Error}");
    var view = LocalTrainScheduleService.GetUpcoming(
        partial,
        new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero));
    Require(view.State == TrainScheduleState.PartiallyAvailable &&
        view.IsPartial && view.Upcoming.Count == 3 && view.DataNote == note,
        "available partial rows were not returned to the widget");

    var empty = LocalTrainScheduleService.Parse(CreateScheduleFixture(
        "", "", dataStatus: "PARTIAL",
        effectiveFrom: "NOT_CONFIRMED",
        effectiveThrough: "NOT_CONFIRMED"));
    var emptyView = LocalTrainScheduleService.GetUpcoming(empty, DateTimeOffset.UtcNow);
    Require(emptyView.State == TrainScheduleState.NoScheduleData,
        "an empty schedule was misreported as merely unverified");

    var missing = LocalTrainScheduleService.Load(
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt"));
    Require(LocalTrainScheduleService.GetUpcoming(missing, DateTimeOffset.UtcNow).State == TrainScheduleState.Invalid,
        "a missing schedule file did not produce a load error");
}

void TestProductionScheduleResource()
{
    var path = Path.Combine(
        Directory.GetCurrentDirectory(),
        "assets",
        "schedules",
        "tsarskoe_selo_kupchino.txt");
    var schedule = LocalTrainScheduleService.Load(path);
    var counts = $"weekdays={schedule.Weekdays.Count}, weekends={schedule.Weekends.Count}, " +
        $"Saturday={schedule.Saturdays.Count}, Sunday={schedule.Sundays.Count}";
    Require(schedule.Error is null && schedule.IsPartial &&
        schedule.Weekdays.Count == 53 &&
        schedule.Weekends.Count == 0 &&
        schedule.Saturdays.Count == 48 &&
        schedule.Sundays.Count == 48,
        $"the production resource was not loaded as expected: {schedule.Error ?? counts}");

    var weekday = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero));
    var weekdayNumbers = string.Join(", ", weekday.Upcoming.Select(train => train.TrainNumber));
    Require(weekday.State == TrainScheduleState.PartiallyAvailable &&
        weekday.Upcoming.Select(train => train.TrainNumber).SequenceEqual(["6756", "6322", "6330"]),
        $"the production weekday list did not produce its next three departures ({weekday.State}: {weekdayNumbers})");

    var weekend = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero));
    var weekendNumbers = string.Join(", ", weekend.Upcoming.Select(train => train.TrainNumber));
    Require(weekend.State == TrainScheduleState.PartiallyAvailable &&
        weekend.Upcoming.Select(train => train.TrainNumber).SequenceEqual(["6404", "6520", "6522"]),
        $"the production Saturday list did not produce its next three departures ({weekend.State}: {weekendNumbers})");

    var sunday = LocalTrainScheduleService.GetUpcoming(
        schedule,
        new DateTimeOffset(2026, 11, 22, 6, 0, 0, TimeSpan.Zero));
    var sundayNumbers = string.Join(", ", sunday.Upcoming.Select(train => train.TrainNumber));
    Require(sunday.State == TrainScheduleState.PartiallyAvailable &&
        sunday.Upcoming.Select(train => train.TrainNumber).SequenceEqual(["6520", "6404", "6318"]),
        $"the production Sunday list did not produce its next three departures ({sunday.State}: {sundayNumbers})");
}

void TestAllTrainsForDate()
{
    var schedule = LocalTrainScheduleService.Load(Path.Combine(
        Directory.GetCurrentDirectory(),
        "assets",
        "schedules",
        "tsarskoe_selo_kupchino.txt"));
    var day = LocalTrainScheduleService.GetTrainsForDate(schedule, new DateOnly(2026, 11, 18));
    Require(day.State == TrainScheduleState.PartiallyAvailable && day.Trains.Count == 53,
        $"the full weekday schedule was not exposed ({day.State}, {day.Trains.Count} trains)");
    Require(day.Trains.Count > LocalTrainScheduleService.GetUpcoming(
            schedule,
            new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero)).Upcoming.Count,
        "the all-trains view was incorrectly limited to the main list's three departures");
}

void TestReminderLeadOptionsAndValidation()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var scheduler = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        scheduler.Initialize();
        var now = MoscowUtc(new DateOnly(2026, 11, 19), new TimeOnly(11, 0));
        var options = new[]
        {
            (new DateOnly(2026, 11, 19), CreateTestTrain("12:00", "T10"), 10),
            (new DateOnly(2026, 11, 19), CreateTestTrain("13:00", "T20"), 20),
            (new DateOnly(2026, 11, 19), CreateTestTrain("14:00", "T30"), 30),
            (new DateOnly(2026, 11, 19), CreateTestTrain("15:00", "T45"), 45),
            (new DateOnly(2026, 11, 19), CreateTestTrain("12:00", "T1"), 1),
            (new DateOnly(2026, 11, 20), CreateTestTrain("15:00", "TMAX"), 1440)
        };
        foreach (var (date, train, lead) in options)
            Require(scheduler.SetReminder(date, train, lead, now) == TrainReminderOperationResult.Saved,
                $"valid {lead}-minute reminder was rejected");
        Require(scheduler.Reminders.Select(reminder => reminder.LeadMinutes)
                .Order().SequenceEqual([1, 10, 20, 30, 45, 1440]),
            "preset and custom lead times were not stored exactly");

        foreach (var valid in new[] { "1", "10", "20", "30", "1440" })
            Require(TrainReminderScheduler.TryParseLeadMinutes(valid, out var parsed) &&
                parsed == int.Parse(valid, CultureInfo.InvariantCulture),
                $"valid lead time {valid} was rejected");
        foreach (var invalid in new string?[] { null, "", "0", "1441", "-1", "+10", "1.5", "1 0" })
            Require(!TrainReminderScheduler.TryParseLeadMinutes(invalid, out _),
                $"invalid lead time '{invalid}' was accepted");

        var rejectedZero = false;
        try { scheduler.SetReminder(new DateOnly(2026, 11, 19), CreateTestTrain("16:00", "TZERO"), 0, now); }
        catch (ArgumentOutOfRangeException) { rejectedZero = true; }
        Require(rejectedZero, "the scheduler accepted zero minutes");
        var rejectedLarge = false;
        try { scheduler.SetReminder(new DateOnly(2026, 11, 19), CreateTestTrain("16:00", "TOVER"), 1441, now); }
        catch (ArgumentOutOfRangeException) { rejectedLarge = true; }
        Require(rejectedLarge, "the scheduler accepted more than 1440 minutes");
    });
}

void TestReminderPersistenceAndManagement()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var date = new DateOnly(2026, 11, 19);
        var train = CreateTestTrain("15:00", "TSTORE");
        var now = MoscowUtc(date, new TimeOnly(11, 0));
        var first = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        first.Initialize();
        Require(first.SetReminder(date, train, 10, now) == TrainReminderOperationResult.Saved,
            "initial reminder was not saved");
        Require(first.SetReminder(date, train, 20, now) == TrainReminderOperationResult.Saved &&
            first.Reminders.Count == 1 && first.Reminders[0].LeadMinutes == 20,
            "selecting the same train created a duplicate instead of updating it");

        var restarted = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        restarted.Initialize();
        var stored = restarted.Reminders.Single();
        Require(stored.TrainNumber == "TSTORE" && stored.Direction == TrainDepartureReminder.Route &&
            stored.DepartureDate == date && stored.DepartureTime == train.Departure &&
            stored.LeadMinutes == 20 && stored.Enabled && !stored.NotificationSent,
            "the reminder did not survive reloading its persistent store");
        Require(restarted.SetEnabled(stored.Id, false, now) == TrainReminderOperationResult.Saved &&
            !restarted.Reminders.Single().Enabled,
            "the reminder could not be disabled");
        Require(restarted.SetEnabled(stored.Id, true, now) == TrainReminderOperationResult.Saved &&
            restarted.Reminders.Single().Enabled,
            "the reminder could not be re-enabled before its due time");
        Require(restarted.Remove(stored.Id), "the reminder could not be deleted");
        var afterDelete = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        afterDelete.Initialize();
        Require(afterDelete.Reminders.Count == 0, "the deleted reminder reappeared after reload");
    });
}

void TestReminderIsDeliveredOnce()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var scheduler = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        scheduler.Initialize();
        var date = new DateOnly(2026, 11, 18);
        var train = CreateTestTrain("07:00", "TONCE");
        var now = MoscowUtc(date, new TimeOnly(6, 0));
        Require(scheduler.SetReminder(date, train, 20, now) == TrainReminderOperationResult.Saved,
            "the one-time reminder was not created");

        var notifier = new RecordingTrainReminderNotifier();
        var due = MoscowUtc(date, new TimeOnly(6, 40));
        var restartedBeforeDue = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        restartedBeforeDue.Initialize();
        Require(restartedBeforeDue.ProcessDue(due, notifier) == 1 && notifier.Notifications.Count == 1,
            "the due reminder was not delivered");
        Require(restartedBeforeDue.ProcessDue(due, notifier) == 0,
            "the same due reminder was delivered twice in one process");

        var restartedAfterDue = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        restartedAfterDue.Initialize();
        Require(restartedAfterDue.ProcessDue(due, notifier) == 0 && notifier.Notifications.Count == 1 &&
            restartedAfterDue.Reminders.Single().NotificationSent &&
            restartedAfterDue.Reminders.Single().NotificationSentAt == due,
            "a sent reminder was repeated after restarting the scheduler");
    });
}

void TestSimultaneousReminders()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var scheduler = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        scheduler.Initialize();
        var date = new DateOnly(2026, 11, 18);
        var now = MoscowUtc(date, new TimeOnly(6, 0));
        var first = CreateTestTrain("07:00", "TSAME1");
        var second = CreateTestTrain("07:00", "TSAME2");
        var disabled = CreateTestTrain("07:30", "TDISABLED");
        Require(scheduler.SetReminder(date, first, 20, now) == TrainReminderOperationResult.Saved &&
            scheduler.SetReminder(date, second, 20, now) == TrainReminderOperationResult.Saved &&
            scheduler.SetReminder(date, disabled, 10, now) == TrainReminderOperationResult.Saved,
            "multiple reminders were not stored");
        var disabledId = TrainDepartureReminder.CreateId(date, disabled);
        Require(scheduler.SetEnabled(disabledId, false, now) == TrainReminderOperationResult.Saved,
            "the third reminder was not disabled");

        var notifier = new RecordingTrainReminderNotifier();
        var due = MoscowUtc(date, new TimeOnly(6, 40));
        Require(scheduler.ProcessDue(due, notifier) == 2 &&
            notifier.Notifications.Select(reminder => reminder.TrainNumber)
                .Order().SequenceEqual(["TSAME1", "TSAME2"]),
            "simultaneous enabled reminders were not both delivered");
        Require(scheduler.ProcessDue(due, notifier) == 0 && notifier.Notifications.Count == 2,
            "simultaneous reminders were repeated");
    });
}

void TestMissedAndDepartedReminders()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var scheduler = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        scheduler.Initialize();
        var date = new DateOnly(2026, 11, 18);
        var now = MoscowUtc(date, new TimeOnly(6, 0));
        var missed = CreateTestTrain("07:00", "TMISSED");
        Require(scheduler.SetReminder(date, missed, 20, now) == TrainReminderOperationResult.Saved,
            "the missed reminder fixture was not saved");
        var afterNotify = MoscowUtc(date, new TimeOnly(6, 42));
        var notifier = new RecordingTrainReminderNotifier();
        Require(scheduler.ProcessDue(afterNotify, notifier) == 0 &&
            notifier.Notifications.Count == 0 &&
            !scheduler.Reminders.Single(reminder => reminder.TrainNumber == "TMISSED").Enabled,
            "a reminder missed by more than the delivery tolerance was sent late");
        Require(scheduler.SetReminder(date, CreateTestTrain("07:00", "TTOOLATE"), 20, afterNotify) ==
            TrainReminderOperationResult.NotificationTimePassed,
            "a newly configured, already-overdue reminder was not rejected");

        var departed = CreateTestTrain("07:30", "TDEPARTED");
        Require(scheduler.SetReminder(date, departed, 10, now) == TrainReminderOperationResult.Saved,
            "the departed-train fixture was not saved");
        Require(scheduler.ProcessDue(MoscowUtc(date, new TimeOnly(7, 31)), notifier) == 0 &&
            notifier.Notifications.Count == 0 &&
            !scheduler.Reminders.Single(reminder => reminder.TrainNumber == "TDEPARTED").Enabled,
            "a reminder for a departed train was sent");
    });
}

void TestReminderMidnightRollover()
{
    WithTempReminderStore(directory =>
    {
        var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var scheduler = new TrainReminderScheduler(new TrainReminderStore(directory), timeZone);
        scheduler.Initialize();
        var departureDate = new DateOnly(2026, 11, 19);
        var train = CreateTestTrain("00:15", "TMIDNIGHT");
        var now = MoscowUtc(new DateOnly(2026, 11, 18), new TimeOnly(23, 0));
        Require(scheduler.SetReminder(departureDate, train, 30, now) == TrainReminderOperationResult.Saved,
            "the after-midnight train reminder was not created");
        var reminder = scheduler.Reminders.Single();
        var notifyAtLocal = scheduler.GetNotifyAtLocal(reminder);
        Require(DateOnly.FromDateTime(notifyAtLocal.DateTime) == new DateOnly(2026, 11, 18) &&
            TimeOnly.FromDateTime(notifyAtLocal.DateTime) == new TimeOnly(23, 45),
            "the notification time did not roll back to the previous Moscow date");
        var notifier = new RecordingTrainReminderNotifier();
        Require(scheduler.ProcessDue(MoscowUtc(new DateOnly(2026, 11, 18), new TimeOnly(23, 45)), notifier) == 1,
            "the previous-day notification was not delivered");

        var secondTrain = CreateTestTrain("00:45", "TNEXTDAY");
        Require(scheduler.SetReminder(departureDate, secondTrain, 30, now) == TrainReminderOperationResult.Saved,
            "the next-day reminder fixture was not saved");
        Require(scheduler.ProcessDue(MoscowUtc(departureDate, new TimeOnly(00, 15)), notifier) == 1,
            "the notification was not delivered after the local date changed");
    });
}

void WithTempReminderStore(Action<string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), $"NOVA-Reminder-Tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        test(directory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

ScheduledTrain CreateTestTrain(string departure, string trainNumber, string? arrival = null)
{
    var departureTime = TimeOnly.ParseExact(departure, "HH:mm", CultureInfo.InvariantCulture);
    var arrivalTime = TimeOnly.ParseExact(
        arrival ?? TimeOnly.FromTimeSpan(departureTime.ToTimeSpan().Add(TimeSpan.FromMinutes(30)))
            .ToString("HH:mm", CultureInfo.InvariantCulture),
        "HH:mm",
        CultureInfo.InvariantCulture);
    return new ScheduledTrain(
        departureTime,
        arrivalTime,
        arrivalTime < departureTime ? 1 : 0,
        trainNumber);
}

DateTimeOffset MoscowUtc(DateOnly date, TimeOnly time)
{
    var timeZone = LocalTrainScheduleService.GetMoscowTimeZone()
        ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
    var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
}

string CreateScheduleFixture(
    string weekdayRows,
    string weekendRows,
    string dataStatus = "VERIFIED",
    string effectiveFrom = "2026-01-01",
    string effectiveThrough = "2027-12-31",
    string dataNote = "") =>
    $"# NOVA LOCAL SCHEDULE\n" +
    $"# FORMAT_VERSION: 1\n" +
    $"# ROUTE: TSARSKOE_SELO -> KUPCHINO\n" +
    $"# TIMEZONE: Europe/Moscow\n" +
    $"# DATA_STATUS: {dataStatus}\n" +
    $"# EFFECTIVE_FROM: {effectiveFrom}\n" +
    $"# EFFECTIVE_THROUGH: {effectiveThrough}\n" +
    $"# DATA_NOTE: {dataNote}\n" +
    $"[WEEKDAYS]\n{weekdayRows}\n" +
    $"[WEEKENDS]\n{weekendRows}\n";

void TestYandexLrcParser()
{
    var timed = YandexMusicLyricsService.ParseLrc(
        "[ar:Artist]\r\n[00:02.30][00:03.5]Second line\r\n[00:01.2]First line");
    Require(timed.Lines.Count == 3, $"expected three timed entries, got {timed.Lines.Count}");
    Require(timed.Lines[0].Text == "First line" &&
        Math.Abs(timed.Lines[0].Position.TotalMilliseconds - 1200) < 1,
        "single-digit fractional timestamp was parsed incorrectly");
    Require(timed.Lines[1].Text == "Second line" &&
        Math.Abs(timed.Lines[1].Position.TotalMilliseconds - 2300) < 1,
        "two-digit fractional timestamp was parsed incorrectly");
    Require(Math.Abs(timed.Lines[2].Position.TotalMilliseconds - 3500) < 1,
        "multiple timestamps on one line were not expanded");
    Require(timed.PlainText.Length == 0, "metadata or timed lines leaked into plain text");

    var plain = YandexMusicLyricsService.ParseLrc("[ar:Artist]\nFirst plain line\nSecond plain line");
    Require(plain.Lines.Count == 0 && plain.PlainText == $"First plain line{Environment.NewLine}Second plain line",
        "untimed lyrics were not retained as plain text");

    var empty = YandexMusicLyricsService.ParseLrc("");
    Require(empty.Lines.Count == 0 && empty.PlainText.Length == 0,
        "an empty LRC response was not represented as an absent lyric");
}

void TestYandexLyricsFailureClassification()
{
    Require(
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.Unauthorized, YandexLyricsRequestStage.Search) ==
            YandexLyricsFailureKind.Authorization &&
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.Forbidden, YandexLyricsRequestStage.Download) ==
            YandexLyricsFailureKind.Authorization,
        "authorization responses did not map to the authorization state");
    Require(
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.NotFound, YandexLyricsRequestStage.Search) ==
            YandexLyricsFailureKind.TrackNotMatched &&
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.NotFound, YandexLyricsRequestStage.Lyrics) ==
            YandexLyricsFailureKind.NoLyrics &&
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.NotFound, YandexLyricsRequestStage.Download) ==
            YandexLyricsFailureKind.Api,
        "404 responses were not distinguished by request stage");
    Require(
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.TooManyRequests, YandexLyricsRequestStage.Lyrics) ==
            YandexLyricsFailureKind.Api &&
        YandexMusicLyricsService.ClassifyHttpFailure(HttpStatusCode.ServiceUnavailable, YandexLyricsRequestStage.Download) ==
            YandexLyricsFailureKind.Api &&
        YandexMusicLyricsService.ClassifyHttpFailure((HttpStatusCode?)null, YandexLyricsRequestStage.Lyrics) ==
            YandexLyricsFailureKind.Network,
        "API and network failures were not kept distinct from missing lyrics");
    Require(
        YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.Authorization).Contains("авторизации", StringComparison.OrdinalIgnoreCase) &&
        YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.TrackNotMatched).Contains("не сопоставлен", StringComparison.OrdinalIgnoreCase) &&
        YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.NoLyrics).Contains("не найден", StringComparison.OrdinalIgnoreCase) &&
        YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.Network).Contains("сети", StringComparison.OrdinalIgnoreCase),
        "failure categories did not produce concrete user-facing explanations");
}

void TestSystemMasterVolume()
{
    var level = new SystemMasterVolumeService().ReadLevel();
    Require(float.IsFinite(level) && level is >= 0 and <= 1,
        $"Windows returned an invalid master volume value ({level})");
}

void TestObsAuthentication()
{
    var actual = ObsWebSocketClient.CreateAuthentication("NOVA test", "salt", "challenge");
    Require(actual == "4/3wXlkcCM39HZIII2wzSqwbbdij+joGPOJgquwtZsA=",
        "OBS WebSocket authentication did not match the v5 test vector");
}

void TestObsInstallationDiscovery()
{
    var executable = ObsInstallationLocator.FindExecutable();
    if (executable is null)
    {
        Console.WriteLine("      OBS Studio is not installed; discovery returned no executable");
        return;
    }

    Require(File.Exists(executable), $"OBS discovery returned a missing executable: {executable}");
    Console.WriteLine($"      detected OBS executable without launching: {executable}");
}

async Task TestObsWebSocketProtocol()
{
    using var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();

    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var peer = RunObsProtocolPeerAsync(listener);
    try
    {
        await using var client = await ObsWebSocketClient.ConnectAsync("test-password", port, CancellationToken.None);
        var snapshot = await client.ReadSnapshotAsync(CancellationToken.None);
        Require(snapshot.Version == "32.2.2" && snapshot.CurrentScene == "Scene One" &&
            snapshot.Scenes.SequenceEqual(["Scene One", "Scene Two"]) && !snapshot.IsRecording,
            "OBS state or scene list was not parsed correctly");

        await client.SetProgramSceneAsync("Scene Two", CancellationToken.None);
        await client.StartRecordingAsync(CancellationToken.None);
        var recording = await client.ReadSnapshotAsync(CancellationToken.None);
        Require(recording.CurrentScene == "Scene Two" && recording.IsRecording,
            "OBS scene selection or recording start did not take effect");

        await client.StopRecordingAsync(CancellationToken.None);
        var stopped = await client.ReadSnapshotAsync(CancellationToken.None);
        Require(!stopped.IsRecording, "OBS recording stop did not take effect");
    }
    finally
    {
        listener.Close();
    }
    await peer;
}

async Task RunObsProtocolPeerAsync(HttpListener listener)
{
    var context = await listener.GetContextAsync();
    using var webSocket = (await context.AcceptWebSocketAsync(null)).WebSocket;
    await SendObsMessageAsync(webSocket, new
    {
        op = 0,
        d = new
        {
            obsWebSocketVersion = "5.5.0",
            rpcVersion = 1,
            authentication = new { salt = "test-salt", challenge = "test-challenge" }
        }
    });

    using var identify = await ReceiveObsMessageAsync(webSocket)
        ?? throw new InvalidDataException("OBS client closed before Identify.");
    var identifyData = identify.RootElement.GetProperty("d");
    var expected = ObsWebSocketClient.CreateAuthentication("test-password", "test-salt", "test-challenge");
    Require(identifyData.GetProperty("authentication").GetString() == expected,
        "OBS WebSocket sent an incorrect authentication response");
    await SendObsMessageAsync(webSocket, new { op = 2, d = new { negotiatedRpcVersion = 1 } });

    var currentScene = "Scene One";
    var recording = false;
    while (webSocket.State == WebSocketState.Open)
    {
        using var request = await ReceiveObsMessageAsync(webSocket);
        if (request is null) break;
        var data = request.RootElement.GetProperty("d");
        var requestType = data.GetProperty("requestType").GetString()!;
        var requestId = data.GetProperty("requestId").GetString()!;
        object responseData = requestType switch
        {
            "GetVersion" => new { obsVersion = "32.2.2" },
            "GetRecordStatus" => new { outputActive = recording, outputState = recording ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED" },
            "GetCurrentProgramScene" => new { currentProgramSceneName = currentScene },
            "GetSceneList" => new { scenes = new[] { new { sceneName = "Scene One" }, new { sceneName = "Scene Two" } } },
            "SetCurrentProgramScene" => SetScene(data, ref currentScene),
            "StartRecord" => SetRecording(true, ref recording),
            "StopRecord" => SetRecording(false, ref recording),
            _ => throw new InvalidOperationException($"Unexpected OBS request: {requestType}")
        };
        await SendObsMessageAsync(webSocket, new
        {
            op = 7,
            d = new
            {
                requestType,
                requestId,
                requestStatus = new { result = true, code = 100, comment = "" },
                responseData
            }
        });
    }
}

object SetScene(JsonElement data, ref string currentScene)
{
    currentScene = data.GetProperty("requestData").GetProperty("sceneName").GetString()!;
    return new { };
}

object SetRecording(bool value, ref bool recording)
{
    recording = value;
    return new { };
}

async Task<JsonDocument?> ReceiveObsMessageAsync(WebSocket webSocket)
{
    using var buffer = new MemoryStream();
    var bytes = new byte[8192];
    WebSocketReceiveResult result;
    do
    {
        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test peer closed", CancellationToken.None);
            return null;
        }
        buffer.Write(bytes, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
}

Task SendObsMessageAsync(WebSocket webSocket, object message)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
    return webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

void TestBrowserWindowGeometry()
{
    var workArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);
    var capsule = new System.Drawing.Rectangle(800, 0, 320, 84);
    var maximizedWindow = workArea;
    var target = BrowserWindowGeometry.FindNonOverlappingBounds(maximizedWindow, capsule, workArea);
    Require(target is not null && !target.Value.IntersectsWith(capsule) && target.Value.Width == workArea.Width,
        "maximized browser window was not moved into the available work area");

    var noOverlap = BrowserWindowGeometry.FindNonOverlappingBounds(
        new System.Drawing.Rectangle(0, 100, 640, 480), capsule, workArea);
    Require(noOverlap is null, "window geometry was changed even though it did not cover the capsule");
}

void TestPanelDockGeometry()
{
    const double workLeft = 100;
    const double workTop = 50;
    const double workWidth = 1000;
    const double workHeight = 700;
    const double panelWidth = 300;
    const double panelHeight = 200;

    Require(!PanelDockGeometry.IsVertical("top") && !PanelDockGeometry.IsVertical("bottom") &&
        PanelDockGeometry.IsVertical("left") && PanelDockGeometry.IsVertical("right"),
        "dock edges did not map to the required panel orientations");

    var left = PanelDockGeometry.GetPositionAtEdge(
        "left", workLeft, workTop, workWidth, workHeight, panelWidth, panelHeight);
    var right = PanelDockGeometry.GetPositionAtEdge(
        "right", workLeft, workTop, workWidth, workHeight, panelWidth, panelHeight);
    var top = PanelDockGeometry.GetPositionAtEdge(
        "top", workLeft, workTop, workWidth, workHeight, panelWidth, panelHeight);
    var bottom = PanelDockGeometry.GetPositionAtEdge(
        "bottom", workLeft, workTop, workWidth, workHeight, panelWidth, panelHeight);
    Require(left.Left == 110 && left.Top == 300 &&
        right.Left == 790 && right.Top == 300 &&
        top.Left == 450 && top.Top == 58 &&
        bottom.Left == 450 && bottom.Top == 540,
        "docked positions did not align to edges and center along the working area");

    Require(PanelDockGeometry.FindNearestEdge(
            120, 250, panelWidth, panelHeight, workLeft, workTop, workWidth, workHeight) == "left" &&
        PanelDockGeometry.FindNearestEdge(
            132, 250, panelWidth, panelHeight, workLeft, workTop, workWidth, workHeight) == "left" &&
        PanelDockGeometry.FindNearestEdge(
            133, 250, panelWidth, panelHeight, workLeft, workTop, workWidth, workHeight) is null,
        "the docking threshold was inconsistent or caused distant edges to snap");
}

void TestWidgetIconSize()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var widget = new WidgetDefinition(
                "icon-size-test",
                "Icon size test",
                "Test widget",
                "N",
                new System.Windows.Controls.Border());
            var registry = new WidgetRegistry();
            registry.Register(widget);

            registry.SetIconSize(19);
            Require(widget.IconSize == 20 && widget.NavigationButtonHeight == 48,
                "minimum icon size did not preserve the minimum navigation button height");
            registry.SetIconSize(50);
            Require(widget.IconSize == 50 && widget.NavigationButtonHeight == 66,
                "maximum icon size did not grow the navigation button");
            registry.SetIconSize(51);
            Require(widget.IconSize == 50, "icon size was not clamped to the supported DIP range");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
        throw new InvalidOperationException("Widget icon size test failed.", failure);
}

void TestScreenTextCleanup()
{
    var cleaned = ScreenTextTranslationService.CleanFormatting("  NOVA   screen  \r\n text \n\n another  line \n");
    Require(cleaned == $"NOVA screen text{Environment.NewLine}{Environment.NewLine}another line",
        "screen OCR formatting cleanup did not normalize wrapped lines and paragraphs");
}

async Task TestTranslationLimits()
{
    var service = new ScreenTextTranslationService();
    var rejectedEmpty = false;
    try { await service.TranslateAsync("", "en", "ru"); }
    catch (ArgumentException) { rejectedEmpty = true; }
    Require(rejectedEmpty, "empty text was not rejected");

    var rejectedOversized = false;
    try { await service.TranslateAsync(new string('x', 501), "en", "ru"); }
    catch (ArgumentException) { rejectedOversized = true; }
    Require(rejectedOversized, "text above the provider's 500-character limit was not rejected");
}

async Task TestWindowsOcr()
{
    var service = new ScreenOcrService();
    var languages = service.GetAvailableLanguages();
    Require(languages.Count > 0, "Windows has no installed OCR language packs");
    var language = languages.FirstOrDefault(item => item.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        ?? languages[0];

    using var bitmap = new System.Drawing.Bitmap(1500, 220);
    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
    {
        graphics.Clear(System.Drawing.Color.White);
        using var font = new System.Drawing.Font("Arial", 64, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        graphics.DrawString("NOVA SCREEN TEXT 123", font, System.Drawing.Brushes.Black, new System.Drawing.PointF(28, 62));
    }
    using var imageStream = new MemoryStream();
    bitmap.Save(imageStream, ImageFormat.Png);

    var text = await service.RecognizeImageAsync(imageStream.ToArray(), language.LanguageTag);
    Require(text.Contains("NOVA", StringComparison.OrdinalIgnoreCase),
        $"Windows OCR did not recognize the synthetic text image: {text}");
}

async Task TestLiveLoopback()
{
    using var meter = new AudioMeterService();
    meter.SetEnabled(true);
    await Task.Delay(TimeSpan.FromSeconds(1));
    var sample = meter.Sample();
    meter.SetEnabled(false);
    Require(sample.Error is null, $"WASAPI loopback capture failed: {sample.Error}");
    Console.WriteLine(sample.Level > 0.01f
        ? $"      observed non-silent system audio (level {sample.Level:0.00})"
        : "      loopback capture was available; no significant audio level was sampled");
}

void TestThroughput()
{
    const int blockCount = 3000;
    const int samplesPerBlock = 480;
    var analyzer = new AudioBandAnalyzer();
    var block = new float[samplesPerBlock];
    for (var index = 0; index < block.Length; index++)
        block[index] = 0.35f * MathF.Sin(2 * MathF.PI * 80 * index / 48_000) +
            0.2f * MathF.Sin(2 * MathF.PI * 6000 * index / 48_000);

    var timer = Stopwatch.StartNew();
    for (var index = 0; index < blockCount; index++)
        ProcessBlock(analyzer, block);
    timer.Stop();
    Console.WriteLine($"      analyzer: {blockCount * 10} ms audio processed in {timer.Elapsed.TotalMilliseconds:0.0} ms");
    Require(timer.Elapsed < TimeSpan.FromSeconds(3), "processing exceeded 10% of the represented audio duration");
}

AudioBeatSample AnalyzeTone(float frequency, float amplitude)
{
    const int samplesPerBlock = 480;
    const int blockCount = 32;
    var analyzer = new AudioBandAnalyzer();
    var block = new float[samplesPerBlock];
    AudioBeatSample? result = null;
    for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
    {
        for (var sampleIndex = 0; sampleIndex < samplesPerBlock; sampleIndex++)
        {
            var sampleNumber = blockIndex * samplesPerBlock + sampleIndex;
            block[sampleIndex] = amplitude * MathF.Sin(2 * MathF.PI * frequency * sampleNumber / 48_000);
        }
        result = ProcessBlock(analyzer, block);
    }
    return result!;
}

AudioBeatSample ProcessBlock(AudioBandAnalyzer analyzer, ReadOnlySpan<float> samples)
{
    foreach (var sample in samples)
        analyzer.AddSample(sample, 48_000);
    return analyzer.CompleteBuffer(samples.Length, 48_000);
}

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class RecordingTrainReminderNotifier : ITrainReminderNotifier
{
    public List<TrainDepartureReminder> Notifications { get; } = [];

    public void Show(TrainDepartureReminder reminder) => Notifications.Add(reminder);
}
