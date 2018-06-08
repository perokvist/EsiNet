﻿using System;
using System.IO;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EsiNet.Caching;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace EsiNet.AspNetCore
{
    public class EsiMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly EsiBodyParser _parser;
        private readonly EsiFragmentExecutor _executor;
        private readonly IEsiFragmentCache _cache;

        public EsiMiddleware(
            RequestDelegate next,
            EsiBodyParser parser,
            EsiFragmentExecutor executor,
            IEsiFragmentCache cache)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task Invoke(HttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.Response.StatusCode == 304 || context.Request.Headers.ContainsKey("X-Esi"))
            {
                await _next(context);
                return;
            }

            FragmentPageResponse response;
            var key = context.Request.GetDisplayUrl();
            var (found, cachedResponse) = await _cache.TryGet<FragmentPageResponse>(key);
            if (found)
            {
                response = cachedResponse;
            }
            else
            {
                context.Request.Headers["Accept-Encoding"] = StringValues.Empty;

                var body = await InterceptNext(context);

                var fragment = _parser.Parse(body);
                response = new FragmentPageResponse(fragment, context.Response.ContentType);

                CacheControlHeaderValue.TryParse(
                    context.Response.Headers["Cache-Control"], out var cacheControl);

                if (ShouldSetCache(context))
                {
                    await _cache.Set(key, cacheControl, response);
                }
            }

            var content = await _executor.Execute(response.Fragment);
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength = null;

            foreach (var part in content)
            {
                await context.Response.WriteAsync(part);
            }
        }

        private async Task<string> InterceptNext(HttpContext context)
        {
            var originBody = context.Response.Body;

            try
            {
                using (var newBody = new MemoryStream())
                {
                    context.Response.Body = newBody;

                    await _next(context);

                    newBody.Seek(0, SeekOrigin.Begin);

                    using (var streamReader = new StreamReader(newBody))
                    {
                        return streamReader.ReadToEnd();
                    }
                }
            }
            finally
            {
                context.Response.Body = originBody;
            }
        }

        private static bool ShouldSetCache(HttpContext context)
        {
            return context.Response.StatusCode == 200;
        }
    }
}