using System.Net.Http.Headers;
using System.Text.Json;
using BaiZe.Domain.Meetings;
using BaiZe.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace BaiZe.Infrastructure.FunAsr;

/// <summary>
/// FunASR 网关（真实实现，非流式）：调用本地 funasr-server 的 OpenAI 兼容端点
/// POST {HttpBaseUrl}/v1/audio/transcriptions（multipart，file 字段），返回 {"text": "..."}。
/// 整段音频按单说话人返回一段 RawSegment；HTTP 端口在请求时动态读取（支持换端口）。
/// </summary>
public sealed class FunAsrGateway : IAsrGateway
{
    private readonly FunAsrOptions _options;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly ILogger<FunAsrGateway> _logger;

    public FunAsrGateway(FunAsrOptions options, ILogger<FunAsrGateway> logger)
    {
        _options = options;
        _model = options.Model;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<AsrResult> TranscribeAsync(Stream audio, CancellationToken ct = default)
    {
        var baseUrl = _options.HttpBaseUrl.TrimEnd('/');   // 每次读取，跟随端口变更
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "audio.wav");
        form.Add(new StringContent(_model), "model");

        using var resp = await _http.PostAsync($"{baseUrl}/v1/audio/transcriptions", form, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("FunASR 转写返回 {Bytes} 字节", body.Length);

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (text.Length == 0) return new AsrResult(Array.Empty<RawSegment>());

        var speaker = new SpeakerId(SpeakerId.New().Value);
        return new AsrResult(new[]
        {
            new RawSegment(speaker, Array.Empty<float>(), text, StartMs: 0, EndMs: 0)
        });
    }

    public IAsyncEnumerable<RawSegment> StreamAsync(Stream audio, CancellationToken ct = default) =>
        // 实时流式走 IRealtimeTranscriber（FunAsrRealtimeClient，push 模型）；本端口保留整段流式扩展点。
        AsyncEnumerable.Empty<RawSegment>();
}
