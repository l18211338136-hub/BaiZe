using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BaiZe.Infrastructure.FunAsr;

/// <summary>
/// 本地 funasr-server 全生命周期托管：
/// 1) 确认 Python → 2) 首次自动创建 venv 并 pip 安装依赖 → 3) 端口被占自动换端口
/// → 4) 拉起内嵌 server.py → 5) TCP 健康检查 → 6) 把实际端口回写 FunAsrOptions
/// → 7) 应用停止时 Kill 进程树。
/// </summary>
public sealed class FunAsrServerHost : IHostedService
{
    private readonly FunAsrOptions _options;
    private readonly ILogger<FunAsrServerHost> _logger;
    private Process? _process;
    private string _setupLog = "";    // 安装/托管过程日志
    private string _serverLog = "";   // funasr-server 运行日志（隐藏窗口模式下重定向目标）

    public FunAsrServerHost(FunAsrOptions options, ILogger<FunAsrServerHost> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.AutoStart)
        {
            _logger.LogInformation("FunASR 自启未开启（AutoStart=false），假定服务外部运行于 {Ws}", _options.WsUrl);
            return;
        }

        try
        {
            await StartInternalAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 托管失败不阻断应用启动；客户端连接失败时 UI 会提示
            _logger.LogError(ex, "funasr-server 托管失败");
        }
    }

    private async Task StartInternalAsync()
    {
        // venv 默认放用户目录（避免安装到 Program Files 只读位置）
        if (string.IsNullOrWhiteSpace(_options.VenvDir))
            _options.VenvDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BaiZe", "funasr", "venv");

        // 日志目录：与 venv 同级（默认 %LOCALAPPDATA%\BaiZe\funasr\logs）
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_options.VenvDir))
                   ?? AppContext.BaseDirectory;
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        _setupLog = Path.Combine(logDir, "funasr-setup.log");
        _serverLog = Path.Combine(logDir, "funasr-server.log");
        AppendLog(_setupLog, $"===== funasr-server 托管开始 =====");

        // ---- 1. 找到 Python 解释器 ----
        var python = ResolvePython()
            ?? throw new InvalidOperationException("未找到可用的 Python（>=3.8），请安装 Python 或在配置里指定 FunAsr:PythonPath。");
        _logger.LogInformation("使用 Python: {Python}", python);
        AppendLog(_setupLog, $"使用 Python: {python}");

        // ---- 2. 确保独立虚拟环境（首次创建）----
        var venvPython = GetVenvPython(_options.VenvDir);
        if (!File.Exists(venvPython))
        {
            _logger.LogInformation("首次运行：创建 FunASR 独立虚拟环境 → {Dir}", _options.VenvDir);
            AppendLog(_setupLog, $"创建虚拟环境: {_options.VenvDir}");
            Directory.CreateDirectory(_options.VenvDir);
            await RunProcessAsync(python, $"-m venv \"{_options.VenvDir}\"", timeoutSec: 300).ConfigureAwait(false);
        }

        // ---- 3. 自动安装依赖（已装齐则跳过 pip，秒级探测）----
        if (_options.AutoInstall)
        {
            var requirements = Path.Combine(AppContext.BaseDirectory, "funasr", "requirements.txt");
            if (await AreDepsInstalledAsync(venvPython).ConfigureAwait(false))
            {
                _logger.LogInformation("FunASR 依赖已装齐，跳过 pip 检查");
            }
            else
            {
                _logger.LogInformation("检查/安装 FunASR 依赖（首次需下载 torch 等，可能耗时较长）...");
                var index = string.IsNullOrWhiteSpace(_options.PipIndexUrl)
                    ? ""
                    : $" -i {_options.PipIndexUrl}";
                // -X utf8：中文 Windows 默认 GBK 编码，pip 读含非 ASCII 的文件会 UnicodeDecodeError
                // 不用 -q（否则无输出无法判断是否在跑）；--progress-bar off 避免 \r 进度条重定向后变乱码，
                // Collecting/Downloading（含包大小）逐行经日志输出
                await RunProcessAsync(venvPython,
                    $"-X utf8 -m pip install -r \"{requirements}\"{index} --progress-bar off --disable-pip-version-check",
                    timeoutSec: _options.InstallTimeoutMinutes * 60,
                    progressLabel: "pip 安装 FunASR 依赖").ConfigureAwait(false);
                _logger.LogInformation("FunASR 依赖就绪");
            }

            // GPU 模式：Windows 上 PyPI 的 torch 默认是 CPU 构建，需从 PyTorch 官方索引换装 CUDA 构建
            if (!string.Equals(_options.Device, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCudaTorchInstalled(_options.VenvDir))
                {
                    _logger.LogInformation("torch 已是 CUDA 构建，跳过换装");
                }
                else
                {
                    // 优先本地 wheel 缓存（镜像预下载/上次的断点续传，秒装；官方 CDN 国内又慢又不稳）
                    var wheelDir = ResolveTorchWheelDir();
                    var torchWhl = FindWheel(wheelDir, "torch-*");
                    var audioWhl = FindWheel(wheelDir, "torchaudio-*");

                    // wheel 是 zip 包，中央目录在文件尾——下到一半的残件读不出来
                    if (torchWhl is not null && !IsWheelValid(torchWhl))
                    {
                        if (string.IsNullOrWhiteSpace(_options.TorchWheelUrl))
                            throw new InvalidOperationException(
                                $"本地 torch wheel 尚未下载完成（{torchWhl}），且未配置 FunAsr:TorchWheelUrl 自动下载；" +
                                "或临时把 FunAsr:Device 设为 cpu 跳过 GPU 换装。");
                        _logger.LogInformation("本地 torch wheel 不完整（{MB:F0} MB），续传补完...", new FileInfo(torchWhl).Length / 1048576.0);
                        torchWhl = null;
                    }
                    if (audioWhl is not null && !IsWheelValid(audioWhl)) audioWhl = null;

                    // 本地没有完整 wheel 且配置了镜像 URL → 自动断点续传下载
                    if (torchWhl is null && !string.IsNullOrWhiteSpace(_options.TorchWheelUrl))
                        torchWhl = await DownloadWheelAsync(_options.TorchWheelUrl, wheelDir).ConfigureAwait(false);
                    if (torchWhl is not null && audioWhl is null && !string.IsNullOrWhiteSpace(_options.TorchaudioWheelUrl))
                        audioWhl = await DownloadWheelAsync(_options.TorchaudioWheelUrl, wheelDir).ConfigureAwait(false);

                    if (torchWhl is not null)
                    {
                        _logger.LogInformation("从本地 wheel 安装 CUDA 版 torch: {Whl}", torchWhl);
                        AppendLog(_setupLog, $"本地 wheel 安装: {torchWhl}");
                        var args = $"-X utf8 -m pip install \"{torchWhl}\"";
                        if (audioWhl is not null) args += $" \"{audioWhl}\"";
                        args += " --force-reinstall --no-deps --progress-bar off --disable-pip-version-check";
                        await RunProcessAsync(venvPython, args,
                            timeoutSec: _options.InstallTimeoutMinutes * 60,
                            progressLabel: "pip 本地安装 CUDA 版 torch").ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation("换装 CUDA 版 torch（约 2.5GB，索引 {Index}）...", _options.TorchIndexUrl);
                        AppendLog(_setupLog, $"换装 CUDA 版 torch: {_options.TorchIndexUrl}");
                        await RunProcessAsync(venvPython,
                            $"-X utf8 -m pip install torch torchaudio --force-reinstall --no-deps --index-url {_options.TorchIndexUrl} --progress-bar off --disable-pip-version-check",
                            timeoutSec: _options.InstallTimeoutMinutes * 60,
                            progressLabel: "pip 安装 CUDA 版 torch").ConfigureAwait(false);
                    }
                    _logger.LogInformation("CUDA 版 torch 就绪");
                }
            }
        }

        // ---- 4. 端口分配：被占用则换新端口 ----
        var port = new Uri(_options.WsUrl).Port;
        if (IsPortOpen(port))
        {
            _logger.LogWarning("端口 {Port} 已被占用，自动寻找新端口...", port);
            AppendLog(_setupLog, $"端口 {port} 已被占用，自动寻找新端口...");
            port = FindFreePort(port + 1, tryCount: 50)
                ?? throw new InvalidOperationException($"端口 {port} 被占用，且 {port+1}~{port+50} 范围内无空闲端口。");
            _logger.LogInformation("改用端口 {Port}", port);
            AppendLog(_setupLog, $"改用端口 {port}");
        }

        // ---- 5. 拉起内嵌 Python 服务 ----
        var script = Path.Combine(AppContext.BaseDirectory, "funasr", "server.py");
        var spkArg = string.IsNullOrWhiteSpace(_options.SpkModel) ? "" : $" --spk {_options.SpkModel}";
        // 重叠语音分离模型（两人同时说话）；拿不到就以缺失处理，不影响常规识别
        var sepModel = await ResolveSepModelAsync().ConfigureAwait(false);
        var sepArg = string.IsNullOrWhiteSpace(sepModel) ? "" : $" --sep \"{sepModel}\"";
        // -X utf8：统一 UTF-8 模式，避免中文 Windows GBK 编码问题
        var serverArgs = $"-X utf8 \"{script}\" --port {port} --device {_options.Device}{spkArg}{sepArg}";
        // onnxruntime 的 CUDA provider 要靠 PATH 才能找到 nvidia DLL，详见 BuildEnvPath
        var envPath = BuildEnvPath();

        if (_options.ShowWindow)
        {
            // 独立 cmd 控制台窗口：Python 日志实时可见；/k 使异常退出后窗口保留错误信息便于排查。
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k title BaiZe funasr-server && set \"PATH={envPath}\" && \"{venvPython}\" {serverArgs}",
                UseShellExecute = true
            });
            _logger.LogInformation("funasr-server 已在独立控制台窗口启动（端口 {Port}），日志同步写入 {Log}", port, _serverLog);
        }
        else
        {
            // 隐藏窗口：stdout/stderr 重定向到 ILogger + 日志文件
            // PATH 必须在发起启动前写进 ProcessStartInfo（进程起来后再改 Environment 已经无效）
            var psi = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = serverArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            psi.Environment["PATH"] = envPath;
            _process = Process.Start(psi);
            if (_process is null)
                throw new InvalidOperationException("funasr-server 进程启动失败。");

            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogInformation("[funasr] {Line}", e.Data);
                AppendLog(_serverLog, e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogWarning("[funasr] {Line}", e.Data);
                AppendLog(_serverLog, e.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        if (_process is null)
            throw new InvalidOperationException("funasr-server 进程启动失败。");
        AppendLog(_setupLog, $"funasr-server 启动: {venvPython} {serverArgs}（ShowWindow={_options.ShowWindow}）");

        // ---- 6. 健康检查（首次加载/下载模型可能很慢）----
        // 用真正的 WebSocket 握手探测：裸 TCP 连接即断会被 websockets 服务端记为 handshake 失败（EOFError 噪音）
        var deadline = DateTime.UtcNow.AddSeconds(_options.HealthTimeoutSeconds);
        var waited = 0;
        var ready = false;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"funasr-server 提前退出，ExitCode={_process.ExitCode}，请检查日志。");
            if (await IsWsReadyAsync(port).ConfigureAwait(false))
            {
                ready = true;
                _logger.LogInformation("funasr-server 就绪: ws://127.0.0.1:{Port}", port);
                AppendLog(_setupLog, $"funasr-server 就绪: ws://127.0.0.1:{port}");
                break;
            }
            await Task.Delay(1000).ConfigureAwait(false);
            waited++;
            // 心跳：每 30s 报告等待进度（首次需下载 GB 级模型）
            if (waited % 30 == 0)
            {
                var beat = $"funasr-server 启动中，已等待 {waited} 秒（首次需下载模型，请耐心等候）...";
                _logger.LogInformation("[进度] {Beat}", beat);
                AppendLog(_setupLog, beat);
            }
        }

        if (!ready)
            throw new InvalidOperationException($"funasr-server 健康检查超时（{_options.HealthTimeoutSeconds}s），端口 {port} 未监听。");

        // ---- 7. 把实际端口回写配置（客户端在连接时动态读取）----
        _options.WsUrl = $"ws://127.0.0.1:{port}";
        _options.HttpBaseUrl = $"http://127.0.0.1:{port}";
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _logger.LogInformation("funasr-server 进程已回收");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "回收 funasr-server 进程失败");
            }
        }
        _process?.Dispose();
        _process = null;
        return Task.CompletedTask;
    }

    // ---- helpers ----

    private string? ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(_options.PythonPath))
        {
            if (File.Exists(_options.PythonPath)) return _options.PythonPath;
            // 可能是 PATH 上的名字（python / py），用 where/which 验证
            var probe = ProbePath(_options.PythonPath);
            if (probe is not null) return probe;
        }
        return ProbePath("python") ?? ProbePath("py");
    }

    private static string? ProbePath(string exe)
    {
        try
        {
            var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var psi = new ProcessStartInfo
            {
                FileName = isWin ? "where.exe" : "which",
                Arguments = exe,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var line = p?.StandardOutput.ReadLine();
            p?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string GetVenvPython(string venvDir)
    {
        var rel = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine("Scripts", "python.exe")
            : Path.Combine("bin", "python");
        return Path.Combine(venvDir, rel);
    }

    private async Task RunProcessAsync(string fileName, string arguments, int timeoutSec, string? progressLabel = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"进程启动失败: {fileName} {arguments}");

        var downloadWatchStarted = false;   // 每个 pip 进程只起一个下载进度监视器
        // 安装/venv 过程输出：ILogger + 落盘 setup 日志（pip 报错可追溯）
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogInformation("[安装] {Line}", e.Data);
            AppendLog(_setupLog, e.Data);
            // pip 关了进度条（--progress-bar off，重定向下 \r 条会变乱码），
            // 改为在 "Downloading" 行出现时监视临时文件大小自算进度
            if (!downloadWatchStarted && e.Data.Contains("Downloading "))
            {
                downloadWatchStarted = true;
                var m = Regex.Match(e.Data, @"\(([\d.]+)\s*(GB|MB|kB)\)");
                var totalMB = m.Success
                    ? m.Groups[2].Value switch
                    {
                        "GB" => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1024,
                        "MB" => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        _ => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1024
                    }
                    : 0;   // 总大小解析失败也监视，只报 MB
                _ = WatchDownloadAsync(totalMB, p);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogWarning("[安装] {Line}", e.Data);
            AppendLog(_setupLog, e.Data);
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var exitTask = p.WaitForExitAsync(timeoutCts.Token);

        // 心跳：每 30s 报告一次进程仍在运行（子进程可能长时间无输出，如 pip 下载大包）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (done == exitTask) break;
            var beat = $"{(progressLabel ?? Path.GetFileName(fileName))} 仍在运行（已 {sw.Elapsed.TotalSeconds:F0} 秒）...";
            _logger.LogInformation("[进度] {Beat}", beat);
            AppendLog(_setupLog, beat);
        }

        try
        {
            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"进程超时未完成: {fileName} {arguments}");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"进程退出码 {p.ExitCode}: {fileName} {arguments}");
    }

    /// <summary>自动下载 CUDA wheel 到缓存目录（断点续传 + 卡死看门狗 + 自动重连）。
    /// 文件已完整（zip 校验通过）直接返回；有残件则从已有字节处 Range 续传；
    /// 60 秒收不到任何字节（含响应头）判定链路卡死，自动从断点重连（最多 6 次）；
    /// 下载完校验，防止把残件交给 pip。同一文件被其他进程占用时明确报错。</summary>
    private async Task<string> DownloadWheelAsync(string url, string dir)
    {
        Directory.CreateDirectory(dir);
        // URL 里 %2B(+号) 需还原，落盘文件名与 pip 期望一致
        var fileName = Uri.UnescapeDataString(new Uri(url).Segments[^1]);
        var dest = Path.Combine(dir, fileName);

        if (File.Exists(dest) && IsWheelValid(dest))
        {
            _logger.LogInformation("wheel 已完整，跳过下载: {File}", dest);
            return dest;
        }

        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            // 每次尝试都从磁盘实际字节数续传（上一次尝试可能又下了一部分）
            var existing = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            try
            {
                return await DownloadWheelAttemptAsync(url, dest, existing).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning("[进度] CUDA wheel 下载中断（第 {Attempt}/{Max} 次）：{Reason}，5 秒后从 {MB:F0} MB 断点重连...",
                    attempt, maxAttempts, ex.Message, existing / 1048576.0);
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>单次下载尝试（从 existing 字节处续传）。链路卡死由 stallCts 看门狗兜底：
    /// 响应头和每一块数据共享 60 秒无数据超时，收到数据即重置。</summary>
    private async Task<string> DownloadWheelAttemptAsync(string url, string dest, long existing)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        // 总超时交给人肉不可靠的链路看门狗：整体下载可能合法耗时 20+ 分钟，不能用 HttpClient 默认 100s
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 阿里云镜像 WAF 拦截空 User-Agent（实测 403），必须带 UA
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 BaiZe/1.0");
        if (existing > 0)
        {
            _logger.LogInformation("续传 CUDA wheel（已有 {MB:F0} MB）: {Url}", existing / 1048576.0, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }
        else
        {
            _logger.LogInformation("开始下载 CUDA wheel: {Url}", url);
            AppendLog(_setupLog, $"下载 wheel: {url}");
        }

        const int stallSeconds = 60;
        using var stallCts = new CancellationTokenSource(TimeSpan.FromSeconds(stallSeconds));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stallCts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var already = existing;
        if (existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // 服务器不支持 Range，只能重头下（覆盖残件）
            _logger.LogInformation("镜像不支持断点续传，重新完整下载");
            already = 0;
        }
        var total = already + (resp.Content.Headers.ContentLength ?? 0);

        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        FileStream fs;
        try
        {
            fs = new FileStream(dest, FileMode.Append, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException($"wheel 正在被其他进程下载，请等它结束或删除后再启动: {dest}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var got = already;
        try
        {
            var buffer = new byte[512 * 1024];
            int n;
            while ((n = await src.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false)) > 0)
            {
                stallCts.CancelAfter(TimeSpan.FromSeconds(stallSeconds)); // 收到数据，重置看门狗
                await fs.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                got += n;
                if (sw.Elapsed - lastReport >= TimeSpan.FromSeconds(5))
                {
                    lastReport = sw.Elapsed;
                    var line = total > 0
                        ? $"下载中 {got / 1048576.0:F0} / {total / 1048576.0:F0} MB（{got * 100.0 / total:F0}%）"
                        : $"下载中 {got / 1048576.0:F0} MB";
                    _logger.LogInformation("[进度] {Line}", line);
                    AppendLog(_setupLog, line);
                }
            }
        }
        catch (OperationCanceledException) when (stallCts.IsCancellationRequested)
        {
            throw new TimeoutException($"下载链路 {stallSeconds} 秒无任何数据，判定卡死");
        }
        finally
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }

        if (!IsWheelValid(dest))
            throw new InvalidOperationException($"wheel 下载后完整性校验失败，请重试（会自动续传）: {dest}");
        _logger.LogInformation("CUDA wheel 下载完成: {File}（{MB:F0} MB，耗时 {Min:F0} 分钟）",
            dest, got / 1048576.0, sw.Elapsed.TotalMinutes);
        return dest;
    }

    /// <summary>校验 wheel 完整性：wheel 本质是 zip，ZipFile 读不出中央目录 = 下载不完整。
    /// 防止把还在下载的残件交给 pip 硬装（退出码 1）。</summary>
    private static bool IsWheelValid(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析本地 CUDA wheel 缓存目录：配置为空时默认 %LOCALAPPDATA%\BaiZe\funasr\wheels
    /// （与 venv 同级，与 App 外部预下载脚本的落点一致）。</summary>
    private string ResolveTorchWheelDir()
    {
        if (!string.IsNullOrWhiteSpace(_options.TorchWheelDir)) return _options.TorchWheelDir;
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_options.VenvDir))
                   ?? AppContext.BaseDirectory;
        return Path.Combine(root, "wheels");
    }

    /// <summary>拼给 funasr-server 子进程的 PATH：把 venv 里 nvidia/*/bin 追加到最前面。
    ///
    /// 必需项：onnxruntime 的 CUDA provider 是 LoadLibrary 隐式加载依赖的，找不到就会报
    /// "Error loading onnxruntime_providers_cuda.dll which depends on cublasLt64_12.dll"，
    /// 而 pip 装的 nvidia-cublas-cu12 等包把 dll 放在各自 site-packages/nvidia/&lt;pkg&gt;/bin 下，
    /// 压根不在 Windows 的 DLL 默认搜索路径里。实测 Python 侧的 os.add_dll_directory() 对它无效，
    /// 只有把目录塞进 PATH 才稳。</summary>
    private string BuildEnvPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            var nvidiaRoot = Path.Combine(_options.VenvDir, "Lib", "site-packages", "nvidia");
            if (!Directory.Exists(nvidiaRoot)) return current;
            var bins = Directory.EnumerateDirectories(nvidiaRoot)
                .Select(d => Path.Combine(d, "bin"))
                .Where(Directory.Exists);
            return string.Join(Path.PathSeparator, bins) + Path.PathSeparator + current;
        }
        catch
        {
            return current;
        }
    }

    /// <summary>定位重叠语音分离模型：本地缓存优先，缺失且配了 URL 则下载（约 87MB）。
    ///
    /// 拿不到模型返回 null，此时 server.py 不会启用分离——损失的是"两人同时说话"的能力，
    /// 常规识别与轮流说话的说话人区分都不受影响，所以这里失败一律降级而非抛错。</summary>
    private async Task<string?> ResolveSepModelAsync()
    {
        if (!_options.EnableOverlapSeparation) return null;
        if (!string.IsNullOrWhiteSpace(_options.SepModelPath) && File.Exists(_options.SepModelPath))
            return _options.SepModelPath;

        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_options.VenvDir))
                   ?? AppContext.BaseDirectory;
        var dest = Path.Combine(root, "models", "mossformer2-ss-16k-int8.onnx");
        // ONNX 是 protobuf 容器，用大小做粗校验：86.7MB 的 INT8 量化版，50MB 以下必是残件
        if (File.Exists(dest) && new FileInfo(dest).Length > 50L * 1024 * 1024) return dest;

        if (string.IsNullOrWhiteSpace(_options.SepModelUrl)) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _logger.LogInformation("下载重叠语音分离模型（约 87MB）: {Url}", _options.SepModelUrl);
            AppendLog(_setupLog, $"下载分离模型: {_options.SepModelUrl}");
            // 跟随系统代理（国内访问 ModelScope 常需代理）；wheel 下载走的是另一套直连逻辑
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var req = new HttpRequestMessage(HttpMethod.Get, _options.SepModelUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 BaiZe/1.0");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                await src.CopyToAsync(fs).ConfigureAwait(false);
            _logger.LogInformation("分离模型就绪: {File}（{MB:F0} MB）", dest, new FileInfo(dest).Length / 1048576.0);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("分离模型下载失败，本次不支持两人同时说话（常规识别不受影响）: {Msg}", ex.Message);
            AppendLog(_setupLog, $"分离模型下载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>在缓存目录中找匹配通配的 wheel 文件（返回最新的一个，找不到返回 null）。</summary>
    private static string? FindWheel(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, $"{pattern}.whl")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>监视 pip 下载进度：pip 把在途下载放在 %TEMP%\pip-* 临时目录，
    /// 每 10 秒扫一次最近仍在增长的文件大小，与 pip 输出的总大小相除得百分比。
    /// 进程退出即停止；临时文件找不到（已装完/已被移走）时静默跳过。</summary>
    private async Task WatchDownloadAsync(double totalMB, Process p)
    {
        var tempRoot = Path.GetTempPath();
        while (!p.HasExited)
        {
            try
            {
                FileInfo? newest = null;
                foreach (var dir in Directory.EnumerateDirectories(tempRoot, "pip-*"))
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            // 只认 1MB 以上、2 分钟内仍在写入的文件（正在下载的 whl）
                            if (fi.Length > 1_000_000 &&
                                DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromMinutes(2) &&
                                (newest is null || fi.Length > newest.Length))
                                newest = fi;
                        }
                        catch { /* 文件可能恰好被移走 */ }
                    }
                }
                if (newest is not null)
                {
                    var curMB = newest.Length / 1048576.0;
                    var line = totalMB > 0
                        ? $"下载中 {curMB:F0} / {totalMB:F0} MB（{Math.Min(curMB / totalMB * 100, 100):F0}%）"
                        : $"下载中 {curMB:F0} MB（总大小未知）";
                    _logger.LogInformation("[进度] {Line}", line);
                    AppendLog(_setupLog, line);
                }
            }
            catch { /* 扫描失败不影响主流程 */ }
            await Task.Delay(10000).ConfigureAwait(false);
        }
    }

    /// <summary>快速探测依赖是否已装齐（importlib 元数据查找，不加载模块，约 0.5 秒）。
    /// 装齐则跳过 pip 检查；探测失败按未装齐处理，回落完整 pip 流程兜底。</summary>
    private static async Task<bool> AreDepsInstalledAsync(string venvPython)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = venvPython,
                // 与 requirements.txt 对应的包名列表（onnxruntime-gpu 导入名仍是 onnxruntime）
                Arguments = "-c \"import importlib.util,sys; mods=('funasr','numpy','websockets','torch','torchaudio','onnxruntime'); sys.exit(0 if all(importlib.util.find_spec(m) for m in mods) else 1)\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return p.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检查 venv 里的 torch 是否为 CUDA 构建（读 torch/version.py 的 cuda 字段，不启动进程）。</summary>
    private static bool IsCudaTorchInstalled(string venvDir)
    {
        try
        {
            // Windows venv 布局：venv\Lib\site-packages\torch\version.py
            var versionPy = Path.Combine(venvDir, "Lib", "site-packages", "torch", "version.py");
            if (!File.Exists(versionPy)) return false;   // torch 未安装
            foreach (var line in File.ReadLines(versionPy))
            {
                var t = line.Trim();
                if (!t.StartsWith("cuda", StringComparison.OrdinalIgnoreCase) || !t.Contains('=')) continue;
                // CPU 构建: cuda = None；CUDA 构建: cuda = '12.4.1'
                var value = t.Split('=')[1].Trim().Trim('\'', '"');
                return !value.Equals("None", StringComparison.OrdinalIgnoreCase) && value.Length > 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>向日志文件追加一行（失败静默，不影响主流程）。</summary>
    private void AppendLog(string file, string line)
    {
        if (string.IsNullOrEmpty(file)) return;
        try { File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
        catch { /* 日志写失败不阻断 */ }
    }

    /// <summary>真正的 WebSocket 握手探测：成功即认定服务就绪。
    /// （裸 TCP 连接即断会被 websockets 服务端记为 handshake 失败，产生 EOFError 噪音日志）。</summary>
    private static async Task<bool> IsWsReadyAsync(int port)
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token).ConfigureAwait(false);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;   // 未就绪 / 端口未监听 / 非预期响应
        }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(300) && client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从起始端口开始找一个系统空闲端口。</summary>
    private static int? FindFreePort(int startPort, int tryCount)
    {
        for (var port = startPort; port < startPort + tryCount; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                var assigned = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return assigned;
            }
            catch (SocketException)
            {
                continue;   // 被占，试下一个
            }
        }
        return null;
    }
}

/// <summary>FunASR 配置项（appsettings.json → FunAsr 节）。</summary>
public sealed class FunAsrOptions
{
    public const string SectionName = "FunAsr";

    /// <summary>WebSocket 流式端点（托管模式下启动时可能被改写为实际端口）。</summary>
    public string WsUrl { get; set; } = "ws://127.0.0.1:10095";

    /// <summary>HTTP（OpenAI 兼容）端点，用于整段文件转写。</summary>
    public string HttpBaseUrl { get; set; } = "http://127.0.0.1:10095";

    /// <summary>启动时自动拉起内嵌 funasr-server（false = 外部自管，如 docker）。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>在独立 cmd 控制台窗口运行 funasr-server（日志实时可见，异常退出后窗口保留错误信息）。
    /// false = 隐藏窗口，stdout/stderr 重定向进日志文件与 ILogger。</summary>
    public bool ShowWindow { get; set; } = true;

    /// <summary>首次启动自动创建 venv 并 pip 安装依赖（false = 假定依赖已就绪）。</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>独立虚拟环境目录（空 = %LOCALAPPDATA%\BaiZe\funasr\venv）。</summary>
    public string VenvDir { get; set; } = "";

    /// <summary>Python 解释器路径或 PATH 名称（空 = 自动探测 python/py）。</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>pip 镜像源（空 = PyPI 官方；国内可填 https://pypi.tuna.tsinghua.edu.cn/simple）。</summary>
    public string PipIndexUrl { get; set; } = "";

    /// <summary>推理设备：auto / cuda / cpu。</summary>
    public string Device { get; set; } = "auto";

    /// <summary>GPU 模式（Device != cpu）下 torch 的安装索引。PyPI 默认是 CPU 构建，
    /// 必须用 PyTorch 官方索引的 CUDA 构建（cu124 兼容驱动 ≥ 12.4）。</summary>
    public string TorchIndexUrl { get; set; } = "https://download.pytorch.org/whl/cu124";

    /// <summary>本地 CUDA wheel 缓存目录（含 torch-*.whl / torchaudio-*.whl）。
    /// 非空时优先本地安装（免受 pytorch CDN 慢速/超时影响）；空 = 不启用本地缓存。</summary>
    public string TorchWheelDir { get; set; } = "";

    /// <summary>CUDA torch wheel 的完整下载 URL（镜像，如阿里云 pytorch-wheels）。
    /// 本地无完整 wheel 时自动断点续传下载，下完校验后本地安装；空 = 不自动下载。</summary>
    public string TorchWheelUrl { get; set; } = "";

    /// <summary>CUDA torchaudio wheel 的完整下载 URL（与 TorchWheelUrl 配套；空 = 不自动下载）。</summary>
    public string TorchaudioWheelUrl { get; set; } = "";

    /// <summary>说话人模型（如 cam++；留空关闭说话人分离）。</summary>
    public string? SpkModel { get; set; } = "cam++";

    /// <summary>是否启用重叠语音分离（两人同时说话 / 抢话）。
    ///
    /// 这需要额外一个 86.7MB 的 ONNX 模型和 onnxruntime-gpu 依赖。模型缺失或没有 NVIDIA 显卡时
    /// 会自动降级跳过：常规识别、轮流说话的说话人区分都不受影响，只是不支持"同时说话"。</summary>
    public bool EnableOverlapSeparation { get; set; } = true;

    /// <summary>本地重叠语音分离模型路径。留空则使用默认缓存位置
    /// （venv 同级 models/mossformer2-ss-16k-int8.onnx），不存在时按 <see cref="SepModelUrl"/> 下载。</summary>
    public string SepModelPath { get; set; } = "";

    /// <summary>重叠语音分离模型的下载地址（ModelScope 上的 MossFormer2 ONNX 量化版，Apache-2.0）。
    /// 留空表示不自动下载——此时只使用本地已存在的模型。</summary>
    public string SepModelUrl { get; set; } =
        "https://www.modelscope.cn/api/v1/models/manyeyes/mossformer2-ss-16k-onnx/repo?Revision=master&FilePath=model.int8.onnx";

    /// <summary>HTTP 端点的模型名（OpenAI 兼容字段，服务端一般忽略）。</summary>
    public string Model { get; set; } = "paraformer-zh";

    /// <summary>pip 依赖安装超时（分钟，首次下载 torch 较大）。</summary>
    public int InstallTimeoutMinutes { get; set; } = 30;

    /// <summary>服务健康检查超时（秒，首次需下载模型，给足 15 分钟）。</summary>
    public int HealthTimeoutSeconds { get; set; } = 900;
}
