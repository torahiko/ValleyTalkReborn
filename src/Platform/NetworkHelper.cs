using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace ValleytalkReborn.Platform
{
    /// <summary>
    /// Helper class for Android-compatible network operations
    /// </summary>
    public static class NetworkHelper
    {
        private static readonly HttpClient _httpClient;
        
        static NetworkHelper()
        {
            var handler = new HttpClientHandler();
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(ModEntry.Config?.QueryTimeout ?? 60)
            };

            if (AndroidHelper.IsAndroid)
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent",
                    "ValleyTalk/1.0 (Android; Stardew Valley Mod)");
            }
        }

        public static async Task<string> MakeRequestAsync(string url, string content = null, CancellationToken cancellationToken = default, string authToken = null)
        {
            HttpResponseMessage response = null;
            try
            {
                using var request = new HttpRequestMessage(
                    string.IsNullOrEmpty(content) ? HttpMethod.Get : HttpMethod.Post, 
                    url
                );

                if (!string.IsNullOrEmpty(content))
                {
                    request.Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
                }

                if (!string.IsNullOrEmpty(authToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {authToken}");
                }

                response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was cancelled");
            }
            catch (TaskCanceledException)
            {
                throw new TimeoutException("Request timed out");
            }
            catch (HttpRequestException ex)
            {
                string message = ex.Message;
                if (response?.Content != null)
                {
                    // 【Bug 修复】使用 await 异步读取错误响应，消除原代码中 .Result 导致的线程死锁
                    try
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        message += $"\n (HTTP {(int)response.StatusCode} - {errorBody})";
                    }
                    catch
                    {
                        message += $"\n (HTTP {(int)response.StatusCode})";
                    }
                }
                throw new InvalidOperationException($"Network request failed: {message}", ex);
            }
        }

        public static async Task<string> MakeRequestWithCustomHeadersAsync(string url, string content, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage response = null;
            try
            {
                using var stringContent = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = stringContent };
                
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.Add(header.Key, header.Value);
                    }
                }

                response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was cancelled");
            }
            catch (TaskCanceledException)
            {
                throw new TimeoutException("Request timed out");
            }
            catch (HttpRequestException ex)
            {
                string message = ex.Message;
                if (response?.Content != null)
                {
                    // 【优化】同样补充详细的错误日志读取
                    try
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        message += $"\n (HTTP {(int)response.StatusCode} - {errorBody})";
                    }
                    catch
                    {
                        message += $"\n (HTTP {(int)response.StatusCode})";
                    }
                }
                throw new InvalidOperationException($"Network request failed: {message}", ex);
            }
        }

        public static bool IsNetworkAvailable()
        {
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            }
            catch
            {
                return false;
            }
        }

        public static void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}