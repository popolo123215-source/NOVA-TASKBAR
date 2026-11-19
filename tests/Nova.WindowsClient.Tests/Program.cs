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
using Windows.UI.Notifications;
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
    var generalToastTag = $"smoke-{Guid.NewGuid():N}";
    new WindowsToastNotificationService().ShowNotification(
        "NOVA · проверка",
        "Проверка уведомления центра NOVA.",
        generalToastTag);

    var testTag = testId[..16];
    var history = ToastNotificationManagerCompat.History.GetHistory();
    var found = history
        .Any(notification => notification.Tag == testTag && notification.Group == "NOVA-Train");
    Require(found, "Windows did not record the NOVA test toast in notification history.");
    Require(history.Any(notification => notification.Tag == generalToastTag &&
        notification.Group == "NOVA-Notification"),
        "Windows did not record the notification-center test toast in notification history.");
    Console.WriteLine("PASS  Train and notification-center Windows Toasts appear in notification history.");
    return 0;
}

if (args.Contains("--notification-center-smoke", StringComparer.Ordinal))
{
    var directory = Path.Combine(Path.GetTempPath(), $"NOVA-Notification-Smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var center = new NotificationCenterService(new NotificationCenterStore(directory));
        center.Initialize();
        var dueAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var reminder = center.AddReminder("NOVA · проверка планировщика", "Панель не требуется для доставки.", dueAt, DateTimeOffset.UtcNow);
        await Task.Delay(TimeSpan.FromSeconds(4));
        var delivered = center.ProcessDue(
            DateTimeOffset.UtcNow,
            new WindowsToastNotificationService().ShowNotification);
        var found = ToastNotificationManager.History.GetHistory("NOVA.TaskBar")
            .Any(notification => notification.Tag == reminder.Id &&
                notification.Group == "NOVA-Notification");
        Require(delivered == 1 && found,
            "The notification-center scheduler did not deliver a real Windows Toast.");
        Console.WriteLine("PASS  A persisted due reminder was delivered through the notification-center scheduler as a real Windows Toast.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
    return 0;
}

if (args.Contains("--productivity-notification-smoke", StringComparer.Ordinal))
{
    var smokeArgumentIndex = Array.IndexOf(args, "--productivity-notification-smoke");
    if (args.Length <= smokeArgumentIndex + 1 || !File.Exists(args[smokeArgumentIndex + 1]))
        throw new ArgumentException("Pass the actual Nova.TaskBar.exe path after --productivity-notification-smoke.");
    var appExecutablePath = args[smokeArgumentIndex + 1];
    string? failure = null;
    var smokeThread = new Thread(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NOVA-Productivity-Smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            WindowsToastRegistration.Initialize(appExecutablePath);
            var now = DateTimeOffset.UtcNow;
            var service = new ProductivityService(new ProductivityStore(directory));
            service.Initialize();
            var reminder = service.CreateReminder(
                new ParsedSmartReminder(
                    "NOVA · проверка Productivity",
                    now.AddSeconds(1),
                    ProductivityRecurrence.None,
                    TimeZoneInfo.Local.Id),
                "Планировщик работает без открытого окна.",
                now);
            var center = new NotificationCenterService(new NotificationCenterStore(directory));
            center.Initialize();
            var historyCountBefore = ToastNotificationManager.History.GetHistory("NOVA.TaskBar").Count;
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var delivered = service.ProcessDue(
                DateTimeOffset.UtcNow,
                (title, body, tag) =>
                {
                    center.AddSystemEvent(title, body, DateTimeOffset.UtcNow);
                    new WindowsToastNotificationService().ShowNotification(title, body, tag);
                });
            var found = ToastNotificationManager.History.GetHistory("NOVA.TaskBar").Count > historyCountBefore;
            Require(delivered == 1 && found && center.Entries.Any(entry => entry.Kind == NotificationEntryKind.SystemEvent),
                "Productivity reminder was not delivered as a real Windows Toast and saved to local history.");
            Console.WriteLine("PASS  A persisted Productivity reminder generated a Windows Toast and notification history entry without creating a panel window.");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    })
    {
        IsBackground = true,
        Name = "NOVA Productivity Toast smoke"
    };
    smokeThread.SetApartmentState(ApartmentState.STA);
    smokeThread.Start();
    smokeThread.Join();
    if (failure is not null)
        throw new InvalidOperationException(failure);
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
Run("notification center persists reminders and records system events", TestNotificationCenterPersistence);
Run("notification center delivers simultaneous reminders once and skips stale reminders", TestNotificationCenterDelivery);
Run("productivity tasks, notes, calendar links and local memory persist and update", TestProductivityPersistenceAndManagement);
Run("productivity schema v0 migrates atomically to the current document", TestProductivitySchemaMigration);
Run("multiple timers and task deadlines restore and notify exactly once", TestProductivityTimerAndTaskDeadlineDelivery);
Run("failed Productivity deliveries stay pending and retry once", TestProductivityDeliveryRetry);
Run("smart reminders parse Russian natural language and recurring schedules", TestSmartReminderParsingAndDelivery);
Run("calendar recurrence handles month boundaries and daylight-saving gaps", TestProductivityCalendarTimeZones);
Run("quick actions and workflows require confirmation for risky operations", TestProductivityQuickActions);
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
Run("defect recovery controls use readable templates and centralized nav sizing", TestDefectRecoveryTemplates);
Run("profile CRUD, persistence and built-in profiles", TestProfileCrudAndPersistence);
Run("profile priority, cooldown and manual override", TestProfileRulesAndManualOverride);
Run("screen text cleanup preserves paragraphs and merges wrapped lines", TestScreenTextCleanup);
await RunAsync("screen translation rejects empty and oversized input before sending it", TestTranslationLimits);
Run("default Windows output volume is readable", TestSystemMasterVolume);
await RunAsync("Windows Audio enumerates active endpoints and sessions", TestAudioMixerSnapshot);
Run("system metrics read live memory, disks and Windows counters", TestSystemMetrics);
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

void TestNotificationCenterPersistence()
{
    WithTempReminderStore(directory =>
    {
        var now = DateTimeOffset.UtcNow;
        var center = new NotificationCenterService(new NotificationCenterStore(directory));
        center.Initialize();
        var reminder = center.AddReminder(
            "Проверка встречи",
            "Взять документы",
            now.AddHours(2),
            now);
        center.AddSystemEvent("Входящий файл", "Пользователь предлагает документ.", now);

        var restarted = new NotificationCenterService(new NotificationCenterStore(directory));
        restarted.Initialize();
        Require(restarted.Entries.Count == 2 &&
            restarted.Entries.Any(entry => entry.Id == reminder.Id &&
                entry.Kind == NotificationEntryKind.Reminder &&
                entry.Enabled &&
                entry.DueAt == reminder.DueAt) &&
            restarted.Entries.Any(entry => entry.Kind == NotificationEntryKind.SystemEvent),
            "notification history or reminder did not survive reloading the local store");
        var eventEntry = restarted.Entries.Single(entry => entry.Kind == NotificationEntryKind.SystemEvent);
        Require(restarted.MarkRead(eventEntry.Id) && restarted.UnreadCount == 1,
            "a stored system event could not be marked read");
        Require(restarted.SetReminderEnabled(reminder.Id, false, now) == NotificationReminderUpdateResult.Updated &&
            !restarted.Entries.Single(entry => entry.Id == reminder.Id).Enabled,
            "a general reminder could not be disabled");
        Require(restarted.Delete(reminder.Id) &&
            !restarted.Entries.Any(entry => entry.Id == reminder.Id),
            "a general reminder could not be deleted");
    });
}

void TestNotificationCenterDelivery()
{
    WithTempReminderStore(directory =>
    {
        var now = DateTimeOffset.UtcNow;
        var dueAt = now.AddSeconds(5);
        var center = new NotificationCenterService(new NotificationCenterStore(directory));
        center.Initialize();
        center.AddReminder("Первое", "Текст 1", dueAt, now);
        center.AddReminder("Второе", "Текст 2", dueAt, now);
        var restarted = new NotificationCenterService(new NotificationCenterStore(directory));
        restarted.Initialize();
        var delivered = new List<string>();
        Require(restarted.ProcessDue(dueAt, (title, _, _) => delivered.Add(title)) == 2 &&
            delivered.Order().SequenceEqual(new[] { "Второе", "Первое" }.Order()),
            "simultaneous reminders were not independently delivered");
        var afterRestart = new NotificationCenterService(new NotificationCenterStore(directory));
        afterRestart.Initialize();
        Require(afterRestart.ProcessDue(dueAt.AddSeconds(20), (_, _, _) => delivered.Add("duplicate")) == 0 &&
            delivered.Count == 2,
            "a delivered reminder was repeated after restarting the notification center");

        var laterNow = DateTimeOffset.UtcNow;
        var missedDue = laterNow.AddSeconds(5);
        afterRestart.AddReminder("Просроченное", "", missedDue, laterNow);
        Require(afterRestart.ProcessDue(missedDue.AddMinutes(2), (_, _, _) => delivered.Add("late")) == 0 &&
            delivered.Count == 2 &&
            afterRestart.Entries.Single(entry => entry.Title == "Просроченное").Enabled == false,
            "a stale notification was sent instead of being disabled");
    });
}

void TestProductivityPersistenceAndManagement()
{
    WithTempReminderStore(directory =>
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        var task = service.CreateTask(
            "Подготовить отчёт",
            "Собрать локальные данные",
            "Работа",
            ProductivityPriority.Normal,
            now.AddDays(1),
            true,
            now);
        var looseTask = service.CreateTask(
            "Проверить результаты",
            "",
            "Работа",
            ProductivityPriority.Low,
            null,
            false,
            now);
        var note = service.CreateNote("Встреча", "Взять блокнот", "Работа", true, now);
        var memory = service.CreateMemory(
            ProductivityMemoryKind.Preference,
            "Предпочитаю утренние встречи",
            now);
        var calendarEvent = service.CreateCalendarEvent(
            "Обсудить отчёт",
            "Комната 2",
            now.AddDays(2),
            15,
            ProductivityRecurrence.Weekly,
            TimeZoneInfo.Local.Id,
            task.Id,
            now);

        service.UpdateTask(
            task.Id,
            "Подготовить квартальный отчёт",
            "Сверить итоговые цифры",
            "Работа",
            ProductivityPriority.High,
            now.AddDays(3),
            false,
            true,
            now.AddMinutes(1));
        Require(service.SetTaskCompleted(looseTask.Id, true, now.AddMinutes(1)),
            "a task could not be marked complete");
        service.UpdateNote(note.Id, "Встреча с командой", "Взять блокнот и план", "Работа", true, now.AddMinutes(2));
        service.UpdateMemory(
            memory.Id,
            ProductivityMemoryKind.Context,
            "Утренние встречи проходят быстрее",
            now.AddMinutes(2));

        var restarted = new ProductivityService(new ProductivityStore(directory));
        restarted.Initialize();
        Require(restarted.IsAvailable &&
            restarted.Tasks.Single(item => item.Id == task.Id).Priority == ProductivityPriority.High &&
            restarted.Tasks.Single(item => item.Id == looseTask.Id).IsCompleted &&
            restarted.Notes.Single().Title == "Встреча с командой" &&
            restarted.Memories.Single().Text == "Утренние встречи проходят быстрее",
            "tasks, notes, categories, priority, pinning, completion, or local memory did not survive restart");
        Require(restarted.SearchContext("утренние").Any(item => item.Source == "Локальный контекст") &&
            restarted.SearchContext("квартальный").Any(item => item.Source == "Незавершённая задача") &&
            restarted.SearchContext("план").Any(item => item.Source == "Заметка"),
            "context search did not include stored memory, tasks and notes");
        Require(restarted.CalendarEvents.Single().RelatedTaskId == task.Id,
            "the calendar event lost its linked task");
        Require(restarted.DeleteTask(task.Id) &&
            restarted.CalendarEvents.Single().RelatedTaskId is null,
            "deleting a linked task left a dangling calendar reference");
        Require(restarted.DeleteNote(note.Id) && restarted.DeleteMemory(memory.Id),
            "a note or user-controlled memory entry could not be deleted");
        var afterDelete = new ProductivityService(new ProductivityStore(directory));
        afterDelete.Initialize();
        Require(afterDelete.Notes.Count == 0 && afterDelete.Memories.Count == 0 &&
            afterDelete.Tasks.Single().Id == looseTask.Id,
            "deletions were not persisted");
    });
}

void TestProductivitySchemaMigration()
{
    WithTempReminderStore(directory =>
    {
        var file = Path.Combine(directory, "productivity.json");
        File.WriteAllText(file, "{\"Format\":\"NOVA-PRODUCTIVITY\",\"Version\":0}");
        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        using var migrated = JsonDocument.Parse(File.ReadAllText(file));
        Require(service.IsAvailable &&
            migrated.RootElement.GetProperty("Version").GetInt32() == 1,
            "the prior empty productivity schema was not upgraded to version 1");
    });
}

void TestProductivityTimerAndTaskDeadlineDelivery()
{
    WithTempReminderStore(directory =>
    {
        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        var now = DateTimeOffset.UtcNow;
        var countdown = service.CreateTimer("Проверка", ProductivityTimerKind.Countdown, TimeSpan.FromSeconds(30), now);
        service.StartTimer(countdown.Id, now);
        var stopwatch = service.CreateTimer("Секундомер", ProductivityTimerKind.Stopwatch, TimeSpan.Zero, now);
        service.StartTimer(stopwatch.Id, now);
        var task = service.CreateTask(
            "Сдать файл",
            "",
            "Работа",
            ProductivityPriority.High,
            now.AddSeconds(20),
            false,
            now);

        var restarted = new ProductivityService(new ProductivityStore(directory));
        restarted.Initialize();
        var restoredCountdown = restarted.GetTimerSnapshots(now.AddSeconds(10)).Single(item => item.Timer.Id == countdown.Id);
        var restoredStopwatch = restarted.GetTimerSnapshots(now.AddSeconds(10)).Single(item => item.Timer.Id == stopwatch.Id);
        Require(Math.Abs(restoredCountdown.Remaining.TotalSeconds - 20) < 0.1 &&
            Math.Abs(restoredStopwatch.Elapsed.TotalSeconds - 10) < 0.1,
            "running countdown or stopwatch elapsed time was not reconstructed after restart");
        var delivered = new List<string>();
        Require(restarted.ProcessDue(now.AddSeconds(20), (title, body, _) => delivered.Add($"{title}:{body}")) == 1 &&
            delivered.Single().Contains("Сдать файл", StringComparison.Ordinal),
            "the task deadline was not sent through the productivity scheduler");
        Require(restarted.ProcessDue(now.AddSeconds(31), (title, body, _) => delivered.Add($"{title}:{body}")) == 1 &&
            delivered.Any(item => item.Contains("Проверка", StringComparison.Ordinal)),
            "the countdown did not complete and deliver its notification");
        var afterRestart = new ProductivityService(new ProductivityStore(directory));
        afterRestart.Initialize();
        Require(afterRestart.ProcessDue(now.AddSeconds(40), (_, _, _) => delivered.Add("duplicate")) == 0 &&
            delivered.Count == 2 &&
            afterRestart.GetTimerSnapshots(now.AddSeconds(40)).Single(item => item.Timer.Id == countdown.Id).Timer.IsCompleted,
            "a task deadline or countdown notification repeated after another restart");

        afterRestart.PauseTimer(stopwatch.Id, now.AddSeconds(40));
        var paused = afterRestart.GetTimerSnapshots(now.AddDays(1)).Single(item => item.Timer.Id == stopwatch.Id);
        Require(!paused.IsRunning && Math.Abs(paused.Elapsed.TotalSeconds - 40) < 0.1,
            "a paused stopwatch did not retain its elapsed time");
        afterRestart.StartTimer(stopwatch.Id, now.AddDays(1));
        var resumed = afterRestart.GetTimerSnapshots(now.AddDays(1).AddSeconds(5))
            .Single(item => item.Timer.Id == stopwatch.Id);
        Require(Math.Abs(resumed.Elapsed.TotalSeconds - 45) < 0.1,
            "the stopwatch did not continue from its persisted elapsed value");
    });
}

void TestProductivityDeliveryRetry()
{
    WithTempReminderStore(directory =>
    {
        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        var now = DateTimeOffset.UtcNow;
        var dueReminder = service.CreateReminder(
            new ParsedSmartReminder("Повторная доставка", now.AddSeconds(1), ProductivityRecurrence.None, TimeZoneInfo.Local.Id),
            "",
            now);

        Require(service.ProcessDue(now.AddSeconds(2), (_, _, _) => throw new IOException("Toast недоступен.")) == 0 &&
            service.Reminders.Single(item => item.Id == dueReminder.Id) is
            {
                IsEnabled: true,
                LastTriggeredAt: null,
                LastOutcome: null
            },
            "a failed Windows notification was recorded as sent instead of remaining retryable");

        var delivered = 0;
        Require(service.ProcessDue(now.AddSeconds(3), (_, _, _) => delivered++) == 1 &&
            delivered == 1 &&
            service.ProcessDue(now.AddSeconds(4), (_, _, _) => delivered++) == 0,
            "a retried notification was not delivered exactly once");
    });
}

void TestSmartReminderParsingAndDelivery()
{
    WithTempReminderStore(directory =>
    {
        var zone = LocalTrainScheduleService.GetMoscowTimeZone()
            ?? throw new InvalidOperationException("Europe/Moscow time zone is unavailable.");
        var date = new DateOnly(2026, 11, 18);
        var now = MoscowUtc(date, new TimeOnly(7, 0));
        Require(SmartReminderParser.TryParse(
                "завтра в 09:15 позвонить маме",
                now,
                zone,
                out var parsedTomorrow,
                out var error) &&
            parsedTomorrow!.Title == "позвонить маме" &&
            TimeZoneInfo.ConvertTime(parsedTomorrow.DueAt, zone).Date == date.AddDays(1).ToDateTime(TimeOnly.MinValue).Date,
            $"natural-language next-day reminder failed: {error}");
        Require(SmartReminderParser.TryParse(
                "каждый день в 08:00 витамины",
                now,
                zone,
                out var parsedDaily,
                out _) &&
            parsedDaily!.Recurrence == ProductivityRecurrence.Daily &&
            parsedDaily.Title == "витамины",
            "daily recurrence or its reminder title was parsed incorrectly");
        Require(SmartReminderParser.TryParse(
                "через 20 минут проверить почту",
                now,
                zone,
                out var parsedRelative,
                out _) &&
            parsedRelative!.Title == "проверить почту" &&
            parsedRelative.DueAt == now.AddMinutes(20),
            "relative time was not parsed or calculated correctly");
        Require(!SmartReminderParser.TryParse("позвонить маме", now, zone, out _, out _),
            "a phrase without a time was accepted as a scheduled reminder");

        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        var dueReminder = service.CreateReminder(
            new ParsedSmartReminder("Проверить доставку", now.AddSeconds(5), ProductivityRecurrence.None, zone.Id),
            string.Empty,
            now);
        var repeating = service.CreateReminder(
            new ParsedSmartReminder("Витамины", now.AddSeconds(5), ProductivityRecurrence.Daily, zone.Id),
            string.Empty,
            now);
        var task = service.CreateTask("Подготовить встречу", "", "", ProductivityPriority.Normal, null, false, now);
        service.CreateCalendarEvent(
            "Встреча",
            "Переговорная",
            now.AddMinutes(5),
            5,
            ProductivityRecurrence.Weekly,
            zone.Id,
            task.Id,
            now);

        var delivered = new List<string>();
        Require(service.ProcessDue(now.AddSeconds(6), (title, _, id) => delivered.Add($"{title}:{id}")) == 3,
            "a reminder and its linked calendar event were not delivered together");
        Require(delivered.Select(item => item.Split(':', 2)[1]).Distinct(StringComparer.Ordinal).Count() == 3 &&
            service.Reminders.Single(item => item.Id == repeating.Id).IsEnabled &&
            service.Reminders.Single(item => item.Id == repeating.Id).DueAt > now.AddSeconds(6) &&
            !service.Reminders.Single(item => item.Id == dueReminder.Id).IsEnabled,
            "coincident event/reminder notifications duplicated or recurrence state was not advanced");
        var restarted = new ProductivityService(new ProductivityStore(directory));
        restarted.Initialize();
        Require(restarted.ProcessDue(now.AddSeconds(7), (_, _, _) => delivered.Add("duplicate")) == 0,
            "a reminder or calendar notification repeated after restart");

        var movable = restarted.CreateReminder(
            new ParsedSmartReminder("Перенос", now.AddDays(1), ProductivityRecurrence.None, zone.Id),
            string.Empty,
            now);
        restarted.SnoozeReminder(movable.Id, TimeSpan.FromMinutes(10), now);
        Require(restarted.Reminders.Single(item => item.Id == movable.Id).DueAt == now.AddMinutes(10),
            "a reminder could not be postponed");
        restarted.SetReminderEnabled(movable.Id, false, now);
        Require(!restarted.Reminders.Single(item => item.Id == movable.Id).IsEnabled &&
            restarted.DeleteReminder(movable.Id),
            "a reminder could not be disabled or deleted");
    });
}

void TestProductivityCalendarTimeZones()
{
    var monthEndZone = TimeZoneInfo.Utc;
    var monthEnd = new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);
    var nextMonthly = ProductivityService.GetNextOccurrence(monthEnd, ProductivityRecurrence.Monthly, monthEndZone);
    Require(nextMonthly == new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero),
        "monthly recurrence did not clamp to the final day of a shorter month");

    var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    var beforeGap = new DateTimeOffset(2026, 3, 7, 2, 30, 0, TimeSpan.FromHours(-8));
    var afterGap = ProductivityService.GetNextOccurrence(beforeGap, ProductivityRecurrence.Daily, pacific);
    var localAfterGap = TimeZoneInfo.ConvertTime(afterGap, pacific);
    Require(localAfterGap.Date == new DateTime(2026, 3, 8) &&
        localAfterGap.TimeOfDay >= TimeSpan.FromHours(3),
        "recurrence did not move a nonexistent daylight-saving local time to the next valid time");
}

void TestProductivityQuickActions()
{
    WithTempReminderStore(directory =>
    {
        var actionPath = Path.Combine(directory, "document.txt");
        File.WriteAllText(actionPath, "local test file");
        var service = new ProductivityService(new ProductivityStore(directory));
        service.Initialize();
        var openFile = service.CreateQuickAction(
            "Открыть файл",
            ProductivityQuickActionKind.OpenPath,
            actionPath,
            DateTimeOffset.UtcNow);
        var lockAction = service.CreateQuickAction(
            "Блокировка",
            ProductivityQuickActionKind.LockWorkstation,
            string.Empty,
            DateTimeOffset.UtcNow);
        Require(!ProductivityQuickActionRunner.RequiresConfirmation(openFile) &&
            ProductivityQuickActionRunner.RequiresConfirmation(lockAction),
            "confirmation requirements did not distinguish safe file opening from system actions");
        var rejected = false;
        try
        {
            ProductivityQuickActionRunner.Execute(lockAction, confirmationAccepted: false);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Require(rejected, "a system action ran without explicit confirmation");

        var workflow = service.CreateWorkflow("Открыть и заблокировать", [openFile.Id, lockAction.Id], DateTimeOffset.UtcNow);
        Require(service.GetWorkflowActions(workflow.Id).Select(item => item.Id)
                .SequenceEqual(new[] { openFile.Id, lockAction.Id }),
            "the saved user scenario did not preserve its step order");
        var blockedDelete = false;
        try
        {
            service.DeleteQuickAction(openFile.Id);
        }
        catch (InvalidOperationException)
        {
            blockedDelete = true;
        }
        Require(blockedDelete, "deleting an action broke a stored workflow reference");
        var invalidOrderRejected = false;
        try
        {
            service.CreateWorkflow("Неверный порядок", [lockAction.Id, openFile.Id], DateTimeOffset.UtcNow);
        }
        catch (ArgumentException)
        {
            invalidOrderRejected = true;
        }
        Require(invalidOrderRejected, "a system-lock action was allowed before later workflow steps");
        Require(service.DeleteWorkflow(workflow.Id) && service.DeleteQuickAction(openFile.Id),
            "a workflow and its now-unreferenced action could not be deleted");
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

async Task TestAudioMixerSnapshot()
{
    using var mixer = new AudioMixerService();
    var snapshot = await mixer.RefreshAsync();
    if (snapshot.Error is { Length: > 0 } error)
    {
        Console.WriteLine($"      Windows Audio reported unavailable: {error}");
        return;
    }

    Require(snapshot.Outputs.All(device => !string.IsNullOrWhiteSpace(device.Id) &&
        !string.IsNullOrWhiteSpace(device.Name)) &&
        snapshot.Inputs.All(device => !string.IsNullOrWhiteSpace(device.Id) &&
        !string.IsNullOrWhiteSpace(device.Name)),
        "Windows Audio returned an endpoint without its device identity");
    Require(snapshot.Sessions.All(session => float.IsFinite(session.Volume) &&
        session.Volume is >= 0 and <= 1 &&
        !string.IsNullOrWhiteSpace(session.Id) &&
        !string.IsNullOrWhiteSpace(session.Name)),
        "Windows Audio returned an invalid active-session snapshot");
    if (snapshot.Sessions.FirstOrDefault() is { } session)
    {
        await mixer.SetSessionVolumeAsync(session.DeviceId, session.SessionId, session.Volume);
        await mixer.SetSessionMuteAsync(session.DeviceId, session.SessionId, session.IsMuted);
    }
    Console.WriteLine($"      endpoints: {snapshot.Outputs.Count} output / {snapshot.Inputs.Count} input; active sessions: {snapshot.Sessions.Count}");
}

void TestSystemMetrics()
{
    using var metrics = new SystemStatsService();
    var snapshot = metrics.Sample();
    Require(double.IsFinite(snapshot.RamTotalGb) &&
        double.IsFinite(snapshot.RamUsedGb) &&
        snapshot.RamTotalGb > 0 &&
        snapshot.RamUsedGb >= 0 &&
        snapshot.RamUsedGb <= snapshot.RamTotalGb,
        "Windows returned invalid physical-memory totals");
    Require(snapshot.CpuPercent is null or >= 0 and <= 100 &&
        snapshot.Gpu3DPercent is null or >= 0 and <= 100 &&
        snapshot.DiskReadBytesPerSecond is null or >= 0 &&
        snapshot.DiskWriteBytesPerSecond is null or >= 0,
        "a native counter returned an invalid metric value");
    Require(snapshot.DiskSpaces.All(disk => disk.TotalBytes > 0 &&
        disk.AvailableBytes >= 0 &&
        disk.AvailableBytes <= disk.TotalBytes),
        "Windows returned invalid free-space information");
    Console.WriteLine($"      RAM {snapshot.RamUsedGb:0.0}/{snapshot.RamTotalGb:0.0} GB; disks: {snapshot.DiskSpaces.Count}; CPU/GPU sensors: {snapshot.CpuTemperatureCelsius?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a"}/{snapshot.GpuTemperatureCelsius?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a"} °C");
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

    var russianLanguage = languages.FirstOrDefault(item =>
        item.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
    if (russianLanguage is not null)
    {
        using var russianBitmap = new Bitmap(1100, 180, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(russianBitmap))
        {
            graphics.Clear(Color.White);
            using var russianFont = new Font("Segoe UI", 34, FontStyle.Bold);
            graphics.DrawString("ПРИВЕТ NOVA 123", russianFont, Brushes.Black, new PointF(28, 52));
        }
        using var russianStream = new MemoryStream();
        russianBitmap.Save(russianStream, ImageFormat.Png);
        var russianText = await service.RecognizeImageAsync(russianStream.ToArray(), russianLanguage.LanguageTag);
        Require(russianText.Contains("NOVA", StringComparison.OrdinalIgnoreCase) ||
            russianText.Contains("ПРИВЕТ", StringComparison.OrdinalIgnoreCase),
            $"Windows OCR did not recognize the synthetic Russian text image: {russianText}");
    }
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

void TestProfileCrudAndPersistence()
{
    WithTempReminderStore(directory =>
    {
        var service = new ProfileService(new ProfileStore(directory));
        service.Initialize();
        Require(service.Profiles.Count == 5 &&
            service.Profiles.Select(profile => profile.Name).Contains("Gaming", StringComparer.Ordinal),
            "built-in NOVA profiles were not initialized");

        var custom = service.CreateProfile("Work custom");
        var duplicate = service.DuplicateProfile(custom.Id);
        var renamed = service.RenameProfile(duplicate.Id, "Renamed profile");
        Require(service.Profiles.Count == 7 &&
            renamed.Name == "Renamed profile",
            "custom profile create, duplicate or rename failed");

        Require(service.DeleteProfile(renamed.Id),
            "custom profile deletion failed");

        var snapshot = ProfileSnapshot.Default with
        {
            SelectedWidget = "Music",
            EnableAudioReactive = true,
            NotificationsEnabled = false
        };
        service.UpdateProfileSnapshot("gaming", snapshot);
        service.ActivateManual("gaming", pauseAutomation: true);

        var restarted = new ProfileService(new ProfileStore(directory));
        restarted.Initialize();
        Require(restarted.ActiveProfile.Id == "gaming" &&
            restarted.ActiveProfile.Snapshot.SelectedWidget == "Music" &&
            !restarted.ActiveProfile.Snapshot.NotificationsEnabled &&
            restarted.AutomationPaused,
            "profile state and snapshot did not persist across restart");
    });
}

void TestProfileRulesAndManualOverride()
{
    WithTempReminderStore(directory =>
    {
        var service = new ProfileService(new ProfileStore(directory));
        service.Initialize();
        var now = DateTimeOffset.UtcNow;
        var high = new ProfileRule(
            "gaming-rule",
            ProfileRuleKind.Process,
            "game.exe",
            true,
            200,
            2,
            30,
            null,
            null,
            null);
        var low = new ProfileRule(
            "work-rule",
            ProfileRuleKind.Process,
            "devenv",
            true,
            100,
            0,
            30,
            null,
            null,
            null);
        service.AddRule("gaming", high);
        service.AddRule("work", low);

        var pendingResult = service.Evaluate(now.AddSeconds(3), ["game.exe"], false, null);
        var activatedResult = service.Evaluate(now.AddSeconds(6), ["game.exe"], false, null);
        Require(pendingResult == "default" &&
            activatedResult == "gaming" &&
            service.ActiveProfile.Id == "gaming",
            "profile process rule did not activate after its configured delay");

        var manual = service.ActivateManual("focus", pauseAutomation: true);
        Require(manual.Id == "focus" &&
            service.AutomationPaused &&
            service.Evaluate(now.AddMinutes(1), ["game.exe"], false, null) == "focus",
            "manual override did not prevent automatic profile switching");

        service.ResumeAutomation();
        service.Evaluate(now.AddSeconds(300), ["game.exe"], false, null);
        Require(service.Evaluate(now.AddSeconds(303), ["game.exe"], false, null) == "gaming",
            "automation did not resume after manual override");

        service.ActivateManual("work", pauseAutomation: false);
        service.Evaluate(
            now.AddMinutes(10),
            ["game.exe", "devenv"],
            false,
            null);
        var priorityResult = service.Evaluate(
            now.AddMinutes(10).AddSeconds(3),
            ["game.exe", "devenv"],
            false,
            null);
        Require(priorityResult == "gaming",
            "higher-priority matching profile rule did not win");

        Require(service.Evaluate(now.AddMinutes(11), [], false, null) == "default",
            "missing processes did not fall back to the Default profile");
    });
}

void TestDefectRecoveryTemplates()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Nova.WindowsClient", "ShellWindow.xaml")))
        directory = directory.Parent;
    Require(directory is not null, "the NOVA source root was not found for template validation");

    var xaml = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Nova.WindowsClient", "ShellWindow.xaml"));
    var shell = File.ReadAllText(Path.Combine(directory.FullName, "src", "Nova.WindowsClient", "ShellWindow.xaml.cs"));
    Require(xaml.Contains("<Style TargetType=\"ComboBox\">", StringComparison.Ordinal) &&
        xaml.Contains("<Style TargetType=\"ComboBoxItem\">", StringComparison.Ordinal) &&
        xaml.Contains("<Style TargetType=\"ListBoxItem\">", StringComparison.Ordinal) &&
        xaml.Contains("<Style TargetType=\"PasswordBox\">", StringComparison.Ordinal),
        "readable ComboBox, ListBox, or PasswordBox templates are missing");
    var navigationStyleStart = xaml.IndexOf("<Style x:Key=\"AdaptiveWidgetNavigationButton\"", StringComparison.Ordinal);
    var navigationStyleEnd = navigationStyleStart >= 0
        ? xaml.IndexOf("</Style>", navigationStyleStart, StringComparison.Ordinal)
        : -1;
    var navigationStyle = navigationStyleStart >= 0 && navigationStyleEnd >= 0
        ? xaml[navigationStyleStart..(navigationStyleEnd + "</Style>".Length)]
        : string.Empty;
    Require(navigationStyle.Length > 0 &&
        !navigationStyle.Contains("<Setter Property=\"Width\"", StringComparison.Ordinal),
        "widget navigation still has a competing local Width source");
    Require(shell.Contains("CompleteDimensionsChange(animationVersion, targetWidth, targetHeight, targetLeft, targetTop)",
            StringComparison.Ordinal) &&
        shell.Contains("BeginAnimation(WidthProperty, null)", StringComparison.Ordinal),
        "dimension transitions do not animate from current values to a synchronized target");
    var island = File.ReadAllText(Path.Combine(directory.FullName, "src", "Nova.WindowsClient", "ShellWindow.Island.cs"));
    Require(xaml.Contains("x:Name=\"CompactLyricPrevious\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricCurrent\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricNext\"", StringComparison.Ordinal),
        "the compact music capsule does not contain three lyric lines");
    Require(xaml.Contains("x:Name=\"CompactLyricPreviousViewport\" Grid.Row=\"0\" HorizontalScrollBarVisibility=\"Hidden\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricCurrentViewport\" Grid.Row=\"1\" HorizontalScrollBarVisibility=\"Hidden\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricNextViewport\" Grid.Row=\"2\" HorizontalScrollBarVisibility=\"Hidden\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricPrevious\" Foreground=\"#88FFFFFF\" FontSize=\"8\" TextWrapping=\"NoWrap\" TextAlignment=\"Center\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricCurrent\" Foreground=\"{StaticResource Ink}\" FontSize=\"10\" FontWeight=\"SemiBold\" TextWrapping=\"NoWrap\" TextAlignment=\"Center\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"CompactLyricNext\" Foreground=\"#88FFFFFF\" FontSize=\"8\" TextWrapping=\"NoWrap\" TextAlignment=\"Center\"", StringComparison.Ordinal) &&
        xaml.Contains("HorizontalContentAlignment=\"Center\"", StringComparison.Ordinal) &&
        island.Contains("ScrollToHorizontalOffset", StringComparison.Ordinal) &&
        island.Contains("state.ScrollableWidth / scrollSpeed", StringComparison.Ordinal),
        "the compact lyric lines are not centered within their music capsule");
    Require(island.Contains("UpdateCompactLyrics(media?.CurrentPosition ?? TimeSpan.Zero)", StringComparison.Ordinal) &&
        island.Contains("if (_lyricsLines.Count == 0)", StringComparison.Ordinal) &&
        !island.Contains("UpdateCompactLyrics(TimeSpan.From", StringComparison.Ordinal),
        "compact lyrics are not driven exclusively by loaded real lines and media playback position");
    Require(!island.Contains("CompactMediaState.Text", StringComparison.Ordinal) &&
        !xaml.Contains("x:Name=\"CompactMediaState\"", StringComparison.Ordinal),
        "the compact music capsule still shows a playback-state label");
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
