using System.IO.Compression;

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}
static void Reject(Action action, string label)
{
    try { action(); } catch (InvalidDataException) { Check(true, label); return; }
    throw new Exception("Accepted: " + label);
}

var root = Directory.CreateTempSubdirectory("kinetik-fonts-security-").FullName;
try
{
    var config = new Config { DownloadDir = root, Folders = { root } };
    Check(Library.Unpack(Library.Pack(config)).Folders.Single() == root, "ordinary local backup");
    foreach (string path in new[] { @"\\untrusted.invalid\share", @"\\?\UNC\untrusted.invalid\share", @"C:relative", @"\relative" })
    {
        config.Folders[0] = path;
        Reject(() => Library.Unpack(Library.Pack(config)), "reject nonlocal or ambiguous path");
    }
    var target = Path.Combine(root, "not-a-directory.exe");
    File.WriteAllText(target, "");
    config.Folders[0] = target;
    Reject(() => Library.Unpack(Library.Pack(config)), "reject a file presented as a folder");
    config.Folders.Clear();
    config.Enabled.Add(@"\\untrusted.invalid\share\sample.ttf");
    Reject(() => Library.Unpack(Library.Pack(config)), "reject remote enabled font before probing it");
    config.Enabled.Clear(); config.DownloadDir = @"\\untrusted.invalid\share";
    Reject(() => Library.Unpack(Library.Pack(config)), "reject remote download root");

    var malformed = new byte[28]; malformed[1] = 1; malformed[5] = 1;
    "name"u8.CopyTo(malformed.AsSpan(12)); malformed[23] = 28; malformed[24] = 0x7f;
    var pathFont = Path.Combine(root, "malformed.ttf"); File.WriteAllBytes(pathFont, malformed);
    Reject(() => Library.FaceInfo(pathFont), "reject out-of-file metadata length without allocating it");
    string disguised = new string('a',76) + ".cmd.ttf";
    Check(Online.FontName(disguised).Length == 80 && Online.FontName(disguised).EndsWith(".ttf"), "keep extension after truncation");
    string script = Path.Combine(root, "sample.cmd"); File.WriteAllText(script, "OTTO");
    Check(!Online.IsFont(script), "reject old non-font cache file even with font signature");

    using var bytes = new MemoryStream();
    using (var z = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        foreach (var name in new[] { "regular.ttf", "bold.ttf" })
        { using var stream = z.CreateEntry(name).Open(); stream.Write("OTTOdata"u8); }
    bytes.Position = 0;
    var forged = bytes.ToArray();
    // Undercount both EOCD totals while retaining both actual central headers.
    forged[^14] = forged[^12] = 1;
    Reject(() => Online.OpenBoundedArchive(new MemoryStream(forged)), "reject a forged directory count before opening ZIP");
    using var zip = Online.OpenBoundedArchive(bytes);
    Reject(() => Online.ExtractFonts(zip, root, false, default, 12).GetAwaiter().GetResult(), "enforce cumulative ZIP budget");
    Check(!File.Exists(Path.Combine(root, "regular.ttf")), "no partial extraction on limit");
    Check((await Online.ExtractFonts(zip, root, false, default, 16)).Count == 2, "ordinary bounded archive");

    var cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
    var victim = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
    string important = Path.Combine(victim, "keep"); File.WriteAllText(important, "");
    var junction = Path.Combine(cache, "link");
    var sys = Environment.SystemDirectory;
    using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(sys, "cmd.exe"),
        $"/d /c mklink /J \"{junction}\" \"{victim}\"") { UseShellExecute = false, CreateNoWindow = true }))
    { mk.WaitForExit(); Check(mk.ExitCode == 0, "create test junction"); }
    try
    {
        Check(!Library.LocalPath(junction), "reject linked imported root");
        File.WriteAllText(Path.Combine(cache, "cached.ttf"), "");
        if (Native.IsUserAnAdmin())
        {
            try { Online.ClearPreviewCache(cache); throw new Exception("elevated cleanup ran"); }
            catch (UnauthorizedAccessException) { Check(true, "refuse elevated cleanup"); }
        }
        else Check(Online.ClearPreviewCache(cache) == 1, "remove only ordinary cache files");
        Check(File.Exists(important), "preserve junction destination");
    }
    finally { Directory.Delete(junction); }
}
finally { Directory.Delete(root, true); }
