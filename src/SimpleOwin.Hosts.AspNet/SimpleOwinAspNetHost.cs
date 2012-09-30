﻿//#define ASPNET_WEBSOCKETS

// https://github.com/prabirshrestha/simple-owin

namespace SimpleOwin.Hosts.AspNet
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
#if ASPNET_WEBSOCKETS
    using System.Net.WebSockets;
#endif
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.Routing;

    using AppFunc = System.Func<System.Collections.Generic.IDictionary<string, object>, System.Threading.Tasks.Task>;

#if ASPNET_WEBSOCKETS

    using WebSocketFunc =
       System.Func<
           System.Collections.Generic.IDictionary<string, object>, // WebSocket Environment
           System.Threading.Tasks.Task>; // Complete

    using WebSocketSendAsync = System.Func<
                System.ArraySegment<byte>, // data
                int, // message type
                bool, // end of message
                System.Threading.CancellationToken, // cancel
                System.Threading.Tasks.Task>;

    using WebSocketReceiveTuple = System.Tuple<
                        int, // messageType
                        bool, // endOfMessage
                        int?, // count
                        int?, // closeStatus
                        string>; // closeStatusDescription

    using WebSocketReceiveAsync = System.Func<
                System.ArraySegment<byte>, // data
                System.Threading.CancellationToken, // cancel
                System.Threading.Tasks.Task<
                    System.Tuple< // WebSocketReceiveTuple
                        int, // messageType
                        bool, // endOfMessage
                        int?, // count
                        int?, // closeStatus
                        string>>>; // closeStatusDescription

    using WebSocketCloseAsync = System.Func<
                int, // closeStatus
                string, // closeDescription
                System.Threading.CancellationToken, // cancel
                System.Threading.Tasks.Task>;

#endif

    public class SimpleOwinAspNetRouteHandler : IRouteHandler
    {
        private readonly SimpleOwinAspNetHandler simpleOwinAspNetHandler;

        public SimpleOwinAspNetRouteHandler(AppFunc app)
            : this(app, null)
        {
        }

        public SimpleOwinAspNetRouteHandler(AppFunc app, string root)
        {
            this.simpleOwinAspNetHandler = new SimpleOwinAspNetHandler(app, root);
        }

        public SimpleOwinAspNetRouteHandler(IEnumerable<Func<AppFunc, AppFunc>> apps)
            : this(apps, null)
        {
        }

        public SimpleOwinAspNetRouteHandler(IEnumerable<Func<AppFunc, AppFunc>> apps, string root)
        {
            this.simpleOwinAspNetHandler = new SimpleOwinAspNetHandler(apps, root);
        }

        public IHttpHandler GetHttpHandler(RequestContext requestContext)
        {
            return simpleOwinAspNetHandler;
        }
    }

    public class SimpleOwinAspNetHandler : IHttpAsyncHandler
    {
        private readonly AppFunc appFunc;
        private readonly string root;

        private static readonly Task CompletedTask;
        private static readonly bool SupportsWebSockets;

        static SimpleOwinAspNetHandler()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.TrySetResult(0);
            CompletedTask = tcs.Task;

            var startup = GetStartupProperties();
            if (Get<string>(startup, OwinConstants.WebSocketVersion, null) == OwinConstants.WebSocketVersion
                && Get<string>(startup, OwinConstants.WebSocketSupportKey, null) == OwinConstants.WebSocketSupport)
            {
                SupportsWebSockets = true;
            }
        }

        public SimpleOwinAspNetHandler(AppFunc app)
            : this(app, null)
        {
        }

        public SimpleOwinAspNetHandler(AppFunc app, string root)
        {
            if (app == null)
            {
                throw new ArgumentNullException("app");
            }

            this.appFunc = app;
            if (!string.IsNullOrWhiteSpace(root))
            {
                if (!root.StartsWith("/"))
                {
                    this.root += "/" + root;
                }
            }
        }

        public SimpleOwinAspNetHandler(IEnumerable<Func<AppFunc, AppFunc>> apps)
            : this(apps, null)
        {
        }

        public SimpleOwinAspNetHandler(IEnumerable<Func<AppFunc, AppFunc>> apps, string root)
            : this(ToOwinApp(apps), root)
        {
        }

        public static AppFunc ToOwinApp(IEnumerable<Func<AppFunc, AppFunc>> apps)
        {
            if (apps == null)
                throw new ArgumentNullException("apps");

            return
                env =>
                {
                    var enumerator = apps.GetEnumerator();
                    AppFunc next = null;
                    next = env2 => enumerator.MoveNext() ? enumerator.Current(env3 => next(env3))(env2) : CompletedTask;
                    return next(env);
                };
        }

        public void ProcessRequest(HttpContext context)
        {
            throw new NotSupportedException();
        }

        public bool IsReusable
        {
            get { return true; }
        }

        public IAsyncResult BeginProcessRequest(HttpContext context, AsyncCallback callback, object state)
        {
            return BeginProcessRequest(new HttpContextWrapper(context), callback, state);
        }

        public IAsyncResult BeginProcessRequest(HttpContextBase context, AsyncCallback callback, object state)
        {
            var tcs = new TaskCompletionSource<Action>(state);
            if (callback != null)
            {
                tcs.Task.ContinueWith(task => callback(task), TaskContinuationOptions.ExecuteSynchronously);
            }

            var request = context.Request;
            var response = context.Response;

            var pathBase = request.ApplicationPath;
            if (pathBase == "/" || pathBase == null)
            {
                pathBase = string.Empty;
            }

            if (root != null)
            {
                pathBase += root;
            }

            var path = request.Path;
            if (path.StartsWith(pathBase))
            {
                path = path.Substring(pathBase.Length);
            }

            var serverVarsToAddToEnv = request.ServerVariables.AllKeys
                .Where(key => !key.StartsWith("HTTP_") && !string.Equals(key, "ALL_HTTP") && !string.Equals(key, "ALL_RAW"))
                .Select(key => new KeyValuePair<string, object>(key, request.ServerVariables.Get(key)));

            var env = new Dictionary<string, object>();
            env[OwinConstants.VersionKey] = OwinConstants.Version;
            env[OwinConstants.RequestMethodKey] = request.HttpMethod;
            env[OwinConstants.RequestSchemeKey] = request.Url.Scheme;
            env[OwinConstants.RequestPathBaseKey] = pathBase;
            env[OwinConstants.RequestPathKey] = path;
            env[OwinConstants.RequestQueryStringKey] = request.ServerVariables["QUERY_STRING"];
            env[OwinConstants.RequestProtocolKey] = request.ServerVariables["SERVER_PROTOCOL"];
            env[OwinConstants.RequestBodyKey] = request.InputStream;
            env[OwinConstants.RequestHeadersKey] = request.Headers.AllKeys
                    .ToDictionary(x => x, x => request.Headers.GetValues(x), StringComparer.OrdinalIgnoreCase);

            env[OwinConstants.CallCancelledKey] = CancellationToken.None;

            env[OwinConstants.ResponseHeadersKey] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            int? responseStatusCode = null;

            env[OwinConstants.ResponseBodyKey] =
                new TriggerStream(response.OutputStream)
                {
                    OnFirstWrite = () =>
                    {
                        responseStatusCode = Get<int>(env, OwinConstants.ResponseStatusCodeKey, 200);
                        response.StatusCode = responseStatusCode.Value;

                        object reasonPhrase;
                        if (env.TryGetValue(OwinConstants.ResponseReasonPhraseKey, out reasonPhrase))
                            response.StatusDescription = Convert.ToString(reasonPhrase);

                        var responseHeaders = Get<IDictionary<string, string[]>>(env, OwinConstants.ResponseHeadersKey, null);
                        if (responseHeaders != null)
                        {
                            foreach (var responseHeader in responseHeaders)
                            {
                                foreach (var headerValue in responseHeader.Value)
                                    response.AddHeader(responseHeader.Key, headerValue);
                            }
                        }
                    }
                };

            SetEnvironmentServerVariables(env, serverVarsToAddToEnv);

            env[OwinConstants.HttpContextBaseKey] = context;

#if ASPNET_WEBSOCKETS
            if (context.IsWebSocketRequest)
            {
                env[OwinConstants.WebSocketVersionKey] = OwinConstants.WebSocketVersion;
                env[OwinConstants.WebSocketSupportKey] = OwinConstants.WebSocketSupport;
            }
#endif

            response.BufferOutput = false;

            try
            {
                appFunc(env)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            tcs.TrySetException(t.Exception.InnerExceptions);
                        }
                        else if (t.IsCanceled)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
#if ASPNET_WEBSOCKETS
                            object tempWsBodyDelegate;

                            if (responseStatusCode == null)
                            {
                                responseStatusCode = Get<int>(env, OwinConstants.ResponseStatusCodeKey, 200);
                            }

                            if (responseStatusCode.Value == 101 &&
                                env.TryGetValue(OwinConstants.WebSocketFuncKey, out tempWsBodyDelegate) &&
                                tempWsBodyDelegate != null)
                            {
                                var wsBodyDelegate = (WebSocketFunc)tempWsBodyDelegate;
                                context.AcceptWebSocketRequest(async websocketContext => // todo: AcceptWebSocketRequest throws error
                                {
                                    var webSocket = websocketContext.WebSocket;

                                    var wsEnv = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                    wsEnv[OwinConstants.WebSocketSendAsyncFuncKey] = WebSocketSendAsync(webSocket);
                                    wsEnv[OwinConstants.WebSocketReceiveAsyncFuncKey] = WebSocketReceiveAsync(webSocket);
                                    wsEnv[OwinConstants.WebSocketCloseAsyncFuncKey] = WebSocketCloseAsync(webSocket);
                                    wsEnv[OwinConstants.WebSocketVersionKey] = OwinConstants.WebSocketVersion;
                                    wsEnv[OwinConstants.WebSocketCallCancelledKey] = CancellationToken.None;
                                    wsEnv[OwinConstants.AspNetWebSocketContextKey] = websocketContext;

                                    await wsBodyDelegate(wsEnv);

                                    switch (webSocket.State)
                                    {
                                        case WebSocketState.Closed:  // closed gracefully, no action needed
                                        case WebSocketState.Aborted: // closed abortively, no action needed
                                            break;
                                        case WebSocketState.CloseReceived:
                                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                                            break;
                                        case WebSocketState.Open:
                                        case WebSocketState.CloseSent: // No close received, abort so we don't have to drain the pipe.
                                            websocketContext.WebSocket.Abort();
                                            break;
                                        default:
                                            throw new ArgumentOutOfRangeException("state", webSocket.State, string.Empty);
                                    }

                                    response.Close();
                                });
                            }
#endif
                            tcs.TrySetResult(() => { });
                        }

                    });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        public void EndProcessRequest(IAsyncResult asyncResult)
        {
            var task = ((Task<Action>)asyncResult);
            if (task.IsFaulted)
            {
                var exception = task.Exception;
                exception.Handle(ex => ex is HttpException);
            }
            else if (task.IsCompleted)
            {
                task.Result.Invoke();
            }
        }

        [DebuggerStepThrough]
        private static void SetEnvironmentServerVariables(Dictionary<string, object> env, IEnumerable<KeyValuePair<string, object>> serverVarsToAddToEnv)
        {
            foreach (var kv in serverVarsToAddToEnv)
                env["server." + kv.Key] = kv.Value;
        }

        private static T Get<T>(IDictionary<string, object> env, string key, T defaultValue)
        {
            object value;
            return env.TryGetValue(key, out value) && value is T ? (T)value : defaultValue;
        }

        public static IDictionary<string, object> GetStartupProperties()
        {
            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            properties[OwinConstants.VersionKey] = OwinConstants.Version;
#if ASPNET_WEBSOCKETS
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version >= new Version(6, 2) || HttpRuntime.IISVersion != null && HttpRuntime.IISVersion.Major >= 8)
            {
                properties[OwinConstants.WebSocketVersionKey] = OwinConstants.WebSocketVersion;
                properties[OwinConstants.WebSocketSupportKey] = OwinConstants.WebSocketSupport;
            }
#endif
            return properties;
        }

#if ASPNET_WEBSOCKETS

        private static WebSocketSendAsync WebSocketSendAsync(WebSocket webSocket)
        {
            return (buffer, messageType, endOfMessage, cancel) =>
                webSocket.SendAsync(buffer, OpCodeToEnum(messageType), endOfMessage, cancel);
        }

        private static WebSocketReceiveAsync WebSocketReceiveAsync(WebSocket webSocket)
        {
            return async (buffer, cancel) =>
            {
                var nativeResult = await webSocket.ReceiveAsync(buffer, cancel);
                return new WebSocketReceiveTuple(
                    EnumToOpCode(nativeResult.MessageType),
                    nativeResult.EndOfMessage,
                    (nativeResult.MessageType == WebSocketMessageType.Close ? null : (int?)nativeResult.Count),
                    (int?)nativeResult.CloseStatus,
                    nativeResult.CloseStatusDescription);
            };
        }

        private static WebSocketCloseAsync WebSocketCloseAsync(WebSocket webSocket)
        {
            return (status, description, cancel) =>
                webSocket.CloseOutputAsync((WebSocketCloseStatus)status, description, cancel);
        }

        private static WebSocketMessageType OpCodeToEnum(int messageType)
        {
            switch (messageType)
            {
                case 0x1:
                    return WebSocketMessageType.Text;
                    break;
                case 0x2:
                    return WebSocketMessageType.Binary;
                    break;
                case 0x8:
                    return WebSocketMessageType.Close;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("messageType", messageType, string.Empty);
            }
        }

        private static int EnumToOpCode(WebSocketMessageType webSocketMessageType)
        {
            switch (webSocketMessageType)
            {
                case WebSocketMessageType.Text:
                    return 0x1;
                    break;
                case WebSocketMessageType.Binary:
                    return 0x2;
                    break;
                case WebSocketMessageType.Close:
                    return 0x8;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("webSocketMessageType", webSocketMessageType, string.Empty);
            }
        }

#endif

        /// <remarks>TriggerStream pulled from Gate source code.</remarks>
        private class TriggerStream : Stream
        {
            public TriggerStream(Stream innerStream)
            {
                InnerStream = innerStream;
            }

            public Stream InnerStream { get; set; }

            public Action OnFirstWrite { get; set; }

            private bool IsStarted { get; set; }

            public override bool CanRead
            {
                get { return InnerStream.CanRead; }
            }

            public override bool CanWrite
            {
                get { return InnerStream.CanWrite; }
            }

            public override bool CanSeek
            {
                get { return InnerStream.CanSeek; }
            }

            public override bool CanTimeout
            {
                get { return InnerStream.CanTimeout; }
            }

            public override int WriteTimeout
            {
                get { return InnerStream.WriteTimeout; }
                set { InnerStream.WriteTimeout = value; }
            }

            public override int ReadTimeout
            {
                get { return InnerStream.ReadTimeout; }
                set { InnerStream.ReadTimeout = value; }
            }

            public override long Position
            {
                get { return InnerStream.Position; }
                set { InnerStream.Position = value; }
            }

            public override long Length
            {
                get { return InnerStream.Length; }
            }

            public override void Close()
            {
                InnerStream.Close();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    InnerStream.Dispose();
                }
            }

            public override string ToString()
            {
                return InnerStream.ToString();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return InnerStream.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                InnerStream.SetLength(value);
            }

            public override int ReadByte()
            {
                return InnerStream.ReadByte();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return InnerStream.Read(buffer, offset, count);
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return InnerStream.BeginRead(buffer, offset, count, callback, state);
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                return InnerStream.EndRead(asyncResult);
            }

            public override void WriteByte(byte value)
            {
                Start();
                InnerStream.WriteByte(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Start();
                InnerStream.Write(buffer, offset, count);
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                Start();
                return InnerStream.BeginWrite(buffer, offset, count, callback, state);
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                InnerStream.EndWrite(asyncResult);
            }

            public override void Flush()
            {
                Start();
                InnerStream.Flush();
            }

            private void Start()
            {
                if (!IsStarted)
                {
                    IsStarted = true;
                    if (OnFirstWrite != null)
                    {
                        OnFirstWrite();
                    }
                }
            }
        }

        private static class OwinConstants
        {
            public const string Version = "1.0";
            public const string VersionKey = "owin.Version";
            public const string RequestMethodKey = "owin.RequestMethod";
            public const string RequestSchemeKey = "owin.RequestScheme";
            public const string RequestPathBaseKey = "owin.RequestPathBase";
            public const string RequestPathKey = "owin.RequestPath";
            public const string RequestQueryStringKey = "owin.RequestQueryString";
            public const string RequestProtocolKey = "owin.RequestProtocol";
            public const string RequestBodyKey = "owin.RequestBody";
            public const string RequestHeadersKey = "owin.RequestHeaders";

            public const string CallCancelledKey = "owin.CallCancelled";

            public const string ResponseHeadersKey = "owin.ResponseHeaders";
            public const string ResponseBodyKey = "owin.ResponseBody";
            public const string ResponseStatusCodeKey = "owin.ResponseStatusCode";
            public const string ResponseReasonPhraseKey = "owin.ResponseReasonPhrase";

            public const string HttpContextBaseKey = "System.Web.HttpContextBase";

            public const string WebSocketVersion = "1.0";
            public const string WebSocketSupport = "WebSocketFunc";
            public const string WebSocketVersionKey = "websocket.Version";
            public const string WebSocketSupportKey = "websocket.Support";
            public const string WebSocketFuncKey = "websocket.Func";
            public const string WebSocketSendAsyncFuncKey = "websocket.SendAsyncFunc";
            public const string WebSocketReceiveAsyncFuncKey = "websocket.ReceiveAsyncFunc";
            public const string WebSocketCloseAsyncFuncKey = "websocket.CloseAsyncFunc";
            public const string WebSocketCallCancelledKey = "websocket.CallCancelled";

            public const string AspNetWebSocketContextKey = "aspnet.AspNetWebSocketContext";
        }
    }
}
