using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Nova.WindowsClient.Services;

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
Run("Yandex LRC parser preserves timestamps and untimed text", TestYandexLrcParser);
Run("Yandex lyrics failures map to distinct user-visible states", TestYandexLyricsFailureClassification);
Run("OBS WebSocket authentication vector is stable", TestObsAuthentication);
Run("OBS installation discovery never launches the program", TestObsInstallationDiscovery);
await RunAsync("OBS WebSocket reads state/scenes and sends recording/scene commands", TestObsWebSocketProtocol);
Run("browser adaptation finds a non-overlapping window position", TestBrowserWindowGeometry);
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
