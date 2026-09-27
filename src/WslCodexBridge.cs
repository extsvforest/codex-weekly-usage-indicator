using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WeeklyUsageIndicator;

internal sealed class WslCodexBridge : IWslCodexBridge
{
    private const int MaxOutput = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken token)
    {
        try
        {
            var start = BaseStart();
            foreach (var argument in new[] { "--list", "--running", "--quiet" }) start.ArgumentList.Add(argument);
            var bytes = await RunAsync(start, [], TimeSpan.FromSeconds(8), 32768, token).ConfigureAwait(false);
            try
            {
                var text = bytes.Contains((byte)0) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
                return text.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 128 && !s.Any(char.IsControl))
                    .Distinct(StringComparer.Ordinal).Take(32).ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return []; } // WSL itself is optional. Never install or start a distribution to discover it.
    }

    public async Task<WslCodexProbe> ProbeAsync(string distribution, WslCodexTarget? expected, CancellationToken token)
    {
        if (!(await ListRunningDistributionsAsync(token).ConfigureAwait(false)).Contains(distribution, StringComparer.Ordinal))
            return new(distribution, WslCodexAvailability.NotRunning, null, 0, "not-running");
        try
        {
            using var reply = await CallAsync("probe", distribution, expected, null, null, token).ConfigureAwait(false);
            var root = reply.RootElement;
            var target = root.GetProperty("target").Deserialize<WslCodexTarget>()!;
            target.Validate();
            var writers = root.GetProperty("writers").GetInt32();
            if (writers is < 0 or > 65536) throw new InvalidDataException();
            return new(distribution, writers == 0 ? WslCodexAvailability.Ready : WslCodexAvailability.Busy,
                target, writers, writers == 0 ? "ready" : "writers-running");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (WslCodexException e)
        {
            return new(distribution, e.Code == "missing-cli" ? WslCodexAvailability.MissingCli :
                WslCodexAvailability.Unsupported, null, 0, e.Code);
        }
        catch { return new(distribution, WslCodexAvailability.Unavailable, null, 0, "probe-failed"); }
    }

    public async Task<byte[]> ReadAuthAsync(WslCodexTarget target, CancellationToken token)
    {
        using var reply = await CallAsync("read", target.Distribution, target, null, null, token).ConfigureAwait(false);
        return ReadAuth(reply);
    }

    public async Task ReplaceAuthAsync(WslCodexTarget target, string expectedDigest, byte[] auth, CancellationToken token)
    {
        using var reply = await CallAsync("replace", target.Distribution, target, expectedDigest, auth, token).ConfigureAwait(false);
    }

    public async Task<bool> VerifyOfficialClientAsync(WslCodexTarget target, CancellationToken token)
    {
        using var reply = await CallAsync("verify", target.Distribution, target, null, null, token).ConfigureAwait(false);
        return reply.RootElement.GetProperty("verified").GetBoolean();
    }

    public async Task<byte[]> LoginAsync(WslCodexTarget target, CancellationToken token, Action<Uri>? onAuthorizationUrl = null)
    {
        using var reply = await CallAsync("login", target.Distribution, target, null, null, token, onAuthorizationUrl).ConfigureAwait(false);
        return ReadAuth(reply);
    }

    internal static bool IsAuthorizationUrl(Uri url) => url.IsAbsoluteUri &&
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase) &&
        url.Port == 443 && url.UserInfo.Length == 0 && url.Fragment.Length == 0 &&
        url.AbsolutePath == "/oauth/authorize" && url.Query.Length > 1 &&
        url.OriginalString.Length <= 16384 && url.IsWellFormedOriginalString() &&
        !url.OriginalString.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\');

    private static byte[] ReadAuth(JsonDocument reply)
    {
        var bytes = reply.RootElement.GetProperty("auth").GetBytesFromBase64();
        if (bytes.Length is <= 0 or > 1024 * 1024)
        { CryptographicOperations.ZeroMemory(bytes); throw new WslCodexException("invalid-auth", "WSL 로그인 형식을 확인하지 못했습니다."); }
        return bytes;
    }

    private async Task<JsonDocument> CallAsync(string action, string distribution, WslCodexTarget? target,
        string? expectedDigest, byte[]? auth, CancellationToken token, Action<Uri>? onAuthorizationUrl = null)
    {
        if (!(await ListRunningDistributionsAsync(token).ConfigureAwait(false)).Contains(distribution, StringComparer.Ordinal))
            throw new WslCodexException("not-running", "연결한 WSL 배포판이 실행 중이 아닙니다. WSL을 연 뒤 다시 시도하세요.");
        target?.Validate();
        var start = CreateHelperStartInfo(distribution, target?.UserName);
        var input = JsonSerializer.SerializeToUtf8Bytes(new { action, distribution, target, expectedDigest, auth });
        byte[]? output = null;
        try
        {
            output = action == "login"
                ? await RunLoginAsync(start, input, TimeSpan.FromMinutes(10), MaxOutput, token, onAuthorizationUrl).ConfigureAwait(false)
                : await RunAsync(start, input, action == "verify" ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(20),
                    MaxOutput, token).ConfigureAwait(false);
            return ParseReply(output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (WslCodexException) { throw; }
        catch { throw new WslCodexException("helper-failed", "WSL Codex 작업을 완료하지 못했습니다. 현재 로그인과 복구 기록은 보존했습니다."); }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (output is not null) CryptographicOperations.ZeroMemory(output);
        }
    }

    internal static JsonDocument ParseReply(byte[] output)
    {
        // Parse(ReadOnlyMemory<byte>) borrows the caller's buffer. CallAsync clears that buffer
        // before returning, so parse a stream instead: the document owns its copied input.
        using var stream = new MemoryStream(output, writable: false);
        var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        if (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            var code = document.RootElement.TryGetProperty("code", out var field) ? field.GetString() : null;
            document.Dispose();
            // Only a closed vocabulary can leave the helper. Never echo diagnostics or JSON.
            code = KnownCodes.Contains(code ?? "") ? code : "helper-failed";
            throw new WslCodexException(code!, Message(code!));
        }
        return document;
    }

    internal static ProcessStartInfo CreateHelperStartInfo(string distribution, string? user)
    {
        if (string.IsNullOrWhiteSpace(distribution) || distribution.Length > 128 || distribution.Any(char.IsControl) ||
            (user is not null && (string.IsNullOrWhiteSpace(user) || user.Length > 128 || user.Any(char.IsControl))))
            throw new ArgumentException("Invalid WSL target.");
        var start = BaseStart();
        start.ArgumentList.Add("--distribution"); start.ArgumentList.Add(distribution);
        if (user is not null) { start.ArgumentList.Add("--user"); start.ArgumentList.Add(user); }
        start.ArgumentList.Add("--cd"); start.ArgumentList.Add("~");
        start.ArgumentList.Add("--exec"); start.ArgumentList.Add("/usr/bin/python3");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(WslCodexLinuxHelper.Source);
        return start;
    }

    private static ProcessStartInfo BaseStart() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    };

    private static Task<byte[]> RunAsync(ProcessStartInfo start, byte[] input, TimeSpan limit, int maxOutput, CancellationToken token) =>
        RunCoreAsync(start, input, limit, maxOutput, token, false, null);

    private static Task<byte[]> RunLoginAsync(ProcessStartInfo start, byte[] input, TimeSpan limit, int maxOutput,
        CancellationToken token, Action<Uri>? onAuthorizationUrl) =>
        RunCoreAsync(start, input, limit, maxOutput, token, true, onAuthorizationUrl);

    private static async Task<byte[]> RunCoreAsync(ProcessStartInfo start, byte[] input, TimeSpan limit, int maxOutput,
        CancellationToken token, bool login, Action<Uri>? onAuthorizationUrl)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(limit);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("WSL helper could not start.");
        var stdout = login
            ? ReadLoginOutputAsync(process.StandardOutput.BaseStream, maxOutput, onAuthorizationUrl, linked.Token)
            : ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutput, linked.Token);
        var stderr = DrainAsync(process.StandardError.BaseStream, linked.Token);
        var delivered = false;
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, linked.Token).ConfigureAwait(false);
            if (input.Length == 0) process.StandardInput.Close();
            else
            {
                // Keep the private pipe open as a lifetime lease. Linux watches EOF as well as
                // parent death, so cancellation cannot leave an owned OAuth/app-server alive.
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, linked.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            var exit = process.WaitForExitAsync(linked.Token);
            // A rejected/oversized progress stream must close its lifetime lease promptly,
            // rather than leaving the CLI waiting for a browser until the login timeout.
            if (await Task.WhenAny(exit, stdout).ConfigureAwait(false) == stdout && !stdout.IsCompletedSuccessfully)
                await stdout.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0) { CryptographicOperations.ZeroMemory(output); throw new IOException("WSL helper failed."); }
            delivered = true;
            return output;
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { /* This never targets an existing WSL Codex process. Helper owns only its child group. */ }
            }
            linked.Cancel();
            try { await stderr.ConfigureAwait(false); } catch { }
            try
            {
                var output = await stdout.ConfigureAwait(false);
                if (!delivered) CryptographicOperations.ZeroMemory(output);
            }
            catch { }
        }
    }

    // Login URLs exist only in the private pipe and caller's temporary UI state. Never return
    // progress packets with the final authentication document or include raw output in errors.
    internal static async Task<byte[]> ReadLoginOutputAsync(Stream stream, int maximum,
        Action<Uri>? onAuthorizationUrl, CancellationToken token)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        byte[]? final = null;
        var total = 0;
        try
        {
            while (true)
            {
                var length = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (length == 0) break;
                total += length;
                if (total > maximum) throw new IOException("WSL login response exceeded its bound.");
                for (var i = 0; i < length; i++)
                {
                    if (buffer[i] == (byte)'\n') ReadFrame();
                    else line.WriteByte(buffer[i]);
                }
            }
            if (line.Length != 0) ReadFrame();
            return final ?? throw new IOException("WSL login response was incomplete.");
        }
        catch
        {
            if (final is not null) CryptographicOperations.ZeroMemory(final);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (line.TryGetBuffer(out var contents)) CryptographicOperations.ZeroMemory(contents.AsSpan());
        }

        void ReadFrame()
        {
            if (line.Length == 0) return;
            if (final is not null) throw new IOException("WSL login response was invalid.");
            try
            {
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(line.GetBuffer(), 0, (int)line.Length),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new IOException("WSL login response was invalid.");
                if (root.TryGetProperty("loginUrl", out var progress))
                {
                    if (root.EnumerateObject().Count() != 1 || progress.ValueKind != JsonValueKind.String)
                        throw new IOException("WSL login response was invalid.");
                    if (Uri.TryCreate(progress.GetString(), UriKind.Absolute, out var url) && IsAuthorizationUrl(url))
                    {
                        try { onAuthorizationUrl?.Invoke(url); }
                        catch { throw new IOException("WSL login window is no longer available."); }
                    }
                }
                else if (root.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    final = line.ToArray();
                else throw new IOException("WSL login response was invalid.");
            }
            catch (JsonException) { throw new IOException("WSL login response was invalid."); }
            finally
            {
                if (line.TryGetBuffer(out var contents)) CryptographicOperations.ZeroMemory(contents.AsSpan());
                line.SetLength(0);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var length = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (length == 0) return memory.ToArray();
                if (memory.Length + length > maximum) throw new IOException("WSL helper output exceeded its bound.");
                memory.Write(buffer, 0, length);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (memory.TryGetBuffer(out var contents)) CryptographicOperations.ZeroMemory(contents.AsSpan());
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0) CryptographicOperations.ZeroMemory(buffer); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    { "missing-cli", "unsupported", "managed-config", "store-mode", "linked-path", "writers-running", "writers-unknown",
      "environment-changed", "missing-auth", "invalid-auth", "unsafe-permissions", "auth-changed", "verify-failed", "login-failed" };

    internal static string Message(string code) => code switch
    {
        "missing-cli" => "이 WSL 환경에서 Linux용 Codex CLI를 찾지 못했습니다. 설치하거나 전환하지 않았습니다.",
        "writers-running" => "WSL에서 Codex 또는 Codex 연결 프로세스가 실행 중입니다. 작업을 마치고 종료한 뒤 다시 시도하세요.",
        "writers-unknown" => "WSL Codex 실행 상태를 확인할 수 없어 전환하지 않았습니다.",
        "managed-config" or "store-mode" => "이 WSL 환경의 인증 저장 방식은 지원하지 않습니다. 설정은 변경하지 않았습니다.",
        "auth-changed" => "WSL 로그인이 작업 중 바뀌어 전환을 멈췄습니다. 현재 로그인은 보존했습니다.",
        "missing-auth" => "WSL Codex에 먼저 로그인한 뒤 현재 계정을 연결하세요.",
        "login-failed" => "WSL Codex 로그인이 완료되지 않았습니다. 브라우저 로그인 후 다시 연결해 주세요.",
        "verify-failed" => "WSL 로그인 파일을 교체했지만 새 Codex 연결의 계정을 확인하지 못했습니다. 복구를 다시 실행하세요.",
        _ => "지원되는 개인용 WSL Codex 환경인지 확인하지 못해 작업을 멈췄습니다."
    };
}
