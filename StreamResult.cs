﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using thuvu.Models;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace thuvu
{


    // Result for a single streamed assistant turn
    public class StreamResult
    {
        public string Content { get; init; } = "";
        public List<ToolCall>? ToolCalls { get; init; }
        public string? FinishReason { get; init; }
        public Usage? Usage { get; init; } // Optional, if stream_options.include_usage is set


        /// <summary>
        /// Streams a single assistant turn. If the model emits tool calls,
        /// they are accumulated and returned in ToolCalls; Content may be empty in that case.
        /// If it emits plain text, tokens are sent to onToken as they arrive.
        /// </summary>
        public static async Task<StreamResult> StreamChatOnceAsync(
            HttpClient http,
            ChatRequest req,
            CancellationToken ct,
            Action<string>? onToken = null,
            Action<Usage>? onUsage = null)
        {
            // Build a streaming request without changing your strong-typed models
            var streamingReq = new
            {
                model = req.Model,
                messages = req.Messages,
                tools = req.Tools,
                tool_choice = req.ToolChoice,
                temperature = req.Temperature,
                stream = true,
                stream_options = new {include_usage = true}
            };

            using var jsonContent = new StringContent(JsonSerializer.Serialize(streamingReq, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("/v1/chat/completions", jsonContent, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var sbContent = new StringBuilder();
            var finishReason = (string?)null;

            // Collect tool_calls deltas by index, merging arguments chunks
            var toolBuilders = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();

            string? line;
            Usage? usage = null;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (line.Length == 0) continue; // SSE event delimiter
                if (line.StartsWith("data: ") is false) continue;

                var payload = line.AsSpan(6).Trim().ToString();
                if (payload == "[DONE]") break;

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                // choices[0]
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    // Optional usage info
                    usage = JsonSerializer.Deserialize<Usage>(usageEl.GetRawText());
                    if(usage!=null) onUsage?.Invoke(usage);
                }
                // finish_reason might show up on the last delta
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString();

                // delta
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // Content tokens
                if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var token = contentEl.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        onToken?.Invoke(token);
                        sbContent.Append(token);
                    }
                }

                // Tool call deltas
                if (delta.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcArr.EnumerateArray())
                    {
                        // Each delta has an "index" to merge pieces
                        int index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number ? idxEl.GetInt32() : 0;

                        if (!toolBuilders.TryGetValue(index, out var builder))
                        {
                            builder = (null, null, new StringBuilder());
                            toolBuilders[index] = builder;
                        }

                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            builder.Id ??= idEl.GetString();

                        if (tc.TryGetProperty("function", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
                        {
                            if (fEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                builder.Name ??= nameEl.GetString();

                            if (fEl.TryGetProperty("arguments", out var argEl) && argEl.ValueKind == JsonValueKind.String)
                                builder.Args.Append(argEl.GetString());
                        }

                        toolBuilders[index] = builder;
                    }
                }
            }

            // Build final ToolCalls if any
            List<ToolCall>? toolCalls = null;
            if (toolBuilders.Count > 0)
            {
                toolCalls = new List<ToolCall>(toolBuilders.Count);
                foreach (var kv in toolBuilders.OrderBy(k => k.Key))
                {
                    var (id, name, args) = kv.Value;
                    toolCalls.Add(new ToolCall
                    {
                        Id = id ?? Guid.NewGuid().ToString("N"),
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = name ?? "",
                            Arguments = args.ToString()
                        }
                    });
                }
            }

            return new StreamResult
            {
                Content = sbContent.ToString(),
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                Usage = usage
            };
        }

    }
}

