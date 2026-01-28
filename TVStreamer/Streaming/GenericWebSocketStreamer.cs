
using com.lightstreamer.client;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TVStreamer.Listeners;
using TVStreamer.Models;

namespace TVStreamer.Streaming;

/// <summary>
/// Generic, config-driven WebSocket streamer:
/// - PreSteps[]: HTTP requests to enrich Vars (e.g., ClientKey).
/// - Ws.Url (+ Ws.Headers): build/connect WS (supports {{Vars.*}} and {{Guid}}).
/// - Subscriptions[]: HTTP requests to create broker subscriptions.
/// - Frames: decode received WS messages (binary or text), filter by ReferenceId,
///           map each item via IngestTemplate into payload, and POST to your ingest.
/// </summary>
public sealed class GenericWebSocketStreamer : IStreamer, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Dictionary<string, string> _vars;
    private readonly JsonDocument _streamingDoc;
    private readonly JsonElement _streaming;
    private readonly string _ingestUrl;
    private readonly string _ingestKey;

    // CHANGE: Use the Service Interface instead of the Index class
    private readonly IPositionService _positionService;

    private readonly string _brokerName;
    private ClientWebSocket? _ws;

    public GenericWebSocketStreamer(
        HttpClient http,
        string baseUrl,
        IReadOnlyDictionary<string, string> vars,
        JsonElement streamingObject,
        string ingestUrl,
        string ingestKey,
        IPositionService positionService, // CHANGE THIS PARAMETER
        string brokerName
    )
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _vars = new(vars ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _streamingDoc = JsonDocument.Parse(streamingObject.GetRawText());
        _streaming = _streamingDoc.RootElement;
        _ingestUrl = ingestUrl;
        _ingestKey = ingestKey;

        // STORE the service
        _positionService = positionService;

        _brokerName = brokerName;
    }

    private JsonNode MergeWithBase(JsonNode wsItem)
    {
        try
        {
            if (wsItem is not JsonObject obj) return wsItem;

            // 1. Identify the Deal
            var dealId = (obj["PositionId"] ?? obj["DealId"] ?? obj["NetPositionId"])?.ToString();
            if (string.IsNullOrWhiteSpace(dealId)) return wsItem;

            // 2. FIXED: Call the Service instead of the old Index
            if (!_positionService.TryGetPosition(_brokerName, dealId, out var bp))
            {
                // This is actually a good place to log if you're missing data
                // Console.WriteLine($"[{_brokerName}] No base data found for deal {dealId}");
                return wsItem;
            }

            // 3. Clone the incoming WebSocket item
            var clone = JsonNode.Parse(wsItem.ToJsonString())!.AsObject();

            // 4. Enrich the JSON (Keep this as is, it's already broker-agnostic)
            clone["PositionBase"] = new JsonObject
            {
                ["Broker"] = _brokerName,
                ["Account"] = bp.AccountId,
                ["DealId"] = bp.DealId,
                ["Amount"] = JsonValue.Create(bp.Amount),
                ["OpenPrice"] = JsonValue.Create(bp.OpenLevel),
                ["Uic"] = bp.Uic is null ? null : JsonValue.Create(bp.Uic.Value),
                ["BuySell"] = bp.Amount < 0 ? "Sell" : "Buy"
            };

            clone["DisplayAndFormat"] = new JsonObject
            {
                ["Symbol"] = bp.Epic,
                ["Currency"] = bp.Currency
            };

            clone["Direction"] = bp.Amount < 0 ? "Sell" : "Buy";

            return clone;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_brokerName}] Merge Error: {ex.Message}");
            return wsItem;
        }
    }


    // ---------- Case-insensitive property resolver ----------
    private static bool GetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public async Task StartAsync()
    {
        CancellationToken ct = CancellationToken.None;
        try
        {
            // (A) PreSteps
            if (GetPropertyCI(_streaming, "PreSteps", out var preSteps) && preSteps.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in preSteps.EnumerateArray())
                {
                    Console.WriteLine("[WS] Running PreStep...");
                    await ExecuteHttpStepAsync(step, ct);
                }
            }

            // (A.2) Ensure we have a concrete ContextId in Vars
            if (!_vars.TryGetValue("ContextId", out var cid) || string.IsNullOrWhiteSpace(cid) || cid.Contains("{{Guid}}"))
            {
                var newCid = "tv_" + Guid.NewGuid().ToString("N");
                _vars["ContextId"] = newCid;
                Console.WriteLine($"[WS] Generated ContextId: {newCid}");
            }

            // (B) Connect WebSocket
            if (!GetPropertyCI(_streaming, "Ws", out var wsObj))
                throw new InvalidOperationException("Streaming.Ws section is required for WebSocket streaming (not found).");

            if (!GetPropertyCI(wsObj, "Url", out var urlEl))
                throw new InvalidOperationException("Streaming.Ws.Url is required (not found).");

            var wsUrlTpl = urlEl.GetString() ?? throw new InvalidOperationException("Streaming.Ws.Url is null.");
            var wsUrl = Resolve(wsUrlTpl, null);   // supports {{Vars.*}} and {{Guid}} (GUID already injected above)

            Console.WriteLine("[WS] Preparing connect...");

            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            if (GetPropertyCI(wsObj, "Headers", out var wsHdrs) && wsHdrs.ValueKind == JsonValueKind.Object)
            {
                foreach (var h in wsHdrs.EnumerateObject())
                {
                    var name = h.Name;
                    var val = Resolve(h.Value.GetString() ?? "", null);
                    _ws.Options.SetRequestHeader(name, val);
                    Console.WriteLine($"[WS] Header: {name}=(hidden)");
                }
            }
            await _ws.ConnectAsync(new Uri(wsUrl), ct);
            Console.WriteLine("[WS] Connected.");

            // (C) Subscriptions
            if (GetPropertyCI(_streaming, "Subscriptions", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                foreach (var subStep in subs.EnumerateArray())
                {
                    Console.WriteLine("[WS] Creating subscription...");
                    await ExecuteHttpStepAsync(subStep, ct);
                }
            }
            else
            {
                Console.WriteLine("[WS] WARN: No 'Subscriptions' array found in Streaming section.");
            }

            // (D) Receive loop (non-blocking)
            _ = Task.Run(() => ReceiveLoopAsync(ct));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] StartAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ExecuteHttpStepAsync(JsonElement step, CancellationToken ct)
    {
        try
        {
            if (!GetPropertyCI(step, "Request", out var reqObj))
                throw new InvalidOperationException("Streaming step requires Request.");

            var method = GetPropertyCI(reqObj, "Method", out var m) ? (m.GetString() ?? "GET") : "GET";
            var path = GetPropertyCI(reqObj, "Path", out var p) ? (p.GetString() ?? "") : "";
            var abs = GetPropertyCI(reqObj, "AbsoluteUrl", out var a) ? a.GetString() : null;

            var uri = abs is not null
                ? new Uri(Resolve(abs, null))
                : new Uri($"{_baseUrl}/{Resolve(path, null).TrimStart('/')}");

            var req = new HttpRequestMessage(new HttpMethod(method), uri);

            // Resolve and apply headers (except Content-Type, which belongs to content)
            if (GetPropertyCI(reqObj, "Headers", out var hObj) && hObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var h in hObj.EnumerateObject())
                {
                    var name = h.Name;
                    var val = Resolve(h.Value.GetString() ?? "", null);
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    req.Headers.TryAddWithoutValidation(name, val);
                }
            }

            if (GetPropertyCI(reqObj, "Body", out var body))
            {
                // 1) Raw template string
                string raw = body.GetRawText();

                // 2) Resolve placeholders
                string resolved = Resolve(raw, null);

                // 3) Send directly
                req.Content = new StringContent(resolved, Encoding.UTF8, GetContentType(reqObj));
            }

            Console.WriteLine($"[WS-STEP] {method} {uri}");
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[WS-STEP] -> {(int)resp.StatusCode} {resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WS-STEP] BODY SNIPPET:\n{Snippet(text)}");
                throw new HttpRequestException($"Streaming step failed {(int)resp.StatusCode} {resp.StatusCode}");
            }

            if (text?.Contains("\"Snapshot\"", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var node = JsonNode.Parse(text);
                    var snap = node?["Snapshot"]?["Data"] as JsonArray;
                    if (snap is not null)
                    {
                        // Resolve IngestTemplate
                        if (GetPropertyCI(_streaming, "Frames", out var frames) &&
                            GetPropertyCI(frames, "IngestTemplate", out var ingestTmpl))
                        {
                            // Ingest each snapshot row immediately
                            foreach (var item in snap)
                            {
                                var payloadText = RenderTemplateObject(ingestTmpl, item);
                                _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, JsonDocument.Parse(payloadText).RootElement);
                            }
                        }
                    }
                }
                catch (Exception ex1)
                {
                    Console.WriteLine($"[WS] Snapshot ingest error: {ex1.Message}");
                }
            }

            // Extract to Vars
            if (GetPropertyCI(step, "Extract", out var ex) && ex.ValueKind == JsonValueKind.Object)
                ExtractToVars(ex, resp, text);

            // BindDefaults to HttpClient default headers if configured
            if (GetPropertyCI(step, "BindDefaults", out var bind) && bind.ValueKind == JsonValueKind.Object)
            {
                if (GetPropertyCI(bind, "Headers", out var bindHeaders) && bindHeaders.ValueKind == JsonValueKind.Object)
                {
                    foreach (var h in bindHeaders.EnumerateObject())
                    {
                        var name = h.Name;
                        var val = Resolve(h.Value.GetString() ?? "", null);
                        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = val.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                            _http.DefaultRequestHeaders.Authorization =
                                parts.Length == 2
                                    ? new AuthenticationHeaderValue(parts[0], parts[1])
                                    : new AuthenticationHeaderValue("Bearer", val);
                        }
                        else
                        {
                            if (_http.DefaultRequestHeaders.Contains(name))
                                _http.DefaultRequestHeaders.Remove(name);
                            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, val);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] ExecuteHttpStep failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static string GetContentType(JsonElement reqObj)
    {
        // Honor an explicit Content-Type in Request.Headers if provided (rare), else default to application/json
        if (GetPropertyStaticCI(reqObj, "Headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            if (GetPropertyStaticCI(h, "Content-Type", out var ct) && ct.ValueKind == JsonValueKind.String)
                return ct.GetString()!;
        }
        return "application/json";
    }

    private static bool GetPropertyStaticCI(JsonElement obj, string name, out JsonElement value)
        => GetPropertyCI(obj, name, out value);

    private void ExtractToVars(JsonElement ex, HttpResponseMessage resp, string text)
    {
        var added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (GetPropertyCI(ex, "Headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in h.EnumerateObject())
            {
                if (resp.Headers.TryGetValues(kv.Value.GetString()!, out var vals))
                    added[kv.Name] = vals.FirstOrDefault() ?? "";
            }
        }

        if (GetPropertyCI(ex, "Json", out var j) && j.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var node = JsonNode.Parse(text);
                    foreach (var kv in j.EnumerateObject())
                    {
                        var val = Select(node, kv.Value.GetString()!);
                        if (val is not null)
                            added[kv.Name] = val.ToString();
                    }
                }
                catch (Exception parseEx)
                {
                    Console.WriteLine($"[WS-ERR] Extract JSON parse failed: {parseEx.Message}");
                }
            }
        }

        foreach (var kv in added)
        {
            _vars[kv.Key] = kv.Value;
            Console.WriteLine($"[WS] Var set: {kv.Key}=**");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_ws is null) return;

        if (!GetPropertyCI(_streaming, "Frames", out var frames))
        {
            Console.WriteLine("[WS] No Frames mapping provided. Closing.");
            return;
        }
        var refIdWanted = GetPropertyCI(frames, "ReferenceId", out var rif) ? rif.GetString() : null;
        var dataArrayPath = GetPropertyCI(frames, "DataArrayPath", out var dap) ? dap.GetString() ?? "Data" : "Data";
        var ingestObj = frames.GetProperty("IngestTemplate");

        var buf = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult? r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("[WS] Closed by server.");
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var bytes = ms.ToArray();

                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    //Console.WriteLine($"[WS] Text frame {bytes.Length} bytes");
                    HandleTextFrame(text, refIdWanted, dataArrayPath, ingestObj);
                }
                else if (r.MessageType == WebSocketMessageType.Binary)
                {
                    //Console.WriteLine($"[WS] Binary frame {bytes.Length} bytes");
                    HandleBinaryFrame(bytes, refIdWanted, dataArrayPath, ingestObj);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] Receive loop: {ex.Message}");
        }
    }

    private void HandleTextFrame(string text, string? refIdWanted, string dataArrayPath, JsonElement ingestTemplate)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is null) return;

            var refId = node?["ReferenceId"]?.ToString();
            if (refIdWanted is not null && !string.Equals(refId, refIdWanted, StringComparison.OrdinalIgnoreCase))
                return;

            JsonArray? dataArr = null;

            // Case 1: root is array → use it
            if (node is JsonArray arrRoot)
            {
                dataArr = arrRoot;
            }
            // Case 2: try the mapped path (e.g. "Data", "items", etc.)
            else
            {
                var selected = Select(node, dataArrayPath);
                if (selected is JsonArray arrSel)
                    dataArr = arrSel;
            }

            if (dataArr is null)
            {
                Console.WriteLine("[WS] No array found at root or DataArrayPath.");
                return;
            }

            if (dataArr is null) return;

            foreach (var item in dataArr)

            {
                Console.WriteLine("[WS-RAW-ITEM] " + item.ToJsonString());
                PostIngest(ingestTemplate, item);
            }

            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] Text frame parse: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode Saxo’s binary envelope:
    /// [8]  MessageId (Int64 LE)
    /// [2]  Flags/Reserved (Int16)
    /// [1]  ReferenceIdLength (UInt8)
    /// [N]  ReferenceId (ASCII)
    /// [1]  PayloadFormat (0=json, 1=protobuf)
    /// [4]  PayloadLength (Int32 LE)
    /// [N]  Payload (UTF-8 JSON if PayloadFormat==0)
    /// </summary>
    private void HandleBinaryFrame(byte[] frame, string? refIdWanted, string dataArrayPath, JsonElement ingestTemplate)
    {
        try
        {
            var offset = 0;

            if (frame.Length < 8) return;
            var messageId = BitConverter.ToInt64(frame, offset); offset += 8;

            if (frame.Length < offset + 2) return;
            var flags = BitConverter.ToInt16(frame, offset); offset += 2;

            if (frame.Length < offset + 1) return;
            int refLen = frame[offset]; offset += 1;
            if (frame.Length < offset + refLen) return;
            var refId = Encoding.ASCII.GetString(frame, offset, refLen); offset += refLen;

            if (frame.Length < offset + 1) return;
            byte fmt = frame[offset]; offset += 1; // 0=json, 1=protobuf

            if (frame.Length < offset + 4) return;
            int payloadLen = BitConverter.ToInt32(frame, offset); offset += 4;
            if (frame.Length < offset + payloadLen) return;

            var payloadBytes = frame.AsSpan(offset, payloadLen).ToArray();

            if (refIdWanted is not null && !string.Equals(refId, refIdWanted, StringComparison.OrdinalIgnoreCase))
                return;

            //Console.WriteLine("[WS-RAW] FRAME BYTES: " + BitConverter.ToString(frame));
            //Console.WriteLine("[WS-RAW] REFID: " + refId + ", PAYLOADFMT: " + fmt + ", PAYLOADLEN: " + payloadLen);

            if (fmt == 0)
            {
                var payloadText = Encoding.UTF8.GetString(payloadBytes);
                //Console.WriteLine("[WS-RAW-PAYLOAD] " + payloadText);
                var node = JsonNode.Parse(payloadText);
                JsonArray? dataArr = null;

                // Case 1: root is array → use it
                if (node is JsonArray arrRoot)
                {
                    dataArr = arrRoot;
                }
                // Case 2: try the mapped path (e.g. "Data", "items", etc.)
                else
                {
                    var selected = Select(node, dataArrayPath);
                    if (selected is JsonArray arrSel)
                        dataArr = arrSel;
                }

                if (dataArr is null)
                {
                    Console.WriteLine("[WS] No array found at root or DataArrayPath.");
                    return;
                }

                if (dataArr is null) return;

                foreach (var item in dataArr)
                    PostIngest(ingestTemplate, item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] Binary frame decode: {ex.Message}");
        }
    }

    private void PostIngest(JsonElement ingestTemplate, JsonNode? itemNode)
    {
        try
        {
            var json = itemNode.ToJsonString();
            Console.WriteLine($"[{_brokerName}] INGESTING: {json}");

            if (itemNode is null)
            {
                Console.WriteLine("[WS-INGEST] Skip: null itemNode");
                return;
            }

            // 1. Perform the generic merge using our thread-safe _baseIndex
            // This is where 'Size' is added back into the object for ALL brokers
            var merged = MergeWithBase(itemNode);

            // 2. Render the template using the enriched data
            var payloadText = RenderTemplateObject(ingestTemplate, merged);

            // 3. (Optional) Log for debugging - you should see the Amount here now
            // Console.WriteLine($"[WS] Final Payload: {payloadText}");

            // 4. Send to the Blazor Web App
            _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, JsonDocument.Parse(payloadText).RootElement);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS-ERR] Ingest post failed for {_brokerName}: {ex.Message}");
        }
    }

    // ---------- Template rendering ----------
    private string RenderTemplateObject(JsonElement tmpl, JsonNode? item)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        RenderAny(tmpl, item, w);
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void RenderAny(JsonElement el, JsonNode? item, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    RenderAny(p.Value, item, w);
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var v in el.EnumerateArray())
                    RenderAny(v, item, w);
                w.WriteEndArray();
                break;

            case JsonValueKind.String:
                {
                    var raw = el.GetString() ?? "";
                    var resolved = Resolve(raw, item);
                    if (decimal.TryParse(resolved, System.Globalization.NumberStyles.Any,
                                         System.Globalization.CultureInfo.InvariantCulture, out var dec))
                        w.WriteNumberValue(dec);
                    else if (bool.TryParse(resolved, out var b)) w.WriteBooleanValue(b);
                    else w.WriteStringValue(resolved);
                }
                break;

            case JsonValueKind.Number:
                w.WriteRawValue(el.GetRawText()); break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                w.WriteBooleanValue(el.GetBoolean()); break;

            case JsonValueKind.Null:
            default:
                w.WriteNullValue(); break;
        }
    }

    private static readonly Regex Placeholder = new(@"\{\{\s*(?<expr>.+?)\s*\}\}", RegexOptions.Compiled);

    // Deep JSON resolver for Body (resolves {{...}} inside values)
    private JsonNode ResolveJsonNode(JsonNode node)
    {
        switch (node)
        {
            case JsonValue value:
                {
                    if (value.TryGetValue<string>(out var s))
                    {
                        var resolved = Resolve(s, null);
                        return JsonValue.Create(resolved)!;
                    }
                    return value;
                }
            case JsonObject obj:
                {
                    var newObj = new JsonObject();
                    foreach (var kv in obj)
                        newObj[kv.Key] = kv.Value != null ? ResolveJsonNode(kv.Value) : null;
                    return newObj;
                }
            case JsonArray arr:
                {
                    var newArr = new JsonArray();
                    foreach (var item in arr)
                        newArr.Add(item != null ? ResolveJsonNode(item) : null);
                    return newArr;
                }
        }
        return node;
    }

    private string Resolve(string input, JsonNode? item)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // Built-in {{Guid}}
        input = input.Replace("{{Guid}}", Guid.NewGuid().ToString("N"), StringComparison.Ordinal);

        return Placeholder.Replace(input, m =>
        {
            var expr = m.Groups["expr"].Value.Trim();

            // Functions: abs(...), sign(x,a,b), coalesce(a,b,...)
            if (expr.StartsWith("abs(", StringComparison.OrdinalIgnoreCase)) return EvalAbs(expr, item);
            if (expr.StartsWith("sign(", StringComparison.OrdinalIgnoreCase)) return EvalSign(expr, item);
            if (expr.StartsWith("coalesce(", StringComparison.OrdinalIgnoreCase)) return EvalCoalesce(expr, item);

            // Vars.* or item.*
            if (expr.StartsWith("Vars.", StringComparison.OrdinalIgnoreCase))
            {
                var key = expr.Substring(5);
                return _vars.TryGetValue(key, out var v) ? v : "";
            }

            if (expr.StartsWith("item.", StringComparison.OrdinalIgnoreCase))
            {
                var path = expr.Substring(5);
                var val = Select(item, path);


                if (val is JsonValue jv)
                {
                    if (jv.TryGetValue<decimal>(out var d))
                        return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (jv.TryGetValue<double>(out var dbl))
                        return dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (jv.TryGetValue<long>(out var lng))
                        return lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (jv.TryGetValue<string>(out var s))
                        return s;
                }
                return val?.ToString() ?? "";

            }


            // Raw JSON path on item (short form): $.X.Y
            if (expr.StartsWith("$."))
            {
                var val = Select(item, expr.TrimStart('$', '.'));
                return val?.ToString() ?? "";
            }

            return ""; // unknown
        });
    }

    private string EvalAbs(string expr, JsonNode? item)
    {
        var inner = expr.Trim().TrimStart('a', 'b', 's', '(').TrimEnd(')');
        var s = Resolve($"{{{{{inner}}}}}", item);
        if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var d))
            return Math.Abs(d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "0";
    }

    private string EvalSign(string expr, JsonNode? item)
    {
        var inner = expr.Trim().TrimStart('s', 'i', 'g', 'n', '(').TrimEnd(')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
        {
            var x = Resolve($"{{{{{parts[0]}}}}}", item);
            var pos = parts[1].Trim().Trim('"');
            var neg = parts[2].Trim().Trim('"');
            if (decimal.TryParse(x, System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d >= 0 ? pos : neg;
        }
        return "";
    }

    private string EvalCoalesce(string expr, JsonNode? item)
    {
        var inner = expr.Trim().TrimStart('c', 'o', 'a', 'l', 'e', 's', 'c', 'e', '(').TrimEnd(')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var v = p.Trim();
            if (v.StartsWith("\"") && v.EndsWith("\"")) return v.Trim('"');
            var resolved = Resolve($"{{{{{v}}}}}", item);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }
        return "";
    }

    private static JsonNode? Select(JsonNode? node, string path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path)) return null;
        var cur = node;
        foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            cur = cur?[seg];
        return cur;
    }

    private static string Snippet(string s) => string.IsNullOrWhiteSpace(s) ? "(empty)" :
        (s.Length <= 600 ? s : s[..600] + "... [truncated]");

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { /* ignore */ }
        try { _streamingDoc?.Dispose(); } catch { /* ignore */ }
    }
}
