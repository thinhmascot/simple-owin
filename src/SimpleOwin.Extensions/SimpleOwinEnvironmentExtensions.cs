﻿namespace SimpleOwin.Extensions
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class SimpleOwinEnvironmentExtensions
    {
        public static IDictionary<string, string[]> GetOwinResponseHeaders(this IDictionary<string, object> env)
        {
            return env.GetOwinEnvironmentValue<IDictionary<string, string[]>>("owin.ResponseHeaders");
        }

        public static IDictionary<string, object> GetOwinResponseHeaders(this IDictionary<string, object> env, Action<IDictionary<string, string[]>> callback)
        {
            var headers = env.GetOwinResponseHeaders();
            if (callback != null)
                callback(headers);
            return env;
        }

        public static Stream GetOwinResponseBody(this IDictionary<string, object> env)
        {
            return env.GetOwinEnvironmentValue<Stream>("owin.ResponseBody");
        }

        public static IDictionary<string, object> SetOwinResponseStatusCode(this IDictionary<string, object> env, int statusCode)
        {
            env["owin.ResponseStatusCode"] = statusCode;
            return env;
        }
    }
}