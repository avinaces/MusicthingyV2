using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Musicthingy.Services;

public class LocalStreamProxy
{
    private readonly object _lock = new();
    private HttpListener? _listener;
    private readonly HttpClient _httpClient;
    private const int ProxyPort = 8088;
    private int _port = 0;

    public LocalStreamProxy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null) return;

            try
            {
                _listener = new HttpListener();
                _port = GetFreePort();

                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                Task.Run(ListenLoop);

                Debug.WriteLine($"[Proxy] Local loopback proxy started on port {_port}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Proxy] Failed to start local proxy: {ex.Message}");
                try { _listener?.Close(); } catch { }
                _listener = null;
                _port = 0;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _listener != null && _listener.IsListening;
            }
        }
    }

    public int Port => _port != 0 ? _port : ProxyPort;

    private static int GetFreePort()
    {
        // Note: GetFreePort opens and closes a port to detect availability, creating a tiny theoretical 
        // race condition if another process binds it immediately after. This is acceptable for local loopback.
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task ListenLoop()
    {
        while (true)
        {
            HttpListener? activeListener;
            lock (_lock)
            {
                activeListener = _listener;
            }

            if (activeListener == null || !activeListener.IsListening)
                break;

            try
            {
                HttpListenerContext context = await activeListener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Proxy] Listen loop interrupted: {ex.Message}");
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

            HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Get, targetUrl);

            if (context.Request.Headers["Range"] != null)
            {
                message.Headers.Add("Range", context.Request.Headers["Range"]);
            }

            if (context.Request.Headers["User-Agent"] != null)
            {
                message.Headers.Add("User-Agent", context.Request.Headers["User-Agent"]);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/webm";

            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
            }

            if (response.Headers.Contains("Accept-Ranges"))
            {
                context.Response.Headers.Add("Accept-Ranges", "bytes");
            }

            if (response.Content.Headers.ContentRange != null)
            {
                context.Response.Headers.Add("Content-Range", response.Content.Headers.ContentRange.ToString());
            }

            try
            {
                using Stream sourceStream = await response.Content.ReadAsStreamAsync();
                using Stream destStream = context.Response.OutputStream;

                await sourceStream.CopyToAsync(destStream);
            }
            catch (Exception ex) when (ex is IOException || ex is HttpListenerException)
            {
                Debug.WriteLine("[Proxy] Playback skipped or stopped by user. Upstream connection aborted.");
            }
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
        if (!IsRunning)
        {
            try { Start(); } catch { }

            if (!IsRunning)
                return originalUrl;
        }

        return $"http://127.0.0.1:{Port}/?url={Uri.EscapeDataString(originalUrl)}";
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _listener?.Close();
                _listener = null;
                _port = 0;
            }
            catch { }
        }
    }
}