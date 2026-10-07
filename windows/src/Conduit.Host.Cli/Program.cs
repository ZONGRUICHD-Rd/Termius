using Conduit.Host.Core;
using QRCoder;

// Headless host: same engine as the WinUI app, handy for servers, WSL or debugging.
//   conduit-host [--data <dir>] [--port <n>] [--relay <wss://…>] [--demo]
string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var runtime = new HostRuntime(Arg("--data") ?? HostRuntime.DefaultDataDirectory());
runtime.Update(c =>
{
    if (Arg("--port") is { } port) c.Port = int.Parse(port);
    if (Arg("--relay") is { } relay) c.RelayUrl = relay;
    if (args.Contains("--demo")) c.EnableEchoAgent = true;
});
runtime.LogAdded += Console.WriteLine;
await runtime.StartAsync();

var link = runtime.PairingLink();
using (var gen = new QRCodeGenerator())
using (var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.L))
{
    Console.WriteLine(new AsciiQRCode(data).GetGraphic(1, drawQuietZones: true));
}
Console.WriteLine($"Host id   : {runtime.Identity.HostId}");
Console.WriteLine($"Endpoints : {string.Join(", ", runtime.Endpoints().Select(e => $"{e.Url} ({e.Kind})"))}");
Console.WriteLine($"Relay     : {runtime.Config.RelayUrl ?? "(none)"}");
Console.WriteLine($"Pair link : {link}");
Console.WriteLine("Scan the QR code in the Conduit app (valid 30 min). Ctrl+C to quit.");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
await done.Task;
await runtime.DisposeAsync();
