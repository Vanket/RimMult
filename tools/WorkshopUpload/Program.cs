using System.Diagnostics;
using Steamworks;

// Updates an existing Steam Workshop item of RimWorld with a packaged mod folder, through the running Steam client.
//   WorkshopUpload --item <id> --content <folder> [--preview <png>] [--note <change note>]
//   WorkshopUpload --check      (only connects to Steam and shows the account: nothing is uploaded)
// Exit codes: 0 done, 1 bad arguments, 2 Steam not available, 3 upload failed.

const uint RimWorldAppId = 294100;

var check = args.Contains("--check");
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var rest = args.Where(a => a != "--check").ToArray();
for (var i = 0; i + 1 < rest.Length; i += 2)
    options[rest[i].TrimStart('-')] = rest[i + 1];

var itemId = 0UL;
var content = "";
if (!check && (!options.TryGetValue("item", out var itemText) || !ulong.TryParse(itemText, out itemId)
               || !options.TryGetValue("content", out content!) || !Directory.Exists(content)))
{
    Console.Error.WriteLine("Usage: WorkshopUpload --item <id> --content <folder> [--preview <png>] [--note <text>] | --check");
    return 1;
}
if (!check)
    content = Path.GetFullPath(content);
var preview = options.TryGetValue("preview", out var previewPath) && File.Exists(previewPath) ? Path.GetFullPath(previewPath) : null;
var note = options.TryGetValue("note", out var noteText) ? noteText : "";

// SteamAPI.Init reads steam_appid.txt from the working directory: the one copied next to the tool.
Environment.CurrentDirectory = AppContext.BaseDirectory;
Environment.SetEnvironmentVariable("SteamAppId", RimWorldAppId.ToString());
if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "steam_api64.dll")))
{
    Console.Error.WriteLine("steam_api64.dll is missing next to the tool: build it with -p:RimWorldDir=<RimWorld folder>.");
    return 2;
}
if (!SteamAPI.Init())
{
    Console.Error.WriteLine("Steam is not available: start the Steam client and log in with the account that owns the item.");
    return 2;
}

try
{
    Console.WriteLine($"Steam user: {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");
    if (check)
        return 0;
    Console.WriteLine($"Uploading {content} to Workshop item {itemId}…");

    var handle = SteamUGC.StartItemUpdate(new AppId_t(RimWorldAppId), new PublishedFileId_t(itemId));
    if (!SteamUGC.SetItemContent(handle, content))
    {
        Console.Error.WriteLine("Steam refused the content folder.");
        return 3;
    }
    if (preview != null && !SteamUGC.SetItemPreview(handle, preview))
        Console.Error.WriteLine("Steam refused the preview image; uploading without it.");

    SubmitItemUpdateResult_t? result = null;
    var ioFailure = false;
    using var callResult = CallResult<SubmitItemUpdateResult_t>.Create((r, failed) =>
    {
        result = r;
        ioFailure = failed;
    });
    callResult.Set(SteamUGC.SubmitItemUpdate(handle, note));

    var clock = Stopwatch.StartNew();
    var lastReport = TimeSpan.Zero;
    while (result == null)
    {
        SteamAPI.RunCallbacks();
        if (clock.Elapsed - lastReport > TimeSpan.FromSeconds(2))
        {
            lastReport = clock.Elapsed;
            var status = SteamUGC.GetItemUpdateProgress(handle, out var done, out var total);
            Console.WriteLine(total > 0 ? $"  {status}: {done * 100 / total}%" : $"  {status}");
        }
        if (clock.Elapsed > TimeSpan.FromMinutes(30))
        {
            Console.Error.WriteLine("No answer from Steam in 30 minutes.");
            return 3;
        }
        Thread.Sleep(100);
    }

    var outcome = result.Value;
    if (ioFailure || outcome.m_eResult != EResult.k_EResultOK)
    {
        Console.Error.WriteLine($"Upload failed: {(ioFailure ? "I/O failure" : outcome.m_eResult.ToString())}.");
        return 3;
    }
    if (outcome.m_bUserNeedsToAcceptWorkshopLegalAgreement)
        Console.WriteLine("Uploaded, but the Workshop legal agreement must be accepted on the item's page before it shows.");
    Console.WriteLine($"Done: https://steamcommunity.com/sharedfiles/filedetails/?id={itemId}");
    return 0;
}
finally
{
    SteamAPI.Shutdown();
}
