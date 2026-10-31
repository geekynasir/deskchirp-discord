using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.Core.Computers;

public interface IComputerLinkService
{
    event Action? Changed;
    bool CanPublish { get; }
    string Status { get; }
    string PairingCode { get; }
    bool HasSavedLink { get; }
    Task InitializeAsync(CancellationToken ct = default);
    void UpdateContext(bool available, string? discordUserId);
    Task LinkAsync(string address, string code, string name, CancellationToken ct = default);
    Task CreateCodeAsync(CancellationToken ct = default);
    Task UnlinkAsync(CancellationToken ct = default);
    Task StopAsync();
}

public sealed record ComputerCredential(string Server, string DeviceId, string GroupId, string Token);

public static class ComputerAddress
{
    public static string Normalize(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("Use a secure linking address. Plain http:// works only on this computer.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public interface IInputAgeReader { double? ReadSeconds(); }

public sealed class WindowsInputAgeReader : IInputAgeReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput data);

    public double? ReadSeconds()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var data = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref data)) return null;
        // LASTINPUTINFO is a wrapping 32-bit tick count for this OS session.
        return unchecked((uint)Environment.TickCount - data.Time) / 1000.0;
    }
}

/// <summary>Stores only device credentials; grants are memory-only and expire monotonically.</summary>
public sealed class ComputerLinkService : IComputerLinkService
{
    private const string CredentialTarget = "DeveloperPresence:computers";
    private readonly ISettingsService _settings;
    private readonly ICredentialStore _store;
    private readonly IInputAgeReader _input;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly string _runtime = Guid.NewGuid().ToString("N");
    private ComputerCredential? _credential;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _available;
    private string? _account;
    private long _contextVersion;
    private long _requestStart;
    private double _grantSeconds;
    private double _lastInput = double.NegativeInfinity;
    private long _inputSequence;
    private string _status = "Not linked";
    private string _pairingCode = "";
    public event Action? Changed;

    public ComputerLinkService(ISettingsService settings, ICredentialStore store,
        IInputAgeReader? input = null, HttpClient? http = null, TimeProvider? clock = null)
    {
        _settings = settings; _store = store;
        _input = input ?? new WindowsInputAgeReader();
        _clock = clock ?? TimeProvider.System;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(4) };
    }

    public bool CanPublish
    {
        get
        {
            if (!_settings.Settings.FollowComputers) return true;
            lock (_gate)
            {
                var age = _input.ReadSeconds();
                return _available && _credential != null && _grantSeconds > 0 &&
                    _clock.GetElapsedTime(_requestStart).TotalSeconds < _grantSeconds &&
                    age is >= 0 and < 300;
            }
        }
    }
    public string Status { get { lock (_gate) return _status; } }
    public string PairingCode { get { lock (_gate) return _pairingCode; } }
    public bool HasSavedLink { get { lock (_gate) return _credential != null; } }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var secret = await _store.ReadCredentialAsync(CredentialTarget, "device", ct);
            if (secret != null)
            {
                var credential = JsonSerializer.Deserialize<ComputerCredential>(secret);
                if (credential != null && ComputerAddress.Normalize(credential.Server) == credential.Server)
                {
                    lock (_gate) _credential = credential;
                    // A saved link is authoritative if a settings write was interrupted.
                    // Never restart in independent mode with a live device credential.
                    if (!_settings.Settings.FollowComputers)
                        await _settings.UpdateSettingsAsync(s => {
                            s.FollowComputers = true; s.ComputerLinkAddress = credential.Server;
                        });
                }
            }
        }
        catch { SetStatus("Couldn’t read your saved computer link. Try linking again.", clear: true); }
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void UpdateContext(bool available, string? discordUserId)
    {
        bool changed;
        lock (_gate)
        {
            changed = _available != available || _account != discordUserId;
            _available = available;
            _account = discordUserId;
            if (changed) { _contextVersion++; _grantSeconds = 0; }
        }
        if (changed) Changed?.Invoke();
    }

    public async Task LinkAsync(string address, string code, string name, CancellationToken ct = default)
    {
        await _actions.WaitAsync(ct);
        try
        {
            if (HasSavedLink) throw new InvalidOperationException("Unlink this computer before linking it again.");
            string server = ComputerAddress.Normalize(address);
            // Persist fail-closed mode before network or credential writes. Linking never enables sharing.
            await _settings.UpdateSettingsAsync(s => { s.FollowComputers = true; s.ComputerLinkAddress = server; });
            UpdateContext(false, null);
            var response = await PostAsync(server, "/v1/join", new { code, name }, null, ct);
            var credential = new ComputerCredential(server,
                response.GetProperty("deviceId").GetString()!, response.GetProperty("groupId").GetString()!,
                response.GetProperty("token").GetString()!);
            await _store.SaveCredentialAsync(CredentialTarget, "device", JsonSerializer.Serialize(credential), ct);
            lock (_gate) { _credential = credential; _contextVersion++; }
            SetStatus("Linked. Open Discord and turn on sharing when you’re ready.", clear: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(ex is ComputerLinkException e ? e.Message : "Couldn’t link this computer. Check the address and try a new code.", clear: true);
        }
        finally { _actions.Release(); }
    }

    public async Task CreateCodeAsync(CancellationToken ct = default)
    {
        try
        {
            ComputerCredential credential;
            lock (_gate) credential = _credential ?? throw new InvalidOperationException();
            var result = await PostAsync(credential.Server, "/v1/pair-code", new { }, credential.Token, ct);
            lock (_gate) _pairingCode = result.GetProperty("code").GetString() ?? "";
            SetStatus("Enter this code on your other computer within five minutes. It works once.");
        }
        catch { SetStatus("Couldn’t create a linking code. Check the connection and try again."); }
    }

    public async Task UnlinkAsync(CancellationToken ct = default)
    {
        await _actions.WaitAsync(ct);
        try
        {
            await _settings.UpdateSettingsAsync(s => s.EnableSharing = false);
            UpdateContext(false, null);
            ComputerCredential? credential;
            lock (_gate) credential = _credential;
            if (credential != null)
                try { await PostAsync(credential.Server, "/v1/leave", new { }, credential.Token, ct); }
                catch (ComputerLinkException e) when (e.Removed) { }
            await _store.DeleteCredentialAsync(CredentialTarget, "device", ct);
            lock (_gate) { _credential = null; _pairingCode = ""; _contextVersion++; }
            await _settings.UpdateSettingsAsync(s => s.FollowComputers = false);
            SetStatus("Not linked. Sharing is off on this computer.", clear: true);
        }
        catch { SetStatus("Couldn’t unlink this computer. Sharing is off; check the connection and try again.", clear: true); }
        finally { _actions.Release(); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ComputerLinkException ex) { SetStatus(ex.Message, clear: true); }
            catch { SetStatus("Waiting for a connection", clear: true); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (!_settings.Settings.FollowComputers) return;
        ComputerCredential? credential;
        bool available;
        string account;
        double age;
        long context, sequence;
        lock (_gate)
        {
            credential = _credential;
            var input = _input.ReadSeconds();
            age = input is >= 0 and <= 604800 ? input.Value : 604800;
            available = _available && input.HasValue && _account != null;
            account = _account != null && credential != null
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.GroupId + ":" + _account))).ToLowerInvariant() : "";
            double instant = _clock.GetElapsedTime(0).TotalSeconds - age;
            if (instant > _lastInput + .3) { _lastInput = instant; _inputSequence++; }
            context = _contextVersion; sequence = _inputSequence;
        }
        if (credential == null)
        {
            if (Status == "Not linked") SetStatus("Not linked. Enter a linking code to continue.", clear: true);
            return;
        }
        long start = _clock.GetTimestamp();
        var reply = await PostAsync(credential.Server, "/v1/heartbeat", new {
            runtimeId = _runtime, inputSequence = sequence, inputAge = Math.Floor(age), available, accountKey = account
        }, credential.Token, ct);
        lock (_gate)
        {
            if (_credential != credential || context != _contextVersion || !_settings.Settings.FollowComputers) return;
            double validity = reply.GetProperty("validFor").GetDouble();
            bool allowed = reply.GetProperty("allowed").GetBoolean() && available && validity is > 3 and <= 15;
            _requestStart = start;
            _grantSeconds = allowed ? validity - 3 : 0;
            string? selected = reply.GetProperty("selectedName").GetString();
            _status = allowed && _clock.GetElapsedTime(start).TotalSeconds < _grantSeconds ? "Selected computer: this computer"
                : reply.GetProperty("switching").GetBoolean() ? "Switching computers…"
                : selected != null ? $"Selected computer: {selected}" : "No computer is sharing";
        }
        Changed?.Invoke();
    }

    private void SetStatus(string value, bool clear = false)
    {
        lock (_gate) { _status = value; if (clear) _grantSeconds = 0; }
        Changed?.Invoke();
    }

    private async Task<JsonElement> PostAsync(string server, string path, object body, string? token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, ComputerAddress.Normalize(server) + path);
        // The bounded private protocol requires Content-Length, not chunked JSON.
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode == System.Net.HttpStatusCode.Conflict
                ? "Check that Discord uses the same account on each computer and only one copy of Developer Presence is open."
                : response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "This computer’s link is no longer available. Unlink it, then use a new code."
                : "Couldn’t reach the linking service. Check the address or try a new code.";
            throw new ComputerLinkException(message, response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 8192) throw new InvalidDataException();
            buffer.Write(chunk, 0, read);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    public async Task StopAsync()
    {
        UpdateContext(false, null);
        _cts?.Cancel();
        if (_loop != null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _cts?.Dispose(); _cts = null;
    }
    private sealed class ComputerLinkException(string message, bool removed = false) : Exception(message)
    { public bool Removed { get; } = removed; }
}
