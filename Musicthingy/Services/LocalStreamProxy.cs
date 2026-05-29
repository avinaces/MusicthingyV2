using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Musicthingy.Services;

public class LocalStreamProxy
{
    private HttpListener? _listener;
    private readonly HttpClient _httpClient;
    private const int ProxyPort = 8088;

    public LocalStreamProxy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void Start()
    {
        if (_listener != null) return;

        try
        {
            _listener = new HttpListener();
            // Bind to loopback only (requires no admin privileges on Windows/Android)
            _listener.Prefixes.Add($"http://127.0.0.1:{ProxyPort}/");
            _listener.Start();
            Task.Run(ListenLoop);
            Debug.WriteLine($"[Proxy] Local loopback proxy started on port {ProxyPort}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Proxy] Failed to start local proxy: {ex.Message}");
        }
    }

    private async Task ListenLoop()
    {
        while (_listener != null && _listener.IsListening)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Proxy] Listen error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            string? targetUrl = context.Request.QueryString["url"];
            if (string.IsNullOrEmpty(targetUrl))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            targetUrl = Uri.UnescapeDataString(targetUrl);

            // Forward request to YouTube using the robust system HttpClient
            var message = new HttpRequestMessage(HttpMethod.Get, targetUrl);

            // Forward Range headers (crucial for seeking in VLC!)
            if (context.Request.Headers["Range"] != null)
            {
                message.Headers.Add("Range", context.Request.Headers["Range"]);
            }

            // Copy User-Agent if provided by VLC
            if (context.Request.Headers["User-Agent"] != null)
            {
                message.Headers.Add("User-Agent", context.Request.Headers["User-Agent"]);
            }

            var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/webm";

            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            // Explicitly copy headers required for media player seeking
            if (response.Headers.Contains("Accept-Ranges"))
            {
                context.Response.Headers.Add("Accept-Ranges", "bytes");
            }
            if (response.Content.Headers.ContentRange != null)
            {
                context.Response.Headers.Add("Content-Range", response.Content.Headers.ContentRange.ToString());
            }

            using Stream sourceStream = await response.Content.ReadAsStreamAsync();
            using Stream destStream = context.Response.OutputStream;

            await sourceStream.CopyToAsync(destStream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Proxy] Playback stream routing error: {ex.Message}");
            try { context.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public string GetProxyUrl(string originalUrl)
    {
        return $"http://127.0.0.1:{ProxyPort}/?url={Uri.EscapeDataString(originalUrl)}";
    }

    public void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener = null;
        }
        catch { }
    }
}