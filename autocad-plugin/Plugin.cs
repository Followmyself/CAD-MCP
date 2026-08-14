using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CadMcp.AutoCAD.CadMcpPlugin))]
[assembly: System.Reflection.AssemblyVersion("1.9.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.9.0.0")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

namespace CadMcp.AutoCAD
{
    public sealed class CadMcpPlugin : IExtensionApplication
    {
        private const int Port = 8765;
        private const int RequestTimeoutSeconds = 15;
        private const int TemplateRequestTimeoutSeconds = 70;
        private const int TranslationRequestTimeoutSeconds = 120;
        private const int MaxCachedResponses = 1000;

        private static readonly ConcurrentQueue<DrawWorkItem> Pending =
            new ConcurrentQueue<DrawWorkItem>();
        private static readonly ConcurrentDictionary<string, DrawWorkItem> Inflight =
            new ConcurrentDictionary<string, DrawWorkItem>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, CircleResponse> Completed =
            new ConcurrentDictionary<string, CircleResponse>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> CompletedOrder =
            new ConcurrentQueue<string>();
        private static readonly ConcurrentQueue<TemplateWorkItem> TemplatePending =
            new ConcurrentQueue<TemplateWorkItem>();
        private static readonly ConcurrentDictionary<string, TemplateWorkItem> TemplateInflight =
            new ConcurrentDictionary<string, TemplateWorkItem>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, TemplateResponse> TemplateCompleted =
            new ConcurrentDictionary<string, TemplateResponse>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<TranslationWorkItem> TranslationPending =
            new ConcurrentQueue<TranslationWorkItem>();
        private static readonly ConcurrentDictionary<string, TranslationWorkItem> TranslationInflight =
            new ConcurrentDictionary<string, TranslationWorkItem>(StringComparer.Ordinal);

        private LocalHttpServer _server;

        public void Initialize()
        {
            try
            {
                AcApplication.Idle += OnApplicationIdle;
                _server = new LocalHttpServer(Port, HandleRequest);
                _server.Start();
                Log.Info("CadMcp AutoCAD plugin initialized on 127.0.0.1:" + Port + ".");
                WriteEditorMessage("\nCAD MCP plugin loaded; HTTP endpoint: 127.0.0.1:" + Port + ".");
            }
            catch (System.Exception ex)
            {
                AcApplication.Idle -= OnApplicationIdle;
                Log.Error("Plugin initialization failed.", ex);
                throw new InvalidOperationException(
                    "CAD MCP plugin initialization failed. Check port 8765 and plugin log: " + Log.Path,
                    ex);
            }
        }

        public void Terminate()
        {
            AcApplication.Idle -= OnApplicationIdle;
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            Log.Info("CadMcp AutoCAD plugin terminated.");
        }

        private static HttpResult HandleRequest(HttpRequest request)
        {
            if (request.Method == "GET" && request.Path == "/health")
            {
                return HttpResult.Json(200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "service", "CadMcp.AutoCAD" },
                    { "version", "1.9.0" },
                    { "port", Port },
                    { "pending_requests", Pending.Count + TemplatePending.Count + TranslationPending.Count },
                    { "active_requests", Inflight.Count + TemplateInflight.Count + TranslationInflight.Count },
                    { "tools", new[] { "draw_circle", "inspect_cad_workspace", "inspect_cad_translation", "translate_cad", "repair_cad_fonts", "build_slt73_template", "verify_slt73_template", "build_tailrace_culvert", "verify_tailrace_culvert", "build_supported_tailrace_culvert", "verify_supported_tailrace_culvert", "build_image_redraw", "verify_image_redraw", "build_copper_waterstop", "verify_copper_waterstop", "inspect_arc_annotations", "annotate_arcs", "verify_arc_annotations" } },
                    { "log_path", Log.Path }
                });
            }

            if (request.Method == "POST" &&
                (request.Path == "/inspect_cad_workspace" ||
                 request.Path == "/build_slt73_template" ||
                 request.Path == "/verify_slt73_template" ||
                 request.Path == "/build_tailrace_culvert" ||
                 request.Path == "/verify_tailrace_culvert" ||
                 request.Path == "/build_supported_tailrace_culvert" ||
                 request.Path == "/verify_supported_tailrace_culvert" ||
                 request.Path == "/build_image_redraw" ||
                 request.Path == "/verify_image_redraw" ||
                 request.Path == "/build_copper_waterstop" ||
                 request.Path == "/verify_copper_waterstop"))
            {
                return HandleTemplateRequest(request);
            }

            if (request.Method == "POST" &&
                (request.Path == "/inspect_cad_translation" ||
                 request.Path == "/translate_cad" ||
                 request.Path == "/repair_cad_fonts" ||
                 request.Path == "/inspect_arc_annotations" ||
                 request.Path == "/annotate_arcs" ||
                 request.Path == "/verify_arc_annotations"))
            {
                return HandleTranslationRequest(request);
            }

            if (request.Method != "POST" || request.Path != "/draw_circle")
            {
                return HttpResult.Json(404, ErrorResponse(null, "Unknown endpoint."));
            }

            DrawCircleRequest payload;
            try
            {
                payload = JsonSerializer().Deserialize<DrawCircleRequest>(request.Body);
            }
            catch (System.Exception ex)
            {
                return HttpResult.Json(400, ErrorResponse(null, "Invalid JSON: " + ex.Message));
            }

            string validationError = Validate(payload);
            if (validationError != null)
            {
                return HttpResult.Json(400, ErrorResponse(payload == null ? null : payload.request_id, validationError));
            }

            CircleResponse cached;
            if (Completed.TryGetValue(payload.request_id, out cached))
            {
                return HttpResult.Json(200, cached.AsDuplicate());
            }

            var candidate = new DrawWorkItem(payload);
            DrawWorkItem item = Inflight.GetOrAdd(payload.request_id, candidate);
            bool owner = ReferenceEquals(item, candidate);
            if (owner)
            {
                Pending.Enqueue(item);
                Log.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "Queued request {0}: center=({1},{2},{3}), radius={4}.",
                    payload.request_id,
                    payload.center.x,
                    payload.center.y,
                    payload.center.z,
                    payload.radius));
            }

            if (!item.Completion.Task.Wait(TimeSpan.FromSeconds(RequestTimeoutSeconds)))
            {
                return HttpResult.Json(504, ErrorResponse(
                    payload.request_id,
                    "AutoCAD did not reach an idle execution context within 15 seconds. The request remains idempotent; retry with the same request_id."));
            }

            CircleResponse response = item.Completion.Task.Result;
            if (!owner && response.ok)
            {
                response = response.AsDuplicate();
            }
            return HttpResult.Json(response.ok ? 200 : 500, response);
        }

        private static HttpResult HandleTemplateRequest(HttpRequest request)
        {
            TemplateRequest payload;
            try
            {
                payload = JsonSerializer().Deserialize<TemplateRequest>(request.Body);
            }
            catch (System.Exception ex)
            {
                return HttpResult.Json(400, TemplateResponse.Failure(null, null, "Invalid JSON: " + ex.Message));
            }

            string operation = request.Path == "/inspect_cad_workspace" ? "inspect" :
                request.Path == "/build_slt73_template" ? "build" :
                request.Path == "/verify_slt73_template" ? "verify" :
                request.Path == "/build_tailrace_culvert" ? "tailrace_build" :
                request.Path == "/verify_tailrace_culvert" ? "tailrace_verify" :
                request.Path == "/build_supported_tailrace_culvert" ? "tailrace_supported_build" :
                request.Path == "/verify_supported_tailrace_culvert" ? "tailrace_supported_verify" :
                request.Path == "/build_image_redraw" ? "image_redraw_build" :
                request.Path == "/verify_image_redraw" ? "image_redraw_verify" :
                request.Path == "/build_copper_waterstop" ? "copper_waterstop_build" :
                "copper_waterstop_verify";
            string validationError = operation.StartsWith("tailrace_", StringComparison.Ordinal)
                ? TailraceBuilder.Validate(payload, operation)
                : operation.StartsWith("image_redraw_", StringComparison.Ordinal)
                    ? ImageRedrawBuilder.Validate(payload, operation)
                    : operation.StartsWith("copper_waterstop_", StringComparison.Ordinal)
                        ? CopperWaterstopBuilder.Validate(payload, operation)
                        : TemplateBuilder.Validate(payload, operation);
            if (validationError != null)
            {
                return HttpResult.Json(400, TemplateResponse.Failure(
                    payload == null ? null : payload.request_id,
                    operation,
                    validationError));
            }

            string key = operation == "build" || operation == "tailrace_build" || operation == "tailrace_supported_build" || operation == "image_redraw_build" || operation == "copper_waterstop_build"
                ? payload.request_id
                : operation + ":" + Guid.NewGuid().ToString("N");
            if (operation == "build" || operation == "tailrace_build" || operation == "tailrace_supported_build" || operation == "image_redraw_build" || operation == "copper_waterstop_build")
            {
                TemplateResponse cached;
                if (TemplateCompleted.TryGetValue(key, out cached))
                {
                    return HttpResult.Json(200, cached.AsDuplicate());
                }
            }

            var candidate = new TemplateWorkItem(key, operation, payload);
            TemplateWorkItem item = TemplateInflight.GetOrAdd(key, candidate);
            bool owner = ReferenceEquals(item, candidate);
            if (owner)
            {
                TemplatePending.Enqueue(item);
                Log.Info("Queued " + operation + " request " + key + ".");
            }

            if (!item.Completion.Task.Wait(TimeSpan.FromSeconds(TemplateRequestTimeoutSeconds)))
            {
                return HttpResult.Json(504, TemplateResponse.Failure(
                    payload.request_id,
                    operation,
                    "AutoCAD did not complete the request within 70 seconds. Retry a build with the same request_id."));
            }

            TemplateResponse response = item.Completion.Task.Result;
            if (!owner && response.ok && (operation == "build" || operation == "tailrace_build" || operation == "tailrace_supported_build" || operation == "image_redraw_build" || operation == "copper_waterstop_build"))
            {
                response = response.AsDuplicate();
            }
            return HttpResult.Json(response.ok ? 200 : 500, response);
        }

        private static HttpResult HandleTranslationRequest(HttpRequest request)
        {
            TranslationRequest payload;
            try
            {
                payload = JsonSerializer().Deserialize<TranslationRequest>(request.Body);
            }
            catch (System.Exception ex)
            {
                return HttpResult.Json(400, TranslationResponse.Failure(null, null, "Invalid JSON: " + ex.Message));
            }

            string operation = request.Path == "/inspect_cad_translation" ? "inspect" :
                request.Path == "/repair_cad_fonts" ? "repair_fonts" :
                request.Path == "/inspect_arc_annotations" ? "arc_inspect" :
                request.Path == "/annotate_arcs" ? "arc_apply" :
                request.Path == "/verify_arc_annotations" ? "arc_verify" : "translate";
            string validationError = operation.StartsWith("arc_", StringComparison.Ordinal)
                ? ArcAnnotationBuilder.Validate(payload, operation)
                : CadTranslationBuilder.Validate(payload, operation);
            if (validationError != null)
            {
                return HttpResult.Json(400, TranslationResponse.Failure(
                    payload == null ? null : payload.request_id, operation, validationError));
            }

            string key = operation + ":" + payload.request_id;
            var candidate = new TranslationWorkItem(key, operation, payload);
            TranslationWorkItem item = TranslationInflight.GetOrAdd(key, candidate);
            bool owner = ReferenceEquals(item, candidate);
            if (owner)
            {
                TranslationPending.Enqueue(item);
                Log.Info("Queued " + operation + " CAD translation request " + key + ".");
            }

            if (!item.Completion.Task.Wait(TimeSpan.FromSeconds(TranslationRequestTimeoutSeconds)))
            {
                return HttpResult.Json(504, TranslationResponse.Failure(
                    payload.request_id,
                    operation,
                    "AutoCAD did not complete CAD translation within 120 seconds. Retry with the same request_id."));
            }

            TranslationResponse response = item.Completion.Task.Result;
            return HttpResult.Json(response.ok ? 200 : 500, response);
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            DrawWorkItem item;
            if (Pending.TryDequeue(out item))
            {
                CircleResponse response;
                try
                {
                    response = DrawCircle(item.Request);
                }
                catch (System.Exception ex)
                {
                    Log.Error("Request " + item.Request.request_id + " failed.", ex);
                    response = ErrorResponse(item.Request.request_id, ex.Message);
                }

                if (response.ok)
                {
                    Completed[item.Request.request_id] = response;
                    CompletedOrder.Enqueue(item.Request.request_id);
                    TrimCompletedCache();
                }

                DrawWorkItem ignored;
                Inflight.TryRemove(item.Request.request_id, out ignored);
                item.Completion.TrySetResult(response);
                return;
            }

            TemplateWorkItem templateItem;
            if (TemplatePending.TryDequeue(out templateItem))
            {
                TemplateResponse templateResponse;
                try
                {
                    templateResponse = templateItem.Operation.StartsWith("tailrace_", StringComparison.Ordinal)
                        ? TailraceBuilder.Execute(templateItem.Operation, templateItem.Request)
                        : templateItem.Operation.StartsWith("image_redraw_", StringComparison.Ordinal)
                            ? ImageRedrawBuilder.Execute(templateItem.Operation, templateItem.Request)
                            : templateItem.Operation.StartsWith("copper_waterstop_", StringComparison.Ordinal)
                                ? CopperWaterstopBuilder.Execute(templateItem.Operation, templateItem.Request)
                                : TemplateBuilder.Execute(templateItem.Operation, templateItem.Request);
                }
                catch (System.Exception ex)
                {
                    Log.Error("Template request " + templateItem.Key + " failed.", ex);
                    templateResponse = TemplateResponse.Failure(
                        templateItem.Request.request_id,
                        templateItem.Operation,
                        ex.Message);
                }

                if (templateResponse.ok &&
                    (templateItem.Operation == "build" || templateItem.Operation == "tailrace_build" || templateItem.Operation == "tailrace_supported_build" || templateItem.Operation == "image_redraw_build" || templateItem.Operation == "copper_waterstop_build"))
                {
                    TemplateCompleted[templateItem.Key] = templateResponse;
                }

                TemplateWorkItem ignoredTemplate;
                TemplateInflight.TryRemove(templateItem.Key, out ignoredTemplate);
                templateItem.Completion.TrySetResult(templateResponse);
                return;
            }

            TranslationWorkItem translationItem;
            if (!TranslationPending.TryDequeue(out translationItem))
            {
                return;
            }

            TranslationResponse translationResponse;
            try
            {
                translationResponse = translationItem.Operation.StartsWith("arc_", StringComparison.Ordinal)
                    ? ArcAnnotationBuilder.Execute(translationItem.Operation, translationItem.Request)
                    : CadTranslationBuilder.Execute(translationItem.Operation, translationItem.Request);
            }
            catch (System.Exception ex)
            {
                Log.Error("CAD translation request " + translationItem.Key + " failed.", ex);
                translationResponse = TranslationResponse.Failure(
                    translationItem.Request.request_id,
                    translationItem.Operation,
                    ex.Message);
            }

            TranslationWorkItem ignoredTranslation;
            TranslationInflight.TryRemove(translationItem.Key, out ignoredTranslation);
            translationItem.Completion.TrySetResult(translationResponse);
        }

        private static CircleResponse DrawCircle(DrawCircleRequest request)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                document = AcApplication.DocumentManager.Add("");
            }
            if (document == null)
            {
                throw new InvalidOperationException("AutoCAD could not create a default DWG document.");
            }

            string objectHandle;
            using (document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(
                    document.Database.BlockTableId,
                    OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

                using (var circle = new Circle(
                    new Point3d(request.center.x, request.center.y, request.center.z),
                    Vector3d.ZAxis,
                    request.radius))
                {
                    modelSpace.AppendEntity(circle);
                    transaction.AddNewlyCreatedDBObject(circle, true);
                    objectHandle = circle.ObjectId.Handle.ToString();
                }

                transaction.Commit();
            }

            document.Editor.Regen();
            var response = new CircleResponse
            {
                ok = true,
                request_id = request.request_id,
                duplicate = false,
                object_id = objectHandle,
                message = "Circle created in the current DWG.",
                error = null
            };
            Log.Info("Completed request " + request.request_id + "; object handle=" + objectHandle + ".");
            return response;
        }

        private static string Validate(DrawCircleRequest request)
        {
            if (request == null)
            {
                return "Request body is required.";
            }
            if (string.IsNullOrWhiteSpace(request.request_id) || request.request_id.Length > 128)
            {
                return "request_id must contain 1 to 128 characters.";
            }
            if (request.center == null)
            {
                return "center is required.";
            }
            if (!IsFinite(request.center.x) || !IsFinite(request.center.y) || !IsFinite(request.center.z))
            {
                return "Center coordinates must be finite numbers.";
            }
            if (!IsFinite(request.radius) || request.radius <= 0)
            {
                return "radius must be a finite number greater than zero.";
            }
            return null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static JavaScriptSerializer JsonSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = 64 * 1024 * 1024
            };
        }

        private static CircleResponse ErrorResponse(string requestId, string error)
        {
            return new CircleResponse
            {
                ok = false,
                request_id = requestId,
                duplicate = false,
                object_id = null,
                message = null,
                error = error
            };
        }

        private static void TrimCompletedCache()
        {
            while (Completed.Count > MaxCachedResponses)
            {
                string oldest;
                CircleResponse ignored;
                if (!CompletedOrder.TryDequeue(out oldest))
                {
                    return;
                }
                Completed.TryRemove(oldest, out ignored);
            }
        }

        private static void WriteEditorMessage(string message)
        {
            try
            {
                Document document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document != null)
                {
                    document.Editor.WriteMessage(message);
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("Could not write AutoCAD editor message.", ex);
            }
        }
    }

    internal sealed class DrawCircleRequest
    {
        public string request_id { get; set; }
        public PointPayload center { get; set; }
        public double radius { get; set; }
    }

    internal sealed class PointPayload
    {
        public double x { get; set; }
        public double y { get; set; }
        public double z { get; set; }
    }

    internal sealed class CircleResponse
    {
        public bool ok { get; set; }
        public string request_id { get; set; }
        public bool duplicate { get; set; }
        public string object_id { get; set; }
        public string message { get; set; }
        public string error { get; set; }

        public CircleResponse AsDuplicate()
        {
            return new CircleResponse
            {
                ok = ok,
                request_id = request_id,
                duplicate = true,
                object_id = object_id,
                message = message,
                error = error
            };
        }
    }

    internal sealed class DrawWorkItem
    {
        public DrawWorkItem(DrawCircleRequest request)
        {
            Request = request;
            Completion = new TaskCompletionSource<CircleResponse>();
        }

        public DrawCircleRequest Request { get; private set; }
        public TaskCompletionSource<CircleResponse> Completion { get; private set; }
    }

    internal sealed class TemplateWorkItem
    {
        public TemplateWorkItem(string key, string operation, TemplateRequest request)
        {
            Key = key;
            Operation = operation;
            Request = request;
            Completion = new TaskCompletionSource<TemplateResponse>();
        }

        public string Key { get; private set; }
        public string Operation { get; private set; }
        public TemplateRequest Request { get; private set; }
        public TaskCompletionSource<TemplateResponse> Completion { get; private set; }
    }

    internal sealed class TranslationWorkItem
    {
        public TranslationWorkItem(string key, string operation, TranslationRequest request)
        {
            Key = key;
            Operation = operation;
            Request = request;
            Completion = new TaskCompletionSource<TranslationResponse>();
        }

        public string Key { get; private set; }
        public string Operation { get; private set; }
        public TranslationRequest Request { get; private set; }
        public TaskCompletionSource<TranslationResponse> Completion { get; private set; }
    }

    internal sealed class HttpRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string Body { get; set; }
    }

    internal sealed class HttpResult
    {
        public int StatusCode { get; private set; }
        public string Body { get; private set; }

        public static HttpResult Json(int statusCode, object value)
        {
            return new HttpResult
            {
                StatusCode = statusCode,
                Body = new JavaScriptSerializer
                {
                    MaxJsonLength = 64 * 1024 * 1024
                }.Serialize(value)
            };
        }
    }

    internal sealed class LocalHttpServer : IDisposable
    {
        private const int MaxBodyBytes = 64 * 1024 * 1024;
        private readonly TcpListener _listener;
        private readonly Func<HttpRequest, HttpResult> _handler;
        private Thread _thread;
        private volatile bool _stopping;

        public LocalHttpServer(int port, Func<HttpRequest, HttpResult> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _handler = handler;
        }

        public void Start()
        {
            _listener.Start();
            _thread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "CadMcp.Http"
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            _listener.Stop();
            if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(2)))
            {
                Log.Info("HTTP listener thread did not stop within two seconds.");
            }
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClientSafely(client));
                }
                catch (SocketException ex)
                {
                    if (!_stopping)
                    {
                        Log.Error("HTTP listener socket failure.", ex);
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (!_stopping)
                    {
                        throw;
                    }
                }
                catch (System.Exception ex)
                {
                    Log.Error("HTTP request handling failed.", ex);
                }
            }
        }

        private void HandleClientSafely(TcpClient client)
        {
            using (client)
            {
                try
                {
                    HandleClient(client);
                }
                catch (System.Exception ex)
                {
                    Log.Error("HTTP request handling failed.", ex);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            NetworkStream stream = client.GetStream();
            HttpResult result;

            try
            {
                var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
                string requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    throw new InvalidDataException("HTTP request line is missing.");
                }

                string[] parts = requestLine.Split(' ');
                if (parts.Length != 3)
                {
                    throw new InvalidDataException("Malformed HTTP request line.");
                }

                int contentLength = 0;
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                            contentLength < 0 || contentLength > MaxBodyBytes)
                        {
                            throw new InvalidDataException("Invalid Content-Length.");
                        }
                    }
                }

                char[] bodyBuffer = new char[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int read = reader.Read(bodyBuffer, totalRead, contentLength - totalRead);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("HTTP request body ended early.");
                    }
                    totalRead += read;
                }

                result = _handler(new HttpRequest
                {
                    Method = parts[0].ToUpperInvariant(),
                    Path = parts[1].Split('?')[0],
                    Body = new string(bodyBuffer)
                });
            }
            catch (System.Exception ex)
            {
                Log.Error("Rejected malformed HTTP request.", ex);
                result = HttpResult.Json(400, new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", ex.Message }
                });
            }

            WriteResponse(stream, result);
        }

        private static void WriteResponse(Stream stream, HttpResult result)
        {
            byte[] body = Encoding.UTF8.GetBytes(result.Body);
            string reason = result.StatusCode == 200 ? "OK" :
                result.StatusCode == 400 ? "Bad Request" :
                result.StatusCode == 404 ? "Not Found" :
                result.StatusCode == 504 ? "Gateway Timeout" : "Internal Server Error";
            string headers = string.Format(
                CultureInfo.InvariantCulture,
                "HTTP/1.1 {0} {1}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {2}\r\nConnection: close\r\n\r\n",
                result.StatusCode,
                reason,
                body.Length);
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }

    internal static class Log
    {
        private static readonly object Sync = new object();
        public static readonly string Path =
            @"G:\.codex\logs\CAD-MCP\autocad-plugin.log";

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, System.Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, System.Exception exception)
        {
            lock (Sync)
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                Directory.CreateDirectory(directory);
                string line = DateTime.Now.ToString("O", CultureInfo.InvariantCulture) +
                    " [" + level + "] " + message;
                if (exception != null)
                {
                    line += Environment.NewLine + exception;
                }
                File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
    }
}
